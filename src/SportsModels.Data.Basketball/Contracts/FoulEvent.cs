namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A foul with one or more charged participants; related field goal identifies the affected attempt.</summary>
public sealed record FoulEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    IReadOnlyList<FoulParticipant> CommittingParticipants,
    long? FouledPlayerId,
    FoulType FoulType,
    long? RelatedFieldGoalEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
