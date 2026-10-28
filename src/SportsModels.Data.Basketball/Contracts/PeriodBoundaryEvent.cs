namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A period boundary; the inherited clock identifies the quarter or overtime number.</summary>
public sealed record PeriodBoundaryEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    PeriodMarker Marker)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
