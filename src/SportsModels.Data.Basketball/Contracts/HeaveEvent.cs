namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A separately reported heave not established as an official attempt; implies no miss or points.</summary>
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
