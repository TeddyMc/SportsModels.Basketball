namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A violation, which does not automatically imply a turnover or an additional scoring award.</summary>
public sealed record ViolationEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    long? CommittingPlayerId,
    ViolationType ViolationType,
    long? RelatedFieldGoalEventId,
    long? RelatedFreeThrowEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
