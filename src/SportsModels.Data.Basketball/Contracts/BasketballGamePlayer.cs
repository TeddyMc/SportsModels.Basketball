namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Player metadata referenced by internal player ID from events.</summary>
public sealed record BasketballGamePlayer(
    long PlayerId,
    string Name,
    IReadOnlyList<SourceEntityIdentifier> SourceIdentifiers);

