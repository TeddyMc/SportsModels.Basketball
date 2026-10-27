namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A substitution referencing internal team and player identities.</summary>
/// <remarks>OccurredAtUtc is null when no occurrence time is known; otherwise it must have DateTimeKind.Utc.</remarks>
public sealed record SubstitutionEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long PlayerOutId,
    long PlayerInId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
