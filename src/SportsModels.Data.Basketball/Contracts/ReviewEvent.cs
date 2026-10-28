namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A basketball review; its result does not itself apply another statistical effect.</summary>
public sealed record ReviewEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? RequestingTeamId,
    ReviewType ReviewType,
    ReviewPhase Phase,
    ReviewOutcome Outcome,
    long? RelatedEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
