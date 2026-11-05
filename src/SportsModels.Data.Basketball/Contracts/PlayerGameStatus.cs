namespace SportsModels.Data.Basketball.Contracts;

public enum PlayerEligibility { Unknown, Active, Inactive }
public enum RosterEvidenceKind { SourceField, Participation, OpeningMembership }

/// <summary>Game-specific roster evidence, not an assertion about original pre-tip publication time.</summary>
public sealed record RosterFieldEvidence(string FieldName, BasketballDataSource Source,
    string SnapshotId, string SourcePath, string SourceValue)
{
    public RosterEvidenceKind Kind { get; init; } = RosterEvidenceKind.SourceField;
    public MinuteEvidenceKind Origin { get; init; } = MinuteEvidenceKind.SupportingFeed;
    public string? Explanation { get; init; }
    public IReadOnlyList<long> EventIds { get; init; } = [];
}
