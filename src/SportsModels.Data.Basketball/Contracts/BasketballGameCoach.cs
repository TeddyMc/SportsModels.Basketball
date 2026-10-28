namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Coach metadata; coaches never occupy player identities.</summary>
public sealed record BasketballGameCoach(
    long CoachId,
    string Name,
    IReadOnlyList<SourceEntityIdentifier> SourceIdentifiers);
