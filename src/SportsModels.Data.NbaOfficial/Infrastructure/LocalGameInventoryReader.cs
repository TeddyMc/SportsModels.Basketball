using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Infrastructure;

internal sealed class LocalGameInventoryReader : ILocalGameInventory
{
    public async Task<LocalGameInventory> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) { return new(false, [], []); }
        var games = new List<LocalReferenceGame>();
        var failures = new List<IngestionFailure>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                var game = document.RootElement.GetProperty("game");
                var id = game.Text("gameId");
                if (id.Length != 10 || id.Any(c => !char.IsAsciiDigit(c))) { throw new InvalidDataException("Invalid game ID."); }
                var rows = game.GetProperty("actions").EnumerateArray().ToArray();
                var periods = rows.Select(r => r.GetProperty("period").GetInt32()).DefaultIfEmpty(0).Max();
                if (periods < 0 || periods > 20) { throw new InvalidDataException("Unsupported period count."); }
                var teams = rows.Select(r => r.Key("teamId")).Where(t => t != "").Distinct().ToArray();
                if (teams.Any(t => t.Length != 10 || t.Any(c => !char.IsAsciiDigit(c)))) { throw new InvalidDataException("Invalid team identity."); }
                games.Add(new(id, periods, teams));
            }
            catch (Exception exception) when (exception is JsonException or IOException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
            { failures.Add(new(path, FailureKind.InvalidJson, exception.Message)); }
        }
        return new(true, games, failures);
    }
}
