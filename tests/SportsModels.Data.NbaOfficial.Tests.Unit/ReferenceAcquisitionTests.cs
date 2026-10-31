using System.Text;
using NUnit.Framework;
using SportsModels.Data.NbaOfficial.Application;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Infrastructure;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class ReferenceAcquisitionTests
{
    [Test]
    public async Task DateSelectionCanAcquireOneEndpointWithoutLocalOrDownloadedPlayByPlay()
    {
        var source = new Source();
        var store = new Store();
        var discovery = new Discovery();
        var service = new ReferenceAcquisition(source, store, discovery, new LocalGameInventoryReader());
        var result = await service.RunAsync(new("missing-input", "unused-output")
        {
            StartDate = new(2025, 12, 1), EndDate = new(2025, 12, 1), Endpoints = ["boxscoresummaryv2"]
        });
        Assert.That(discovery.Dates, Is.EqualTo(new[] { new DateOnly(2025, 12, 1) }));
        Assert.That(source.Calls, Is.EqualTo(new[] { ("boxscoresummaryv2", "0022500315", 0) }));
        Assert.That(result.Downloaded, Is.EqualTo(1));
        Assert.That(result.Failed, Is.Zero);
    }

    [Test]
    public void EndpointAndDateErrorsFailBeforeAnyAcquisition()
    {
        var source = new Source();
        var service = new ReferenceAcquisition(source, new Store(), new Discovery(), new LocalGameInventoryReader());
        Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(new("missing", "unused") { Endpoints = ["unapproved"] }));
        Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(new("missing", "unused") { StartDate = new(2025, 1, 1) }));
        Assert.That(source.Calls, Is.Empty);
    }

    [Test]
    public async Task FullGameBoxScoreSelectionDoesNotRepeatUnreliablePeriodRequests()
    {
        var source = new Source();
        var service = new ReferenceAcquisition(source, new Store(), new Discovery(), new LocalGameInventoryReader());
        await service.RunAsync(new("missing-input", "unused-output")
        {
            StartDate = new(2025, 12, 1), EndDate = new(2025, 12, 1), Endpoints = ["boxscoretraditionalv3-full"]
        });
        Assert.That(source.Calls, Is.EqualTo(new[] { ("boxscoretraditionalv3", "0022500315", 0) }));
    }

    [Test]
    public async Task ReferenceStorePreservesBytesAndDetectsCorruptedExistingSnapshots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nba-reference-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bytes = Encoding.UTF8.GetBytes("{ \"original\": true }\r\n");
            var store = new ReferenceStore();
            Assert.That(await store.SaveAsync(directory, "0022500315", "boxscoretraditionalv3", 0, bytes, default), Is.True);
            var path = Directory.GetFiles(Path.Combine(directory, "boxscoretraditionalv3", "0022500315"), "*.json").Single(p => !p.EndsWith(".retrieval.json"));
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(bytes));
            Assert.That(await store.SaveAsync(directory, "0022500315", "boxscoretraditionalv3", 0, bytes, default), Is.False);
            await File.WriteAllTextAsync(path, "{}");
            Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(directory, "0022500315", "boxscoretraditionalv3", 0, bytes, default));
        }
        finally { if (Directory.Exists(directory)) { Directory.Delete(directory, true); } }
    }

    private sealed class Source : INbaReferenceSource
    {
        public List<(string, string, int)> Calls { get; } = [];
        public Task<byte[]> DownloadAsync(string endpoint, string gameId, int period, CancellationToken cancellationToken)
        {
            Calls.Add((endpoint, gameId, period));
            return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        }
    }

    [Test]
    public async Task InventoryRetainsGamesWithNonFranchiseTeamIdsAndContinuesMalformedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nba-inventory-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "special.json"), """
                {"game":{"gameId":"0012500001","actions":[{"period":4,"teamId":1234},{"period":4,"teamId":-1}]}}
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "bad.json"), """
                {"game":{"gameId":"invalid","actions":[]}}
                """);
            var inventory = await new LocalGameInventoryReader().ReadAsync(directory, default);
            Assert.That(inventory.Games.Single().GameId, Is.EqualTo("0012500001"));
            Assert.That(inventory.Games.Single().TeamIds, Is.EqualTo(new[] { "1234" }));
            Assert.That(inventory.Failures, Has.Count.EqualTo(1));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class Store : IReferenceStore
    {
        public bool Existing { get; init; }
        public ReferenceAcquisitionSummary? Report { get; private set; }
        public Task<bool> ExistsAsync(string directory, string gameId, string endpoint, int period, CancellationToken cancellationToken) => Task.FromResult(Existing);
        public Task SaveReportAsync(string directory, ReferenceAcquisitionSummary report, CancellationToken cancellationToken)
        {
            Report = report;
            return Task.CompletedTask;
        }
        public Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period, byte[] bytes, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    [Test]
    public async Task ResumeSkipsVerifiedExistingReferencesAndPersistsCoverage()
    {
        var source = new Source();
        var store = new Store { Existing = true };
        var service = new ReferenceAcquisition(source, store, new Discovery(), new LocalGameInventoryReader());
        var result = await service.RunAsync(new("missing", "unused")
        {
            StartDate = new(2025,12,1), EndDate = new(2025,12,1), Endpoints = ["liveplaybyplay"], SkipExisting = true
        });
        Assert.That(source.Calls, Is.Empty);
        Assert.That(result.Skipped, Is.EqualTo(1));
        Assert.That(store.Report!.CompletedGames, Is.EqualTo(1));
        Assert.That(store.Report.HaltedAfterRepeatedFailures, Is.False);
    }

    [Test]
    public async Task RepeatedTransportFailuresHaltAcquisitionAndPersistFailureEvidence()
    {
        var store = new Store();
        var service = new ReferenceAcquisition(new UnavailableSource(), store, new Discovery(), new LocalGameInventoryReader());
        var result = await service.RunAsync(new("missing", "unused")
        {
            StartDate = new(2025,12,1), EndDate = new(2025,12,1)
        });
        Assert.That(result.Failed, Is.EqualTo(5));
        Assert.That(store.Report!.HaltedAfterRepeatedFailures, Is.True);
        Assert.That(store.Report.Result.Failures.All(f => f.Message.Contains("refused")), Is.True);
    }

    private sealed class UnavailableSource : INbaReferenceSource
    {
        public Task<byte[]> DownloadAsync(string endpoint, string gameId, int period, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Endpoint refused request");
    }

    private sealed class Discovery : INbaSource
    {
        public List<DateOnly> Dates { get; } = [];
        public Task<IReadOnlyList<NbaGame>> DiscoverAsync(DateOnly date, CancellationToken cancellationToken)
        {
            Dates.Add(date);
            return Task.FromResult<IReadOnlyList<NbaGame>>([new("0022500315", date, "DAL", "DEN")
            { PeriodCount = 4, HomeTeamSourceId = "1610612743", AwayTeamSourceId = "1610612742" }]);
        }
        public Task<byte[]> DownloadAsync(string gameId, CancellationToken cancellationToken) => throw new AssertionException("Supporting acquisition must not download PBP.");
    }
}
