using SportsModels.Data.Basketball.Contracts;

// Synthetic selected events, not a complete game or source reconstruction.
// Internal IDs are independently allocated; provider IDs remain strings on metadata.
var shooter = new BasketballGamePlayer(101, "Home Shooter", [new(BasketballDataSource.NbaOfficial, "900001")]);

var outgoing = new BasketballGamePlayer(201, "Away Outgoing", [new(BasketballDataSource.NbaOfficial, "900002")]);

var incoming = new BasketballGamePlayer(202, "Away Incoming", [new(BasketballDataSource.NbaOfficial, "900003")]);

var home = new BasketballGameTeam(11, "Home", [new(BasketballDataSource.NbaOfficial, "800001")], [shooter]);

var away = new BasketballGameTeam(12, "Away", [new(BasketballDataSource.NbaOfficial, "800002")], [outgoing, incoming]);

var continuationClock = new BasketballClock(BasketballPeriod.FourthQuarter, null, TimeSpan.FromSeconds(413));

var awayFromPlayClock = continuationClock with { ClockRemaining = TimeSpan.FromSeconds(300) };

var interruptedClock = continuationClock with { ClockRemaining = TimeSpan.FromSeconds(42.5) };

var overtimeStart = new BasketballClock(BasketballPeriod.Overtime, 2, TimeSpan.FromMinutes(5));

var overtimeClock = overtimeStart with { ClockRemaining = TimeSpan.FromSeconds(0.7) };

var overtimeEnd = overtimeStart with { ClockRemaining = TimeSpan.Zero };

// Explicit synthetic time; neighbouring events remain unknown, without interpolation.
var knownTime = new DateTime(2026, 1, 2, 2, 15, 0, DateTimeKind.Utc);

BasketballPlayByPlayEvent[] events =
[
    // Informed by #8's recorded Flagg case; original source rows were not re-inspected.
    // Left of the attacking basket, toward centre court; no provider transformation is claimed.
    new FieldGoalEvent(900, 1, continuationClock, null, home.TeamId, shooter.PlayerId,
        FieldGoalType.TwoPointer, ShotOutcome.Made, null, null, null,
        new(-1, 2), ShotStyle.Layup, [ShotModifier.Driving, ShotModifier.Reverse], CourtArea.NotSet),
    new FoulEvent(901, 2, continuationClock, null, away.TeamId, outgoing.PlayerId,
        shooter.PlayerId, FoulType.Shooting, 900),
    new FreeThrowEvent(902, 3, continuationClock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 1, 1, 901, FreeThrowCategory.Ordinary, FreeThrowPossessionRule.Ordinary),

    // Contrast #8's recorded CHI-CHA case: a single FT need not be an and-one.
    new FoulEvent(903, 4, awayFromPlayClock, null, away.TeamId, outgoing.PlayerId,
        shooter.PlayerId, FoulType.AwayFromPlay, null),
    new FreeThrowEvent(904, 5, awayFromPlayClock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 1, 1, 903, FreeThrowCategory.AwayFromPlay, FreeThrowPossessionRule.Retained),

    new FieldGoalEvent(905, 6, interruptedClock, null, home.TeamId, shooter.PlayerId,
        FieldGoalType.TwoPointer, ShotOutcome.Missed, null, null, null, null,
        ShotStyle.JumpShot, [], CourtArea.NotSet),
    new FoulEvent(906, 7, interruptedClock, null, away.TeamId, outgoing.PlayerId,
        shooter.PlayerId, FoulType.Shooting, 905),
    // Existing constructor usage compiles; added category/possession fields default to NotSet.
    new FreeThrowEvent(907, 8, interruptedClock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 1, 2, 906),
    new SubstitutionEvent(908, 9, interruptedClock, null, away.TeamId, outgoing.PlayerId, incoming.PlayerId),
    new FreeThrowEvent(909, 10, interruptedClock, null, home.TeamId, shooter.PlayerId,
        ShotOutcome.Made, 2, 2, 906),

    // Selected second-overtime events; intervening periods/play are omitted.
    new PeriodEvent(910, 11, overtimeStart, null, PeriodMarker.Start),
    new JumpBallEvent(911, 12, overtimeStart, null, shooter.PlayerId, incoming.PlayerId,
        home.TeamId, shooter.PlayerId),
    // Right of the attacking basket and toward centre court: explicit canonical coordinates.
    new FieldGoalEvent(912, 13, overtimeClock, null, home.TeamId, shooter.PlayerId,
        FieldGoalType.TwoPointer, ShotOutcome.Missed, null, incoming.PlayerId, null,
        new(3, 8), ShotStyle.JumpShot, [], CourtArea.NotSet),
    new ReboundEvent(913, 14, overtimeClock, null, away.TeamId, null,
        ReboundType.Defensive, ReboundCredit.Team, false, 912, null),
    new TimeoutEvent(914, 15, overtimeClock, null, null, TimeoutType.Official),
    new TurnoverEvent(915, 16, overtimeClock, null, away.TeamId, incoming.PlayerId,
        TurnoverType.BadPass, shooter.PlayerId),
    // Standalone scoring award: no corresponding made FG to count again.
    new ViolationEvent(916, 17, overtimeClock, null, away.TeamId, incoming.PlayerId,
        ViolationType.Goaltending, null, null),
    new AwardedPointsEvent(917, 18, overtimeClock, null, home.TeamId, shooter.PlayerId,
        2, AwardedPointsReason.Goaltending, 916),
    new ReviewEvent(918, 19, overtimeClock, null, null, ReviewType.OfficialReview,
        ReviewPhase.Started, ReviewOutcome.NotSet, 916),
    new ReviewEvent(919, 20, overtimeClock, knownTime, null, ReviewType.OfficialReview,
        ReviewPhase.Completed, ReviewOutcome.Upheld, 916),
    new HeaveEvent(920, 21, overtimeEnd, null, away.TeamId, incoming.PlayerId),
    new PeriodEvent(921, 22, overtimeEnd, null, PeriodMarker.End)
];

