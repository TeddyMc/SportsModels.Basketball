namespace SportsModels.Data.Basketball.Contracts;

/// <summary>An official field-goal attempt; assist and block credits occur on this event.</summary>
public sealed record FieldGoalEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long ShooterPlayerId,
    FieldGoalType FieldGoalType,
    ShotOutcome Outcome,
    long? AssistingPlayerId,
    long? BlockingPlayerId,
    double? ShotDistanceFeet,
    BasketballShotLocation? ShotLocation,
    ShotStyle ShotStyle,
    IReadOnlyList<ShotModifier> ShotModifiers,
    CourtArea CourtArea)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
