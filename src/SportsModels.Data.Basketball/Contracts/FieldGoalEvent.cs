namespace SportsModels.Data.Basketball.Contracts;

/// <summary>A field-goal attempt, including team-credited heaves; assist and block credits occur here.</summary>
/// <remarks>
/// Shooter identity and statistical attempt credit are separate facts. Team credit does not erase a known shooter.
/// Player credit requires a resolved shooter; team-only source rows can leave the shooter null with evidence.
/// Unestablished shot value/outcome remain NotSet rather than inventing a three-point miss from a provider label.
/// </remarks>
public sealed record FieldGoalEvent(
    long EventId,
    int SequenceNumber,
    BasketballClock Clock,
    DateTime? OccurredAtUtc,
    long TeamId,
    long? ShooterPlayerId,
    FieldGoalType FieldGoalType,
    ShotOutcome Outcome,
    long? AssistingPlayerId,
    long? BlockingPlayerId,
    double? ShotDistanceFeet,
    BasketballShotLocation? ShotLocation,
    ShotStyle ShotStyle,
    IReadOnlyList<ShotModifier> ShotModifiers,
    CourtArea CourtArea,
    FieldGoalAttemptCredit AttemptCredit)
    : BasketballPlayByPlayEvent(
        EventId,
        SequenceNumber,
        Clock,
        OccurredAtUtc);
