namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A team or administrative timeout; the team may be inapplicable or unresolved.</summary>
public sealed record TimeoutEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    TimeoutType TimeoutType)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
