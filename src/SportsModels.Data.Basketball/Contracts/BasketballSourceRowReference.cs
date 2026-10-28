namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A zero-based source-row reference within the immutable snapshot identified by SnapshotId.</summary>
public sealed record BasketballSourceRowReference(
    BasketballDataSource Source,
    string SnapshotId,
    int RowIndex,
    string? ActionId,
    string? ActionNumber);
