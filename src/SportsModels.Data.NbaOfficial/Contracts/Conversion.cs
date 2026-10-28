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
    int? Limit = null, IReadOnlyList<string>? GameIds = null);

public interface INbaReferenceAcquisition
{
    Task<IngestionResult> RunAsync(ReferenceAcquisitionRequest request, CancellationToken cancellationToken = default);
}
