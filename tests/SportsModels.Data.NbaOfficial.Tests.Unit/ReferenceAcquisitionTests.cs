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

    private sealed class Store : IReferenceStore
    {
        public Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period, byte[] bytes, CancellationToken cancellationToken) => Task.FromResult(true);
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
