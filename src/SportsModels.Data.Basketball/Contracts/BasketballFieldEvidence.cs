namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Companion evidence for a canonical event field; does not wrap or duplicate its value.</summary>
/// <remarks>
/// The consumer example prints these separately from its games. Conversion/reporting in #13 will
/// create them to explain parsing, transformations, links and missingness during data review.
/// Canonical event consumers do not require this companion list to use the game contracts.
/// </remarks>
public sealed record BasketballFieldEvidence(
    long EventId,
    string FieldName,
    BasketballEvidenceKind Kind,
    IReadOnlyList<BasketballSourceRowReference> SourceRows,
    string Explanation);
