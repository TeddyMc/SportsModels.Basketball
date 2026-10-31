using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class ReferenceAcquisition(INbaReferenceSource source, IReferenceStore store, INbaSource discovery, ILocalGameInventory inventory) : INbaReferenceAcquisition
{
    public async Task<IngestionResult> RunAsync(ReferenceAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTime.UtcNow;
        if (request.RequestDelay < TimeSpan.Zero || request.RequestDelay > TimeSpan.FromMinutes(1)) { throw new ArgumentException("Request delay must be between zero and one minute."); }
        if (request.Limit is <= 0) { throw new ArgumentException("Limit must be positive."); }
        if (request.Endpoints is not null && request.Endpoints.Any(e => e is not ("boxscoretraditionalv3" or "boxscoretraditionalv3-full" or "boxscoretraditionalv3-range" or "boxscoretraditionalv3-opening" or "boxscoresummaryv2" or "commonteamroster" or "gamerotation" or "liveplaybyplay" or "liveboxscore")))
        { throw new ArgumentException("Unknown reference endpoint selection."); }
        var failures = new List<IngestionFailure>();
        var games = new Dictionary<string, int>();
        var gameTeams = new Dictionary<string, string[]>();
        var dateSelection = new HashSet<string>();
        if (request.StartDate is not null || request.EndDate is not null)
        {
            if (request.StartDate is null || request.EndDate is null || request.StartDate > request.EndDate
                || request.EndDate >= DateOnly.FromDateTime(DateTime.UtcNow)) { throw new ArgumentException("Provide an inclusive date range ending before today UTC."); }
            for (var date = request.StartDate.Value; date <= request.EndDate.Value; date = date.AddDays(1))
            {
                try
                {
                    foreach (var game in await discovery.DiscoverAsync(date, cancellationToken))
                    {
                        if (request.GameIds is not null && !request.GameIds.Contains(game.GameId)) { continue; }
                        dateSelection.Add(game.GameId);
                        games[game.GameId] = game.PeriodCount ?? 0;
                        gameTeams[game.GameId] = new[] { game.HomeTeamSourceId, game.AwayTeamSourceId }.OfType<string>().ToArray();
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                    || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                { failures.Add(new(date.ToString("yyyy-MM-dd"), FailureKind.Discovery, exception.Message)); }
            }
        }
        var local = await inventory.ReadAsync(request.InputDirectory, cancellationToken);
        failures.AddRange(local.Failures);
        if (!local.DirectoryExists && request.StartDate is null) { throw new DirectoryNotFoundException(request.InputDirectory); }
        foreach (var game in local.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.GameIds is not null && !request.GameIds.Contains(game.GameId)) { continue; }
            if (request.StartDate is not null && !dateSelection.Contains(game.GameId)) { continue; }
            games[game.GameId] = game.Periods;
            gameTeams[game.GameId] = game.TeamIds;
        }
        var downloaded = 0;
        var skipped = 0;
        var completed = 0;
        var consecutiveFailures = 0;
        var halted = false;
        var total = games.Take(request.Limit ?? int.MaxValue).Count();
        foreach (var (id, periods) in games.Take(request.Limit ?? int.MaxValue))
        {
            foreach (var (endpoint, period) in Enumerable.Range(0, Math.Min(periods, 20) + 1)
                .Select(p => ("boxscoretraditionalv3", p))
                .Concat(Enumerable.Range(1, Math.Min(periods, 20)).Select(p => ("boxscoretraditionalv3-range", p)))
                .Concat(Enumerable.Range(1, Math.Min(periods, 20)).Select(p => ("boxscoretraditionalv3-opening", p)))
                .Append(("boxscoresummaryv2", 0))
                .Append(("gamerotation", 0))
                .Append(("liveplaybyplay", 0))
                .Append(("liveboxscore", 0))
                .Concat(gameTeams[id].Select(t => ("commonteamroster-" + t, 0))))
            {
                var selectedEndpoint = endpoint.StartsWith("commonteamroster-", StringComparison.Ordinal) ? "commonteamroster" : endpoint;
                var fullGame = endpoint == "boxscoretraditionalv3" && period == 0 && request.Endpoints?.Contains("boxscoretraditionalv3-full") == true;
                if (request.Endpoints is not null && !request.Endpoints.Contains(selectedEndpoint) && !fullGame) { continue; }
                cancellationToken.ThrowIfCancellationRequested();
                var resource = $"{id}/{endpoint}/p{period}";
                try
                {
                    if (request.SkipExisting && await store.ExistsAsync(request.OutputDirectory, id, endpoint, period, cancellationToken))
                    { skipped++; continue; }
                    if (request.RequestDelay > TimeSpan.Zero) { await Task.Delay(request.RequestDelay, cancellationToken); }
                    var bytes = await source.DownloadAsync(endpoint, id, period, cancellationToken);
                    if (await store.SaveAsync(request.OutputDirectory, id, endpoint, period, bytes, cancellationToken)) { downloaded++; }
                    else { skipped++; }
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    failures.Add(new(resource, FailureKind.Timeout, "NBA reference request timed out."));
                    consecutiveFailures++;
                }
                catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or UnauthorizedAccessException)
                {
                    failures.Add(new(resource, exception is HttpRequestException ? FailureKind.Http : FailureKind.Storage, exception.Message));
                    consecutiveFailures++;
                }
                if (consecutiveFailures >= 5) { halted = true; break; }
            }
            completed++;
            request.Progress?.Report(new(completed, total, id, downloaded, skipped, failures.Count));
            if (halted) { break; }
        }
        var result = new IngestionResult(total, downloaded, skipped, failures);
        var runId = started.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        await store.SaveReportAsync(request.OutputDirectory, new(runId, started, DateTime.UtcNow,
            Path.GetFullPath(request.InputDirectory), request.Endpoints, request.SkipExisting, result)
        { CompletedGames = completed, HaltedAfterRepeatedFailures = halted }, cancellationToken);
        return result;
    }
}
