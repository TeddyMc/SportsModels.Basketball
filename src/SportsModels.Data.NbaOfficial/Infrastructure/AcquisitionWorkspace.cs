using System.Security.Cryptography;
using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class AcquisitionWorkspace(IRawJsonStore primaryStore, IReferenceStore referenceStore) : IAcquisitionWorkspace
{
    private sealed record DiscoveryCache(DateOnly Date, IReadOnlyList<NbaGame> Games);

    public async Task<IReadOnlyList<NbaGame>?> ReadDiscoveryAsync(string root, DateOnly date, CancellationToken token)
    {
        var folder = Path.Combine(root, "_acquisition", "discovery");
        if (!Directory.Exists(folder)) { return null; }

        var files = Directory.GetFiles(folder, date.ToString("yyyyMMdd") + "-*.json");
        if (files.Length == 0) { return null; }
        if (files.Length != 1) { throw new InvalidDataException("Ambiguous cached discovery for " + date); }

        var bytes = await File.ReadAllBytesAsync(files[0], token);
        if (!Path.GetFileNameWithoutExtension(files[0]).EndsWith(Hash(bytes), StringComparison.Ordinal))
            throw new InvalidDataException("Cached discovery hash mismatch: " + files[0]);

        var cached = JsonSerializer.Deserialize<DiscoveryCache>(bytes)
            ?? throw new InvalidDataException("Null cached discovery.");
        ValidateDiscovery(date, cached.Date, cached.Games);
        return cached.Games;
    }

    public async Task SaveDiscoveryAsync(string root, DateOnly date, IReadOnlyList<NbaGame> games, CancellationToken token)
    {
        ValidateDiscovery(date, date, games);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new DiscoveryCache(date, games));
        var path = Path.Combine(root, "_acquisition", "discovery", date.ToString("yyyyMMdd") + "-" + Hash(bytes) + ".json");
        await CreateAsync(path, bytes, token);
    }

    public async Task<bool> ExistsAsync(string root, NbaGame game, string endpoint, CancellationToken token)
    {
        if (endpoint == "playbyplayv3")
        {
            var folder = Path.Combine(root, "stats", "playbyplayv3");
            var paths = Directory.Exists(folder) ? Directory.GetFiles(folder, "*" + game.GameId + ".json") : [];
            if (paths.Length == 0) { return false; }
            if (paths.Length != 1) { throw new InvalidDataException("Ambiguous retained primary PBP: " + game.GameId); }

            var bytes = await File.ReadAllBytesAsync(paths[0], token);
            ValidatePayload(bytes, game.GameId, endpoint);
            await VerifyPrimaryHashAsync(root, game.GameId, bytes, token);
            return true;
        }

        var references = Path.Combine(root, "references");
        if (!await referenceStore.ExistsAsync(references, game.GameId, endpoint, 0, token)) { return false; }

        var files = Directory.GetFiles(Path.Combine(references, endpoint, game.GameId), endpoint + "-p0-*.json")
            .Where(p => !p.EndsWith(".retrieval.json", StringComparison.Ordinal)).ToArray();
        if (files.Length != 1) { throw new InvalidDataException("Ambiguous retained reference: " + game.GameId + "/" + endpoint); }

        ValidatePayload(await File.ReadAllBytesAsync(files[0], token), game.GameId, endpoint);
        return true;
    }

    public async Task SaveAsync(string root, NbaGame game, string endpoint, byte[] bytes, CancellationToken token)
    {
        ValidatePayload(bytes, game.GameId, endpoint);
        if (endpoint == "playbyplayv3")
        {
            await primaryStore.SaveAsync(Path.Combine(root, "stats", "playbyplayv3"), game, bytes, token);
            await VerifyPrimaryHashAsync(root, game.GameId, bytes, token);
        }
        else
        {
            await referenceStore.SaveAsync(Path.Combine(root, "references"), game.GameId, endpoint, 0, bytes, token);
        }
    }

    public async Task ProgressAsync(string root, string runId, DataAcquisitionProgress progress, CancellationToken token)
    {
        var folder = Path.Combine(root, "_acquisition", "runs");
        Directory.CreateDirectory(folder);
        await File.AppendAllTextAsync(Path.Combine(folder, runId + ".jsonl"), JsonSerializer.Serialize(progress) + Environment.NewLine, token);
    }

    public Task ReportAsync(string root, DataAcquisitionResult report, CancellationToken token) =>
        CreateAsync(Path.Combine(root, "_acquisition", "runs", report.RunId + ".json"), JsonSerializer.SerializeToUtf8Bytes(report), token);

    private static async Task VerifyPrimaryHashAsync(string root, string id, byte[] bytes, CancellationToken token)
    {
        var path = Path.Combine(root, "_acquisition", "primary-hashes", id + ".sha256");
        var hash = Hash(bytes);
        if (File.Exists(path))
        {
            if (await File.ReadAllTextAsync(path, token) != hash) { throw new InvalidDataException("Retained primary PBP hash mismatch: " + id); }
        }
        else { await CreateAsync(path, System.Text.Encoding.ASCII.GetBytes(hash), token); }
    }

    private static void ValidateDiscovery(DateOnly expected, DateOnly actual, IReadOnlyList<NbaGame> games)
    {
        if (expected != actual || games is null || games.Any(g => g.Date != expected))
            throw new InvalidDataException("Cached discovery date mismatch.");

        foreach (var game in games) { _ = game.FileName; }
        if (games.Select(g => g.GameId).Distinct(StringComparer.Ordinal).Count() != games.Count)
            throw new InvalidDataException("Duplicate game keys in cached discovery.");
    }

    private static void ValidatePayload(byte[] bytes, string id, string endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var game = document.RootElement.GetProperty(endpoint == "boxscoretraditionalv3" ? "boxScoreTraditional" : "game");
            if (game.GetProperty("gameId").GetString() != id) { throw new InvalidDataException("Mismatched game identity for " + id + "/" + endpoint); }

            if (endpoint is "playbyplayv3" or "liveplaybyplay")
            {
                if (game.GetProperty("actions").GetArrayLength() == 0) { throw new InvalidDataException("Empty PBP stream for " + id); }
            }
            else
            {
                foreach (var side in new[] { "homeTeam", "awayTeam" })
                {
                    var team = game.GetProperty(side);
                    if (team.GetProperty("players").GetArrayLength() == 0 || team.GetProperty("statistics").ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Missing reference roster/statistics for " + id);
                }
            }
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("Unexpected " + endpoint + " response schema for " + id, error);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task CreateAsync(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }
}
