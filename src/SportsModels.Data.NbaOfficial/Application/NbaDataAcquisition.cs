using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class NbaDataAcquisition(INbaSource primary, INbaReferenceSource references,
    IAcquisitionWorkspace workspace, IAcquisitionDelay delay) : INbaDataAcquisition
{
    private static readonly string[] Endpoints = ["playbyplayv3", "boxscoretraditionalv3", "liveplaybyplay", "liveboxscore"];

    public async Task<DataAcquisitionResult> RunAsync(DataAcquisitionRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var root = Path.GetFullPath(request.OutputDirectory);
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture)
            + "-" + Guid.NewGuid().ToString("N")[..8];
        var failures = new List<IngestionFailure>();
        var games = new SortedDictionary<string, NbaGame>(StringComparer.Ordinal);
        var discoveredDates = 0;
        var downloaded = 0;
        var skipped = 0;
        var halted = false;

        async Task Progress(string stage, string resource, string detail)
        {
            var progress = new DataAcquisitionProgress(DateTime.UtcNow, stage, resource, detail);
            await workspace.ProgressAsync(root, runId, progress, cancellationToken);
            request.Progress?.Report(progress);
        }

        var recovery = new NbaRequestRecovery(request, delay, Progress);
        await Progress("Started", "", "Discovering completed games, then acquiring primary PBP and three full-game references.");

        try
        {
            for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var found = await workspace.ReadDiscoveryAsync(root, date, cancellationToken);
                    if (found is null)
                    {
                        found = await recovery.ExecuteAsync("discovery/" + date.ToString("yyyy-MM-dd"),
                            () => primary.DiscoverAsync(date, cancellationToken), cancellationToken);
                        await workspace.SaveDiscoveryAsync(root, date, found, cancellationToken);
                    }

                    foreach (var game in found)
                    {
                        _ = game.FileName;
                        if (games.TryGetValue(game.GameId, out var previous)
                            && (previous.HomeTeam != game.HomeTeam || previous.AwayTeam != game.AwayTeam))
                            throw new InvalidDataException("Conflicting discovered teams for " + game.GameId);

                        games.TryAdd(game.GameId, game);
                    }

                    discoveredDates++;
                    await Progress("Discovered", date.ToString("yyyy-MM-dd"), $"{found.Count} final games; {games.Count} unique games so far.");
                }
                catch (Exception error) when (Recoverable(error, cancellationToken))
                {
                    failures.Add(Failure("discovery/" + date.ToString("yyyy-MM-dd"), error));
                    await Progress("ResourceFailed", failures[^1].Resource, error.Message);
                }
            }

            foreach (var game in games.Values)
            {
                foreach (var endpoint in Endpoints)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var resource = game.GameId + "/" + endpoint;

                    try
                    {
                        if (await workspace.ExistsAsync(root, game, endpoint, cancellationToken)) { skipped++; }
                        else
                        {
                            var bytes = await recovery.ExecuteAsync(resource,
                                () => endpoint == "playbyplayv3" ? primary.DownloadAsync(game.GameId, cancellationToken)
                                    : references.DownloadAsync(endpoint, game.GameId, 0, cancellationToken), cancellationToken);
                            await workspace.SaveAsync(root, game, endpoint, bytes, cancellationToken);
                            downloaded++;
                        }
                    }
                    catch (Exception error) when (Recoverable(error, cancellationToken))
                    {
                        failures.Add(Failure(resource, error));
                        await Progress("ResourceFailed", resource, error.Message);
                    }
                }

                await Progress("GameFinished", game.GameId, $"Downloaded={downloaded}; retained={skipped}; failures={failures.Count}.");
            }
        }
        catch (AcquisitionHaltedException error)
        {
            halted = true;
            failures.Add(new("recovery", FailureKind.Http, error.Message));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var cancelled = new DataAcquisitionProgress(DateTime.UtcNow, "Cancelled", "", "Retained data and discovery cache can be resumed.");
            await workspace.ProgressAsync(root, runId, cancelled, CancellationToken.None);
            throw;
        }

        var reportPath = Path.Combine(root, "_acquisition", "runs", runId + ".json");
        var result = new DataAcquisitionResult(runId, reportPath, request.EndDate.DayNumber - request.StartDate.DayNumber + 1,
            discoveredDates, games.Count, games.Count * Endpoints.Length, downloaded, skipped, halted, failures);
        await workspace.ReportAsync(root, result, cancellationToken);
        await Progress(result.Complete ? "Completed" : "Incomplete", "", $"Report: {reportPath}");
        return result;
    }

    private static bool Recoverable(Exception error, CancellationToken token) => error is HttpRequestException
        or IOException or InvalidDataException or JsonException or UnauthorizedAccessException or InvalidOperationException
        || error is OperationCanceledException && !token.IsCancellationRequested;

    private static IngestionFailure Failure(string resource, Exception error) => new(resource, error switch
    {
        HttpRequestException => FailureKind.Http,
        OperationCanceledException => FailureKind.Timeout,
        JsonException => FailureKind.InvalidJson,
        InvalidDataException => FailureKind.InvalidJson,
        _ => FailureKind.Storage
    }, error.Message);

    private static void Validate(DataAcquisitionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);
        if (request.StartDate > request.EndDate || request.EndDate >= DateOnly.FromDateTime(DateTime.UtcNow))
            throw new ArgumentException("Use an inclusive date range ending before today UTC.");
        if (request.RequestDelay < TimeSpan.Zero || request.RequestDelay > TimeSpan.FromMinutes(1)
            || request.RetryDelay < TimeSpan.Zero || request.RetryDelay > TimeSpan.FromHours(1)
            || request.Cooldown < TimeSpan.Zero || request.Cooldown > TimeSpan.FromHours(24)
            || request.AttemptsPerResource is < 1 or > 20 || request.ConsecutiveFailureLimit is < 1 or > 100
            || request.MaximumCooldowns is < 0 or > 100)
            throw new ArgumentException("Invalid bounded pacing/retry options.");
    }
}
