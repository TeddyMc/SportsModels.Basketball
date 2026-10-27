namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A game with internal identities, game-specific rosters and one chronological event stream.</summary>
/// <remarks>Sequence numbers retain fine-grained order at equal clock times. Collections are caller-owned.</remarks>
public sealed record BasketballGamePlayByPlay(
    long GameId,
    IReadOnlyList<SourceEntityIdentifier> SourceIdentifiers,
    BasketballGameTeam HomeTeam,
    BasketballGameTeam AwayTeam,
    IReadOnlyList<BasketballPlayByPlayEvent> Events);
