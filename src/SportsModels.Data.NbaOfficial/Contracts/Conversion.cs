using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Contracts;

public sealed record ConversionRequest(string InputDirectory, string ReferenceDirectory,
    string OutputDirectory, int? Limit = null, IReadOnlyList<string>? GameIds = null);

public sealed record ConversionRunResult(string RunId, string RunDirectory, int Attempted, int Converted, int Failed);

public interface INbaConversion
{
    Task<ConversionRunResult> RunAsync(ConversionRequest request, CancellationToken cancellationToken = default);
}

public sealed record GameConversionReport(string RunId, string GameSourceId, string Competition,
    string Status, string RegistryId, long RegistryRevision,
    IReadOnlyList<InputSnapshot> Inputs, IReadOnlyList<SourceRowDisposition> Rows,
    IReadOnlyList<BasketballFieldEvidence> FieldEvidence, IReadOnlyList<CheckResult> Checks,
    ValidationReport? Validation, string? Failure);

public sealed record ReferenceAcquisitionRequest(string InputDirectory, string OutputDirectory,
    int? Limit = null, IReadOnlyList<string>? GameIds = null)
{
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public IReadOnlyList<string>? Endpoints { get; init; }
    public bool SkipExisting { get; init; }
    public TimeSpan RequestDelay { get; init; }
    public IProgress<ReferenceAcquisitionProgress>? Progress { get; init; }
}

public sealed record ReferenceAcquisitionProgress(int CompletedGames, int TotalGames, string GameId, int Downloaded, int Skipped, int Failures);
public sealed record ReferenceAcquisitionSummary(string RunId, DateTime StartedAtUtc, DateTime CompletedAtUtc,
    string InputDirectory, IReadOnlyList<string>? Endpoints, bool SkipExisting, IngestionResult Result)
{
    public int CompletedGames { get; init; }
    public bool HaltedAfterRepeatedFailures { get; init; }
}

public interface INbaReferenceAcquisition
{
    Task<IngestionResult> RunAsync(ReferenceAcquisitionRequest request, CancellationToken cancellationToken = default);
}
