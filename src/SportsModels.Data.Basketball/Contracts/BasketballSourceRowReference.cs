namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A zero-based source-row reference within the immutable snapshot identified by SnapshotId.</summary>
/// <remarks>
/// Used by BasketballFieldEvidence.SourceRows to locate the original data behind an event field.
/// The consumer example supplies these today; conversion/reporting in #13 will populate them.
/// SnapshotId must resolve through a source manifest; this record neither stores nor loads raw JSON.
/// </remarks>
public sealed record BasketballSourceRowReference(
    BasketballDataSource Source,
    string SnapshotId,
    int RowIndex,
    string? ActionId,
    string? ActionNumber);
