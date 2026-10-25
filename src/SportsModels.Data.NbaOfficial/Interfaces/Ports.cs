using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Interfaces;

internal interface INbaSource
{
    Task<IReadOnlyList<NbaGame>> DiscoverAsync(DateOnly date, CancellationToken cancellationToken);

    Task<byte[]> DownloadAsync(string gameId, CancellationToken cancellationToken);
}

internal interface IRawJsonStore
{
    Task<bool> ExistsAsync(string directory, NbaGame game, CancellationToken cancellationToken);

    Task SaveAsync(string directory, NbaGame game, byte[] bytes, CancellationToken cancellationToken);
}
