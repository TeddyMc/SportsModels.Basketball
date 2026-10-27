namespace SportsModels.Data.Basketball.Contracts;

/// <summary>One independent free-throw attempt; other events may occur between attempts.</summary>
/// <remarks>Attempt counts and the typed parent-foul ID remain null when unknown. The foul schema is deferred.</remarks>
public sealed record FreeThrowEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long ShooterPlayerId,
    ShotOutcome Outcome,
    int? AttemptNumber,
    int? TotalAttempts,
    long? ParentFoulEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);

