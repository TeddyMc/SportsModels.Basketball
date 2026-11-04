using System.Net;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Infrastructure;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class DataAcquisitionTests
{
    private string root = "";
    private static readonly DateOnly Date = new(2021, 10, 19);
    private static readonly NbaGame Game = new("0022100001", Date, "BKN", "MIL");

    [SetUp]
    public void SetUp() => root = Path.Combine(Path.GetTempPath(), "nba-unified-acquisition-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void TearDown() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    [Test]
    public async Task OneInvocationAcquiresFourResourcesAndRerunMakesNoNetworkRequests()
    {
        var source = new Source();
        var service = Service(source);
        var first = await service.RunAsync(Request());
        var second = await service.RunAsync(Request());

        Assert.Multiple(() =>
        {
            Assert.That(first.Complete, Is.True);
            Assert.That(first.ExpectedResources, Is.EqualTo(4));
            Assert.That(first.Downloaded, Is.EqualTo(4));
            Assert.That(second.Complete, Is.True);
            Assert.That(second.Skipped, Is.EqualTo(4));
            Assert.That(source.DiscoverCalls, Is.EqualTo(1));
            Assert.That(source.Calls, Has.Count.EqualTo(4));
            Assert.That(File.ReadAllBytes(Path.Combine(root, "stats", "playbyplayv3", Game.FileName)), Is.EqualTo(Payload("playbyplayv3")));
        });
    }

    [Test]
    public async Task TimeoutBurstTriggersCooldownThenRetriesSameResourceSuccessfully()
    {
        var source = new Source { Fail = (_, count) => count <= 3 ? new TaskCanceledException("timeout") : null };
        var delay = new Delay();
        var result = await Service(source, delay).RunAsync(Request() with { AttemptsPerResource = 4 });

        Assert.That(result.Complete, Is.True);
        Assert.That(delay.Delays, Does.Contain(TimeSpan.FromMinutes(30)));
        Assert.That(delay.Delays.Count(t => t == TimeSpan.FromMinutes(30)), Is.EqualTo(4));
        Assert.That(source.Calls.Count, Is.EqualTo(16));
        Assert.That(Directory.GetFiles(Path.Combine(root, "_acquisition", "runs"), "*.jsonl").Select(File.ReadAllText).Single(), Does.Contain("Cooldown"));
    }

    [Test]
    public async Task RecoveryBudgetExhaustionHaltsWithoutAdvertisingUnattemptedResourcesAsComplete()
    {
        var source = new Source { Fail = (_, _) => new HttpRequestException("blocked", null, HttpStatusCode.Forbidden) };
        var result = await Service(source).RunAsync(Request() with { ConsecutiveFailureLimit = 1, MaximumCooldowns = 1 });

        Assert.That(result.Halted, Is.True);
        Assert.That(result.Complete, Is.False);
        Assert.That(result.ExpectedResources, Is.EqualTo(4));
        Assert.That(result.Downloaded, Is.Zero);
        Assert.That(source.Calls, Has.Count.EqualTo(2));
        Assert.That(File.Exists(result.ReportPath), Is.True);
    }

    [Test]
    public async Task PermanentFailureDoesNotRetryAndOtherResourcesStillDownload()
    {
        var source = new Source { Fail = (endpoint, _) => endpoint == "liveboxscore"
            ? new HttpRequestException("not found", null, HttpStatusCode.NotFound) : null };
        var result = await Service(source).RunAsync(Request());

        Assert.That(result.Complete, Is.False);
        Assert.That(result.Downloaded, Is.EqualTo(3));
        Assert.That(result.Failures.Single().Resource, Does.EndWith("liveboxscore"));
        Assert.That(source.Calls, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task FailedDiscoveryDateIsNotACompleteEmptyDayAndCanBeRetriedOnRerun()
    {
        var source = new Source { DiscoveryFailure = new HttpRequestException("bad request", null, HttpStatusCode.BadRequest) };
        var first = await Service(source).RunAsync(Request());
        Assert.That(first.Complete, Is.False);
        Assert.That(first.DiscoveredDates, Is.Zero);
        Assert.That(first.ExpectedResources, Is.Zero);

        source.DiscoveryFailure = null;
        var second = await Service(source).RunAsync(Request());
        Assert.That(second.Complete, Is.True);
    }

    [Test]
    public async Task EmptyDiscoveryDayIsVerifiedAndCached()
    {
        var source = new Source { Games = [] };
        var service = Service(source);
        Assert.That((await service.RunAsync(Request())).Complete, Is.True);
        Assert.That((await service.RunAsync(Request())).Complete, Is.True);
        Assert.That(source.DiscoverCalls, Is.EqualTo(1));
        Assert.That(source.Calls, Is.Empty);
    }

    [TestCase("playbyplayv3")]
    [TestCase("liveplaybyplay")]
    [TestCase("boxscoretraditionalv3")]
    public async Task CorruptRetainedResourceIsReportedAndNeverOverwrittenOrRefetched(string endpoint)
    {
        var source = new Source();
        var service = Service(source);
        await service.RunAsync(Request());
        var path = endpoint == "playbyplayv3" ? Path.Combine(root, "stats", "playbyplayv3", Game.FileName)
            : Directory.GetFiles(Path.Combine(root, "references", endpoint, Game.GameId), "*.json").Single(p => !p.EndsWith(".retrieval.json"));
        var corrupted = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Payload(endpoint)) + " ");
        await File.WriteAllBytesAsync(path, corrupted);
        var result = await service.RunAsync(Request());

        Assert.That(result.Complete, Is.False);
        Assert.That(result.Failures, Has.Count.EqualTo(1));
        Assert.That(result.Skipped, Is.EqualTo(3));
        Assert.That(source.Calls, Has.Count.EqualTo(4));
        Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(corrupted));
    }

    [Test]
    public async Task CorruptDiscoveryCachePreventsFalseCompletenessAndDoesNotRefetchOverIt()
    {
        var source = new Source();
        var service = Service(source);
        await service.RunAsync(Request());
        var path = Directory.GetFiles(Path.Combine(root, "_acquisition", "discovery")).Single();
        await File.AppendAllTextAsync(path, " ");
        var result = await service.RunAsync(Request());

        Assert.That(result.Complete, Is.False);
        Assert.That(result.DiscoveredDates, Is.Zero);
        Assert.That(source.DiscoverCalls, Is.EqualTo(1));
    }

    [Test]
    public async Task InvalidDownloadedIdentityIsNotStoredOrReportedAsSuccess()
    {
        var source = new Source { WrongIdentity = true };
        var result = await Service(source).RunAsync(Request());
        Assert.That(result.Complete, Is.False);
        Assert.That(result.Downloaded, Is.Zero);
        Assert.That(result.Failures, Has.Count.EqualTo(4));
        Assert.That(Directory.Exists(Path.Combine(root, "stats")), Is.False);
    }

    [Test]
    public void CancellationDuringCooldownPropagatesAndRecordsResumableState()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new Source { Fail = (_, _) => new TaskCanceledException("timeout") };
        var delay = new Delay { OnWait = duration => { if (duration == TimeSpan.FromMinutes(30)) { cancellation.Cancel(); } } };
        Assert.ThrowsAsync<OperationCanceledException>(() => Service(source, delay).RunAsync(Request(), cancellation.Token));
        Assert.That(Directory.GetFiles(Path.Combine(root, "_acquisition", "runs"), "*.jsonl").Select(File.ReadAllText).Single(), Does.Contain("Cancelled"));
    }

    [Test]
    public void InvalidOptionsFailBeforeNetworkAccess()
    {
        var source = new Source();
        Assert.ThrowsAsync<ArgumentException>(() => Service(source).RunAsync(Request() with { MaximumCooldowns = -1 }));
        Assert.That(source.DiscoverCalls, Is.Zero);
    }

    private NbaDataAcquisition Service(Source source, Delay? delay = null) => new(source, source,
        new AcquisitionWorkspace(new RawJsonStore(), new ReferenceStore()), delay ?? new Delay());

    private DataAcquisitionRequest Request() => new(Date, Date, root);

    private static byte[] Payload(string endpoint)
    {
        var game = endpoint is "playbyplayv3" or "liveplaybyplay"
            ? new { gameId = Game.GameId, actions = new[] { new { period = 1 } } } as object
            : new { gameId = Game.GameId, homeTeam = new { players = new[] { new { personId = 1 } }, statistics = new { points = 0 } },
                awayTeam = new { players = new[] { new { personId = 2 } }, statistics = new { points = 0 } } };
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
            { [endpoint == "boxscoretraditionalv3" ? "boxScoreTraditional" : "game"] = game });
    }

    private sealed class Delay : IAcquisitionDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public Action<TimeSpan>? OnWait { get; init; }
        public Task WaitAsync(TimeSpan delay, CancellationToken token)
        {
            Delays.Add(delay);
            OnWait?.Invoke(delay);
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class Source : INbaSource, INbaReferenceSource
    {
        public int DiscoverCalls { get; private set; }
        public IReadOnlyList<NbaGame> Games { get; init; } = [Game];
        public Exception? DiscoveryFailure { get; set; }
        public Func<string, int, Exception?>? Fail { get; init; }
        public bool WrongIdentity { get; init; }
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<NbaGame>> DiscoverAsync(DateOnly date, CancellationToken token)
        {
            DiscoverCalls++;
            if (DiscoveryFailure is not null) { throw DiscoveryFailure; }
            return Task.FromResult(Games);
        }

        public Task<byte[]> DownloadAsync(string gameId, CancellationToken token) => DownloadAsync("playbyplayv3", gameId, 0, token);

        public Task<byte[]> DownloadAsync(string endpoint, string gameId, int period, CancellationToken token)
        {
            Calls.Add(endpoint);
            var error = Fail?.Invoke(endpoint, Calls.Count(e => e == endpoint));
            if (error is not null) { throw error; }
            var bytes = Payload(endpoint);
            return Task.FromResult(WrongIdentity ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(Game.GameId, "0022109999")) : bytes);
        }
    }
}
