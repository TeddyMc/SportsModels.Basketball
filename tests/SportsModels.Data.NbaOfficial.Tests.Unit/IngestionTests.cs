using System.Net;
using System.Text;
using Autofac;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Autofac;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Infrastructure;
using SportsModels.Data.NbaOfficial.Definitions;
using NUnit.Framework;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class IngestionTests
{
    private string directory = null!;
    private static readonly DateOnly Start = new(2024, 1, 1);
    private const string GameId = "0022300001";
    private static readonly NbaGame SampleGame = new(GameId, Start, "BOS", "NYK");

    [SetUp]
    public void SetUp() => directory = Path.Combine(Path.GetTempPath(), "SportsModels.Data.NbaOfficial-tests-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task InclusiveWeek_DeduplicatesFinalGames_PreservesBytes_AndSkipsOnRerun()
    {
        var raw = Encoding.UTF8.GetBytes(" {\n  \"message\": \"café\", \"actions\": []\n}\r\n");
        var requested = new List<Uri>();
        using var client = CreateClient((request, _) =>
        {
            var uri = request.RequestUri!;
            requested.Add(uri);

            return Task.FromResult(uri.AbsolutePath == "/stats/scoreboardv3"
                ? Json(Schedule($"[\"{GameId}\",3],[\"{GameId}\",3],[\"0022300002\",2]"))
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(raw) });
        });
        var service = Service(client);
        var request = new IngestionRequest(Start, Start.AddDays(6), directory);

        var first = await service.RunAsync(request);
        var second = await service.RunAsync(request);

        Assert.Multiple(() =>
        {
            Assert.That(first.Discovered, Is.EqualTo(1));
            Assert.That(first.Downloaded, Is.EqualTo(1));
            Assert.That(first.Failed, Is.Zero);
            Assert.That(second.Skipped, Is.EqualTo(1));
            Assert.That(second.Downloaded, Is.Zero);
            Assert.That(second.Failed, Is.Zero);
            Assert.That(File.ReadAllBytes(Path.Combine(directory, SampleGame.FileName)), Is.EqualTo(raw));
            Assert.That(requested.Count(uri => uri.AbsolutePath == "/stats/scoreboardv3"), Is.EqualTo(14));
            Assert.That(requested.Count(uri => uri.AbsolutePath == "/stats/playbyplayv3"), Is.EqualTo(1));
            Assert.That(Uri.UnescapeDataString(requested[0].Query), Does.Contain("GameDate=2024-01-01"));
            Assert.That(Uri.UnescapeDataString(requested[6].Query), Does.Contain("GameDate=2024-01-07"));
            Assert.That(requested[7].AbsolutePath, Is.EqualTo("/stats/playbyplayv3"));
            Assert.That(requested[7].Host, Is.EqualTo("stats.nba.com"));
            Assert.That(requested[7].Query, Is.EqualTo($"?GameID={GameId}&StartPeriod=0&EndPeriod=0"));
            Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
        });
    }

    [TestCase(403)]
    [TestCase(404)]
    [TestCase(429)]
    [TestCase(500)]
    public async Task HttpFailure_IsCounted_AndLaterGameStillDownloads(int status)
    {
        using var client = CreateClient((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/stats/scoreboardv3"
            ? Json(Schedule($"[\"{GameId}\",3],[\"0022300002\",3]"))
            : request.RequestUri.Query.Contains(GameId)
                ? new HttpResponseMessage((HttpStatusCode)status)
                : Json("{}")));

        var result = await Service(client).RunAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.Discovered, Is.EqualTo(2));
            Assert.That(result.Downloaded, Is.EqualTo(1));
            Assert.That(result.Failed, Is.EqualTo(1));
            Assert.That(result.Failures[0].Kind, Is.EqualTo(FailureKind.Http));
            Assert.That(File.Exists(Path.Combine(directory, SampleGame.FileName)), Is.False);
        });
    }

    [Test]
    public async Task StatsRequests_UseBrowserHeaders_ForDiscoveryAndPlayByPlay()
    {
        using var client = CreateClient((request, _) =>
        {
            var isStats = request.RequestUri!.Host == "stats.nba.com";

            Assert.Multiple(() =>
            {
                Assert.That(request.Headers.Referrer!.AbsoluteUri, Is.EqualTo("https://www.nba.com/"));
                Assert.That(request.Headers.Contains("Origin"), Is.EqualTo(isStats));
                Assert.That(request.Headers.Contains("Sec-Fetch-Mode"), Is.EqualTo(isStats));
                Assert.That(request.Headers.Contains("sec-ch-ua"), Is.EqualTo(isStats));
            });

            if (isStats)
            {
                Assert.Multiple(() =>
                {
                    Assert.That(request.Headers.GetValues("Origin").Single(), Is.EqualTo("https://www.nba.com"));
                    Assert.That(request.Headers.GetValues("Sec-Fetch-Mode").Single(), Is.EqualTo("cors"));
                    Assert.That(request.Headers.UserAgent.ToString(), Does.Contain("Chrome/155.0.0.0"));
                    Assert.That(request.Headers.Accept.ToString(), Is.EqualTo("*/*"));
                    Assert.That(request.Headers.AcceptLanguage.ToString(), Does.Contain("en-IE"));
                });
            }

            return Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/stats/scoreboardv3" ? Schedule($"[\"{GameId}\",3]") : "{}"));
        });

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Downloaded, Is.EqualTo(1));
        Assert.That(result.Failed, Is.Zero);
    }

    [Test]
    public async Task TransportFailure_IsCounted()
    {
        using var client = CreateClient((_, _) => throw new HttpRequestException("Connection failed"));

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Http));
    }

    [Test]
    public async Task Timeout_IsCounted()
    {
        using var client = CreateClient(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        client.Timeout = TimeSpan.FromMilliseconds(50);

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Timeout));
    }

    [Test]
    public async Task DownloadTimeout_IsCountedAgainstGameId()
    {
        using var client = CreateClient(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/stats/scoreboardv3")
            {
                return Json(Schedule($"[\"{GameId}\",3]"));
            }

            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });
        client.Timeout = TimeSpan.FromMilliseconds(50);

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Timeout));
        Assert.That(result.Failures[0].Resource, Is.EqualTo(GameId));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [Test]
    public void CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var client = CreateClient(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Json("{}");
        });

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await Service(client).RunAsync(Request(), cancellation.Token));
    }

    [Test]
    public async Task InvalidDownloadedJson_IsNotStored()
    {
        using var client = OneGameClient("<html>not JSON</html>");

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.InvalidJson));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [Test]
    public async Task InvalidExistingFile_IsPreserved_AndNotDownloaded()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, SampleGame.FileName);
        await File.WriteAllTextAsync(path, "broken");
        var downloads = 0;
        using var client = CreateClient((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/stats/playbyplayv3")
            {
                downloads++;
            }

            return Task.FromResult(Json(Schedule($"[\"{GameId}\",3]")));
        });

        var result = await Service(client).RunAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.InvalidJson));
            Assert.That(File.ReadAllText(path), Is.EqualTo("broken"));
            Assert.That(downloads, Is.Zero);
            Assert.That(result.Skipped, Is.Zero);
        });
    }

    [TestCase("{}", FailureKind.Discovery)]
    [TestCase("{", FailureKind.InvalidJson)]
    [TestCase("{\"resultSets\":[]}", FailureKind.Discovery)]
    [TestCase("{\"resultSets\":[{\"name\":\"GameHeader\",\"headers\":[],\"rowSet\":[]}]}", FailureKind.Discovery)]
    public async Task DiscoveryFailure_IsCounted_AndNextDateStillRuns(string body, FailureKind kind)
    {
        var calls = 0;
        using var client = CreateClient((_, _) => Task.FromResult(Json(++calls == 1 ? body : Schedule(""))));

        var result = await Service(client).RunAsync(new IngestionRequest(Start, Start.AddDays(1), directory));

        Assert.That(calls, Is.EqualTo(2));
        Assert.That(result.Failures.Single().Kind, Is.EqualTo(kind));
        Assert.That(result.Failures[0].Resource, Is.EqualTo("2024-01-01"));
    }

    [TestCase("../escape")]
    [TestCase("123")]
    [TestCase("abcdefghij")]
    public async Task UnsafeGameId_IsRejected(string id)
    {
        using var client = CreateClient((_, _) => Task.FromResult(Json(Schedule($"[\"{id}\",3]"))));

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Discovery));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [Test]
    public async Task EmptySchedule_IsSuccessful()
    {
        using var client = CreateClient((_, _) => Task.FromResult(Json(Schedule(""))));

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Discovered, Is.Zero);
        Assert.That(result.Failed, Is.Zero);
    }

    [Test]
    public void InvalidInputs_AreRejectedBeforeHttp()
    {
        using var client = CreateClient((_, _) => throw new AssertionException("No HTTP expected"));
        var service = Service(client);

        Assert.ThrowsAsync<ArgumentException>(async () => await service.RunAsync(new(Start.AddDays(1), Start, directory)));
        Assert.ThrowsAsync<ArgumentException>(async () => await service.RunAsync(new(Start, DateOnly.FromDateTime(DateTime.UtcNow), directory)));
        Assert.ThrowsAsync<ArgumentException>(async () => await service.RunAsync(new(Start, Start, " ")));
    }

    [Test]
    public async Task StorageFailure_IsCounted()
    {
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "a-file");
        await File.WriteAllTextAsync(output, "occupied");
        using var client = OneGameClient("{}");

        var result = await Service(client).RunAsync(new(Start, Start, output));

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Storage));
        Assert.That(File.ReadAllText(output), Is.EqualTo("occupied"));
    }

    [Test]
    public async Task StoreNeverOverwrites_AndCleansTemporaryFile()
    {
        var store = new RawJsonStore();
        await store.SaveAsync(directory, SampleGame, "{}"u8.ToArray(), default);

        Assert.ThrowsAsync<IOException>(async () =>
            await store.SaveAsync(directory, SampleGame, "[]"u8.ToArray(), default));

        Assert.That(await File.ReadAllTextAsync(Path.Combine(directory, SampleGame.FileName)), Is.EqualTo("{}"));
        Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ScoreboardV3_PreservesEachMatchup_AndIgnoresNonFinalGames()
    {
        const string scoreboard = """
            {"scoreboard":{"games":[
              {"gameId":"0022500713","gameStatus":3,"awayTeam":{"teamTricode":"HOU"},"homeTeam":{"teamTricode":"IND"}},
              {"gameId":"0022500715","gameStatus":3,"awayTeam":{"teamTricode":"PHI"},"homeTeam":{"teamTricode":"LAC"}},
              {"gameId":"0022500716","gameStatus":1}
            ]}}
            """;
        using var client = CreateClient((_, _) => Task.FromResult(Json(scoreboard)));
        var source = new NbaHttpSource(client);

        var games = await source.DiscoverAsync(new DateOnly(2026, 2, 2), default);

        Assert.That(games.Select(game => game.FileName), Is.EquivalentTo(new[]
        {
            "HOU@IND_20260202_0022500713.json", "PHI@LAC_20260202_0022500715.json"
        }));
    }

    [Test]
    public void FileName_UsesAwayHomeDateAndStableId()
    {
        Assert.That(new NbaGame("0022500001", new DateOnly(2026, 2, 2), "BOS", "NYK").FileName,
            Is.EqualTo("BOS@NYK_20260202_0022500001.json"));
    }

    [TestCase("../BOS", "NYK")]
    [TestCase("BOS", "NY/K")]
    [TestCase("bos", "NYK")]
    [TestCase("BOS", "BOS")]
    public void FileName_RejectsUnsafeOrAmbiguousTeams(string away, string home)
    {
        Assert.Throws<InvalidDataException>(() => _ = new NbaGame(GameId, Start, away, home).FileName);
    }

    [TestCase("homeTeam", "missingHome")]
    [TestCase("teamTricode", "missingCode")]
    [TestCase("NYK", "../NYK")]
    public async Task InvalidTeamMetadata_IsReportedBeforeSaving(string original, string replacement)
    {
        using var client = CreateClient((_, _) => Task.FromResult(Json(Schedule($"[\"{GameId}\",3]").Replace(original, replacement))));

        var result = await Service(client).RunAsync(Request());

        Assert.That(result.Failures.Single().Kind, Is.EqualTo(FailureKind.Discovery));
        Assert.That(Directory.Exists(directory), Is.False);
    }

    [Test]
    public void AutofacResolvesPublicEntryPoint()
    {
        var builder = new ContainerBuilder();
        builder.RegisterModule<PlayByPlayModule>();

        using var container = builder.Build();

        Assert.That(container.Resolve<IPlayByPlayIngestion>(), Is.Not.Null);
    }

    private IngestionRequest Request() => new(Start, Start, directory);

    private static PlayByPlayIngestion Service(HttpClient client) => new(new NbaHttpSource(client), new RawJsonStore());

    private static string Schedule(string rows)
    {
        using var document = System.Text.Json.JsonDocument.Parse("[" + rows + "]");
        var games = document.RootElement.EnumerateArray().Select(row => new
        {
            gameId = row[0].GetString(),
            gameStatus = row[1].GetInt32(),
            homeTeam = new { teamTricode = "NYK" },
            awayTeam = new { teamTricode = "BOS" }
        }).ToArray();

        return System.Text.Json.JsonSerializer.Serialize(new { scoreboard = new { games } });
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpClient OneGameClient(string body) => CreateClient((request, _) =>
        Task.FromResult(Json(request.RequestUri!.AbsolutePath == "/stats/scoreboardv3" ? Schedule($"[\"{GameId}\",3]") : body)));

    private static HttpClient CreateClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new StubHandler(respond));

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
