using System.Security.Cryptography;
using System.Text.Json;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class ReferenceStore : IReferenceStore
{
    public async Task<bool> ExistsAsync(string directory, string gameId, string endpoint, int period, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(directory, endpoint.Split('-')[0], gameId);
        if (!Directory.Exists(folder)) { return false; }
        var paths = Directory.GetFiles(folder, $"{endpoint}-p{period}-*.json").Where(p => !p.EndsWith(".retrieval.json", StringComparison.Ordinal)).ToArray();
        if (paths.Length == 0) { return false; }
        foreach (var path in paths)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!Path.GetFileNameWithoutExtension(path).EndsWith(hash, StringComparison.Ordinal)) { throw new InvalidDataException("Stored reference hash mismatch: " + path); }
            using var document = JsonDocument.Parse(bytes);
        }
        return true;
    }

    public async Task SaveReportAsync(string directory, Contracts.ReferenceAcquisitionSummary report, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(directory, "_acquisition");
        Directory.CreateDirectory(folder);
        await using var stream = new FileStream(Path.Combine(folder, report.RunId + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, report, cancellationToken: cancellationToken);
    }

    public async Task<bool> SaveAsync(string directory, string gameId, string endpoint, int period,
        byte[] bytes, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var folder = Path.Combine(directory, endpoint.Split('-')[0], gameId);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{endpoint}-p{period}-{hash}.json");
        if (File.Exists(path))
        {
            if (!(await File.ReadAllBytesAsync(path, cancellationToken)).SequenceEqual(bytes))
            { throw new InvalidDataException("Existing content-addressed reference has different bytes; inspect the retained file before retrying."); }
            return false;
        }

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
