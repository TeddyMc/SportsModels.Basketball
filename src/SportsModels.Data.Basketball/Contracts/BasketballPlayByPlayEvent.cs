namespace SportsModels.Data.Basketball.Contracts;

/// <summary>Common identity, ordering and timing for a flat chronological event stream.</summary>
/// <param name="EventId">An internally allocated event identity, independent of provider action IDs.</param>
/// <param name="SequenceNumber">Fine-grained order, including events sharing a clock value.</param>
/// <param name="Clock">The period-relative game clock.</param>
/// <param name="OccurredAtUtc">Known occurrence time with DateTimeKind.Utc; null when unavailable. Never interpolated.</param>
public abstract record BasketballPlayByPlayEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc);
