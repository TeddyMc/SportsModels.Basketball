namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A separately reported long-distance desperation shot near a period's end.</summary>
/// <remarks>
/// NBA source rows label these Team Field Goal Attempt, separately from ordinary player attempts.
/// Player/team statistical treatment depends on competition/season rules and reference verification.
/// </remarks>
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
