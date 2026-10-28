namespace SportsModels.Data.Basketball.Contracts;

/// <summary>An observed basketball review/ruling, not a manufactured start/completion lifecycle.</summary>
public sealed record ReviewEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? RequestingTeamId,
    ReviewType ReviewType,
    ReviewOutcome Outcome,
    long? RelatedEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