var game = new BasketballGamePlayByPlay(1,
    [new(BasketballDataSource.NbaOfficial, "0022500001")], home, away, events);

Console.WriteLine($"Synthetic contract demonstration: {game.Events.Count} selected events; not a complete game or source validation.");

foreach (var play in game.Events)
{
    var occurredAt = play.OccurredAtUtc?.ToString("O") ?? "unknown";

    Console.WriteLine($"{play.SequenceNumber}: {play.GetType().Name}; {play.Clock.Period} OT={play.Clock.OvertimePeriodNumber} remaining={play.Clock.ClockRemaining:c}; UTC={occurredAt}");
}

// Direct links support consumer queries; no stored and-one flag or relationship graph.
var fouls = game.Events.OfType<FoulEvent>().ToDictionary(foul => foul.EventId);

var shots = game.Events.OfType<FieldGoalEvent>().ToDictionary(shot => shot.EventId);

foreach (var attempt in game.Events.OfType<FreeThrowEvent>().Where(attempt => attempt.TotalAttempts == 1))
{
    var foul = fouls[attempt.ParentFoulEventId!.Value];

    var supportedContinuation = foul.FoulType == FoulType.Shooting
        && foul.RelatedFieldGoalEventId is long shotId
        && shots[shotId].Outcome == ShotOutcome.Made
        && attempt.Category == FreeThrowCategory.Ordinary;

    Console.WriteLine($"FT {attempt.EventId}: supported continuation={supportedContinuation}; foul={foul.FoulType}");
}

var interruptedSequence = game.Events.Where(play => play.EventId is >= 907 and <= 909);

Console.WriteLine($"Same-clock order: {string.Join(" -> ", interruptedSequence.Select(play => play.GetType().Name))}");
Console.WriteLine("Block/steal source rows may support the shot/turnover without duplicate statistical events.");
Console.WriteLine("Heave adds no official attempt; review adds no second award; awarded points have no duplicate made shot here.");
Console.WriteLine("#13 owns source mappings, unknown-row reports, corrected effective history and real overtime verification.");
