namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A positive scoring award not already represented by a made field goal or free throw.</summary>
public sealed record AwardedPointsEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long? CreditedPlayerId,
    int Points,
    AwardedPointsReason Reason,
    long? RelatedViolationEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
