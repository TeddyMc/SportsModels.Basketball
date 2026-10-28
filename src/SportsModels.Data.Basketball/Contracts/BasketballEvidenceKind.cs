namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Describes how BasketballFieldEvidence established a fact or why it remains unset.</summary>
/// <remarks>Used by example diagnostics today; intended for #13 conversion reports, not scoring or confidence rankings.</remarks>
public enum BasketballEvidenceKind
{
    NotSet = 0,
    Direct = 1,
    Parsed = 2,
    Transformed = 3,
    Linked = 4,
    Unresolved = 5,
    NotApplicable = 6
}
