namespace SportsModels.Data.NbaOfficial.Contracts;

public sealed record DataAcquisitionRequest(DateOnly StartDate, DateOnly EndDate, string OutputDirectory)
{
    public TimeSpan RequestDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromMinutes(30);
    public int AttemptsPerResource { get; init; } = 6;
    public int ConsecutiveFailureLimit { get; init; } = 3;
    public int MaximumCooldowns { get; init; } = 12;
    public IProgress<DataAcquisitionProgress>? Progress { get; init; }
}

public sealed record DataAcquisitionProgress(DateTime Utc, string Stage, string Resource, string Detail);

public sealed record DataAcquisitionResult(string RunId, string ReportPath, int ExpectedDates, int DiscoveredDates,
    int DiscoveredGames, int ExpectedResources, int Downloaded, int Skipped, bool Halted,
    IReadOnlyList<IngestionFailure> Failures)
{
    public bool Complete => !Halted && DiscoveredDates == ExpectedDates && Failures.Count == 0
        && Downloaded + Skipped == ExpectedResources;
}

public interface INbaDataAcquisition
{
    Task<DataAcquisitionResult> RunAsync(DataAcquisitionRequest request, CancellationToken cancellationToken = default);
}
