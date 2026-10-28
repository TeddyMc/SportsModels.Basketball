namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A rebound linked to at most one missed attempt; live/dead-ball state is reconstructed separately.</summary>
public sealed record ReboundEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    long? ReboundingPlayerId,
    ReboundType ReboundType,
    ReboundCredit Credit,
    long? RelatedFieldGoalEventId,
    long? RelatedFreeThrowEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
