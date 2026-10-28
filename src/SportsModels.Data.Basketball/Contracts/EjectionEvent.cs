namespace SportsModels.Data.Basketball.Contracts;

/// <summary>An observed player or coach ejection; player and coach identities are mutually exclusive.</summary>
public sealed record EjectionEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long? TeamId,
    long? PlayerId,
    long? CoachId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
