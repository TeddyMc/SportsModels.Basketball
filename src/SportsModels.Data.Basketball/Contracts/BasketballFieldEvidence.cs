namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Companion evidence for a canonical event field; does not wrap or duplicate its value.</summary>
public sealed record BasketballFieldEvidence(
    long EventId,
    string FieldName,
    BasketballEvidenceKind Kind,
    IReadOnlyList<BasketballSourceRowReference> SourceRows,
    string Explanation);
