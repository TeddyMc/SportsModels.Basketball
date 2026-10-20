namespace NbaModel.PlayByPlay.Interfaces;

internal interface INbaSource
{
    Task<IReadOnlyList<string>> DiscoverAsync(DateOnly date, CancellationToken cancellationToken);

    Task<byte[]> DownloadAsync(string gameId, CancellationToken cancellationToken);
}

internal interface IRawJsonStore
{
    Task<bool> ExistsAsync(string directory, string gameId, CancellationToken cancellationToken);

    Task SaveAsync(string directory, string gameId, byte[] bytes, CancellationToken cancellationToken);
}
