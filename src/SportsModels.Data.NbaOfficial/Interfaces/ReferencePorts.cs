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
    Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period,
        byte[] bytes, CancellationToken cancellationToken);
}
