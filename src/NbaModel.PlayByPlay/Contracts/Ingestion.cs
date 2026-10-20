namespace NbaModel.PlayByPlay.Contracts;

public sealed record IngestionRequest(DateOnly StartDate, DateOnly EndDate, string OutputPath);

public enum FailureKind { Http, Timeout, InvalidJson, Discovery, Storage }

public sealed record IngestionFailure(string Resource, FailureKind Kind, string Message);

public sealed record IngestionResult(
    int Discovered, int Downloaded, int Skipped, IReadOnlyList<IngestionFailure> Failures)
{
    public int Failed => Failures.Count;
}

public interface IPlayByPlayIngestion
{
    Task<IngestionResult> RunAsync(IngestionRequest request, CancellationToken cancellationToken = default);
}
