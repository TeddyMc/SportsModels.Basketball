namespace SportsModels.Data.NbaOfficial.Interfaces;

internal interface ILocalGameInventory
{
    Task<Definitions.LocalGameInventory> ReadAsync(string directory, CancellationToken cancellationToken);
}

internal interface INbaReferenceSource
{
    Task<byte[]> DownloadAsync(string endpoint, string gameId, int period, CancellationToken cancellationToken);
}

internal interface IReferenceStore
{
    Task<bool> ExistsAsync(string directory, string gameId, string endpoint, int period, CancellationToken cancellationToken);
    Task SaveReportAsync(string directory, Contracts.ReferenceAcquisitionSummary report, CancellationToken cancellationToken);
    Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period,
        byte[] bytes, CancellationToken cancellationToken);
}
