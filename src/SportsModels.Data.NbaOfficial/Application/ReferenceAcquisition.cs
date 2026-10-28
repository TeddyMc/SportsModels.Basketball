using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class ReferenceAcquisition(INbaReferenceSource source, IReferenceStore store) : INbaReferenceAcquisition
{
    public async Task<IngestionResult> RunAsync(ReferenceAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        var failures = new List<IngestionFailure>();
        var games = new Dictionary<string, int>();
        var gameTeams = new Dictionary<string, string[]>();
        foreach (var path in Directory.EnumerateFiles(request.InputDirectory, "*.json").Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken));
                var game = document.RootElement.GetProperty("game");
                var id = game.GetProperty("gameId").GetString()!;
                if (id.Length != 10 || id.Any(c => !char.IsAsciiDigit(c))) { throw new InvalidDataException("Invalid game ID."); }
                if (request.GameIds is not null && !request.GameIds.Contains(id)) { continue; }
                games[id] = game.GetProperty("actions").EnumerateArray().Select(x => x.GetProperty("period").GetInt32()).DefaultIfEmpty(0).Max();
                gameTeams[id] = game.GetProperty("actions").EnumerateArray().Where(x => x.TryGetProperty("teamId", out var t) && t.GetInt64() > 0)
                    .Select(x => x.GetProperty("teamId").ToString()).Distinct().ToArray();
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                failures.Add(new(path, FailureKind.InvalidJson, exception.Message));
            }
        }
        var downloaded = 0;
        var skipped = 0;
        foreach (var (id, periods) in games.Take(request.Limit ?? int.MaxValue))
        {
            foreach (var (endpoint, period) in Enumerable.Range(0, Math.Min(periods, 20) + 1)
                .Select(p => ("boxscoretraditionalv3", p))
                .Concat(Enumerable.Range(1, Math.Min(periods, 20)).Select(p => ("boxscoretraditionalv3-range", p)))
                .Concat(Enumerable.Range(1, Math.Min(periods, 20)).Select(p => ("boxscoretraditionalv3-opening", p)))
                .Append(("boxscoresummaryv2", 0))
                .Concat(gameTeams[id].Select(t => ("commonteamroster-" + t, 0))))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resource = $"{id}/{endpoint}/p{period}";
                try
                {
                    var bytes = await source.DownloadAsync(endpoint, id, period, cancellationToken);
                    if (await store.SaveAsync(request.OutputDirectory, id, endpoint, period, bytes, cancellationToken)) { downloaded++; }
                    else { skipped++; }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failures.Add(new(resource, FailureKind.Timeout, "NBA reference request timed out."));
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(new(resource, exception is HttpRequestException ? FailureKind.Http : FailureKind.Storage, exception.Message));
                }
            }
        }
        return new(games.Take(request.Limit ?? int.MaxValue).Count(), downloaded, skipped, failures);
    }
}
