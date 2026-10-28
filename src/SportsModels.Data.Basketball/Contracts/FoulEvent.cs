namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A foul; the related field goal is the attempt on which it occurred, made or missed.</summary>
public sealed record FoulEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    long? CommittingPlayerId,
    long? FouledPlayerId,
    FoulType FoulType,
    long? RelatedFieldGoalEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
