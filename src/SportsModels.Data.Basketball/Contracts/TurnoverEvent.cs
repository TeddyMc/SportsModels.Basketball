namespace SportsModels.Data.Basketball.Contracts;

/// <summary>One turnover, including any credited steal; a supporting steal row is not another event.</summary>
public sealed record TurnoverEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long? ResponsiblePlayerId,
    TurnoverType TurnoverType,
    long? StealingPlayerId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
