using System.Text.Json;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class PlayByPlayIngestion(INbaSource source, IRawJsonStore store) : IPlayByPlayIngestion
{
    public async Task<IngestionResult> RunAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);

        if (request.StartDate > request.EndDate || request.EndDate >= DateOnly.FromDateTime(DateTime.UtcNow))
        {
            throw new ArgumentException("Use an inclusive date range ending before today (UTC).", nameof(request));
        }

        var directory = Path.GetFullPath(request.OutputPath);
        var failures = new List<IngestionFailure>();
        var gameIds = new SortedSet<string>(StringComparer.Ordinal);

        for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                gameIds.UnionWith(await source.DiscoverAsync(date, cancellationToken));
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                failures.Add(ToFailure(date.ToString("yyyy-MM-dd"), exception));
            }
        }

        var downloaded = 0;
        var skipped = 0;

        foreach (var gameId in gameIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await store.ExistsAsync(directory, gameId, cancellationToken))
                {
                    skipped++;
                    continue;
                }

                var bytes = await source.DownloadAsync(gameId, cancellationToken);

                // Validate syntax only; never transform the raw response.
                using var json = JsonDocument.Parse(bytes);

                await store.SaveAsync(directory, gameId, bytes, cancellationToken);
                downloaded++;
            }
            catch (Exception exception) when (IsRecoverable(exception, cancellationToken))
            {
                failures.Add(ToFailure(gameId, exception));
            }
        }

        return new IngestionResult(gameIds.Count, downloaded, skipped, failures.AsReadOnly());
    }

    private static bool IsRecoverable(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException or JsonException or IOException or UnauthorizedAccessException
        or InvalidDataException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static IngestionFailure ToFailure(string resource, Exception exception) => new(resource,
        exception switch
        {
            HttpRequestException => FailureKind.Http,
            OperationCanceledException => FailureKind.Timeout,
            JsonException => FailureKind.InvalidJson,
            InvalidDataException => FailureKind.Discovery,
            _ => FailureKind.Storage
        }, exception.Message);
}
