namespace SportsModels.Data.Basketball.Contracts;

public enum PlayerEligibility { Unknown, Active, Inactive }

/// <summary>Game-specific roster evidence, not an assertion about original pre-tip publication time.</summary>
public sealed record RosterFieldEvidence(string FieldName, BasketballDataSource Source,
    string SnapshotId, string SourcePath, string SourceValue);
