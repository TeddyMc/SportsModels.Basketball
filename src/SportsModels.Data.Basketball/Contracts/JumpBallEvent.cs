namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A jump ball with participants and gaining side populated only when established.</summary>
public sealed record JumpBallEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? FirstTeamPlayerId,
    long? SecondTeamPlayerId,
    long? GainingTeamId,
    long? GainingPlayerId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
