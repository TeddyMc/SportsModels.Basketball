using System.Security.Cryptography;
using System.Text.Json;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class ReferenceStore : IReferenceStore
{
    public async Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period,
        byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = Path.Combine(directory, gameId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{endpoint}-p{period}-{hash}.json");
        if (File.Exists(path)) { return false; }

        // Content-addressed snapshots are never overwritten; refreshes add revisions.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, path, false);
            await File.WriteAllTextAsync(path + ".retrieval.json", JsonSerializer.Serialize(new
            {
                Endpoint = endpoint, GameId = gameId, Period = period,
                RetrievedAtUtc = DateTime.UtcNow, Sha256 = hash
            }), cancellationToken);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        return true;
    }
}
