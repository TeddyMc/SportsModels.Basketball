namespace SportsModels.Data.Basketball.Contracts;

/// <summary>One independent free-throw attempt; other events may occur between attempts.</summary>
/// <remarks>
/// Attempt numbers/counts are real facts when known; null means unavailable or unresolved, never zero.
/// Missing values must be surfaced by conversion/QA rather than guessed. The foul schema is deferred.
/// </remarks>
/// <param name="EventId">An internally allocated event identity.</param>
/// <param name="SequenceNumber">Fine-grained order, including events sharing a clock value.</param>
/// <param name="Clock">The period-relative game clock.</param>
/// <param name="OccurredAtUtc">Null when no occurrence time is known; otherwise DateTimeKind.Utc. Never interpolated.</param>
/// <param name="TeamId">The shooter's internal team identity.</param>
/// <param name="ShooterPlayerId">The shooter's internal player identity.</param>
/// <param name="Outcome">The attempt outcome; NotSet means unspecified.</param>
/// <param name="AttemptNumber">1-based attempt number when established; null when unavailable or unresolved.</param>
/// <param name="TotalAttempts">Number of attempts in the associated award when established; null when unavailable or unresolved.</param>
/// <param name="ParentFoulEventId">Internal foul-event ID when a causal foul is established; null when absent or unresolved.</param>
public sealed record FreeThrowEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long ShooterPlayerId,
    ShotOutcome Outcome,
    int? AttemptNumber,
    int? TotalAttempts,
    long? ParentFoulEventId)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
