namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A team and its game-specific player metadata; listing a player does not imply participation or active status.</summary>
public sealed record BasketballGameTeam(
    long TeamId,
    string Name,
    IReadOnlyList<SourceEntityIdentifier> SourceIdentifiers,
    IReadOnlyList<BasketballGamePlayer> Players);
