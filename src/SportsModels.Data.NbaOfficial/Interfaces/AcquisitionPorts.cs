using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Interfaces;

internal interface IAcquisitionWorkspace
{
    Task<IReadOnlyList<NbaGame>?> ReadDiscoveryAsync(string root, DateOnly date, CancellationToken token);
    Task SaveDiscoveryAsync(string root, DateOnly date, IReadOnlyList<NbaGame> games, CancellationToken token);
    Task<bool> ExistsAsync(string root, NbaGame game, string endpoint, CancellationToken token);
    Task SaveAsync(string root, NbaGame game, string endpoint, byte[] bytes, CancellationToken token);
    Task ProgressAsync(string root, string runId, DataAcquisitionProgress progress, CancellationToken token);
    Task ReportAsync(string root, DataAcquisitionResult report, CancellationToken token);
}

internal interface IAcquisitionDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken token);
}
