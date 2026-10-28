namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A separately reported team heave; player/team statistical treatment requires source verification.</summary>
public sealed record HeaveEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    long? PlayerId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
