using SportsModels.Data.Basketball.Contracts;

// Manually constructed selections from identified local source snapshots, not a converter or full games.
// Internal IDs are allocated independently. Metadata associations are curated for these examples;
// production roster/name matching and the complete evidence/coverage report remain #13.
Scenario[] scenarios = [Boston(), DoubleTechnical(), DoublePersonal(), SecondOvertime(), CoachEjection()];

foreach (var scenario in scenarios)
{
    Console.WriteLine($"{scenario.SourceFile}: {scenario.Game.Events.Count} selected events");

    foreach (var play in scenario.Game.Events)
    {
        Console.WriteLine($"  {play.SequenceNumber}: {play.GetType().Name}; {play.Clock.Period} OT={play.Clock.OvertimePeriodNumber}; {play.Clock.ClockRemaining:c}");
    }

    foreach (var evidence in scenario.Evidence)
    {
        Console.WriteLine($"  Evidence {evidence.EventId}.{evidence.FieldName}: {evidence.Kind}; {evidence.Explanation}");
    }
}

// Scenario-specific smoke checks, not a general basketball validator or statistics engine.
var boston = scenarios[0].Game;

var creditedShot = boston.Events.OfType<FieldGoalEvent>().Single();

var goaltending = boston.Events.OfType<ViolationEvent>().Single();

if (creditedShot.Outcome != ShotOutcome.Made || goaltending.RelatedFieldGoalEventId != creditedShot.EventId)
{
    throw new InvalidOperationException("The goaltending example must retain one credited made shot and its linked violation.");
}

var interrupted = boston.Events.Where(play => play.EventId is >= 902 and <= 904).ToArray();

if (interrupted is not [FreeThrowEvent { AttemptNumber: 1 }, SubstitutionEvent, FreeThrowEvent { AttemptNumber: 2 }])
{
    throw new InvalidOperationException("The source's interrupted free-throw sequence must retain its order.");
}

var doubleFoul = scenarios[1].Game.Events.OfType<FoulEvent>().First();

if (doubleFoul.CommittingParticipants.Count != 2 || doubleFoul.FouledPlayerId is not null)
{
    throw new InvalidOperationException("Both double-foul participants are charged; neither is stored as the fouled player.");
}

var coachCharge = boston.Events.OfType<FoulEvent>().Single(foul => foul.FoulType == FoulType.Technical);

if (coachCharge.CommittingParticipants.Single() is not { PlayerId: null, CoachId: 301 })
{
    throw new InvalidOperationException("A coach charge must not use a player identity.");
}

var overtime = scenarios[3].Game.Events;

if (overtime.Any(play => play.Clock.OvertimePeriodNumber != 2)
    || overtime.OfType<FieldGoalEvent>().First().Clock.ClockRemaining != TimeSpan.FromSeconds(59.6))
{
    throw new InvalidOperationException("The real period-six example must preserve second overtime and fractional seconds.");
}

Console.WriteLine("Source-derived smoke checks passed: one credited goaltending basket, interrupted FTs, double foul, coach identity and fractional second overtime.");
Console.WriteLine("No source reconstruction, complete lineup/state validation, coordinate transformation or team-heave statistics are claimed.");

static Scenario Boston()
{
    const string snapshot = "sha256:36380d9ad02c52d5c641ec429294e6471583e0817d4d21e98c785d4220d1ebaa";

    var coach = new BasketballGameCoach(301, "Joe Mazzulla", [new(BasketballDataSource.NbaOfficial, "1628210")]);

    var home = Team(11, "Boston", "1610612738",
        Player(101, "Brown", "1627759"), Player(102, "White", "1628401"),
        Player(103, "Williams", "1642873"), Player(104, "Garza", "1630568")) with { Coaches = [coach] };

    var away = Team(12, "Atlanta", "1610612737",
        Player(201, "Daniels", "1630700"), Player(202, "Johnson", "1630552"));

    var ftClock = Clock(BasketballPeriod.FirstQuarter, 444);

    var coachClock = Clock(BasketballPeriod.ThirdQuarter, 133);

    var shotClock = Clock(BasketballPeriod.FourthQuarter, 331);

    BasketballPlayByPlayEvent[] events =
    [
        new FoulEvent(901, 1, ftClock, null, [new(home.TeamId, 101, null)], null, FoulType.Shooting, null),
        // Category is required explicitly; unresolved award cause is represented by NotSet.
        new FreeThrowEvent(902, 2, ftClock, null, away.TeamId, 202, ShotOutcome.Made, 1, 2, 901, FreeThrowCategory.NotSet),
        new SubstitutionEvent(903, 3, ftClock, null, home.TeamId, 103, 104),
        new FreeThrowEvent(904, 4, ftClock, null, away.TeamId, 202, ShotOutcome.Made, 2, 2, 901, FreeThrowCategory.NotSet),
        // Source teamId/location are unset for this coach row; do not infer a charged team from that row.
        new FoulEvent(905, 5, coachClock, null, [new(null, null, coach.CoachId)], null, FoulType.Technical, null),
        new FieldGoalEvent(906, 6, shotClock, null, home.TeamId, 101, FieldGoalType.TwoPointer,
            ShotOutcome.Made, 102, null, 4, null, ShotStyle.Layup, [ShotModifier.Driving], CourtArea.NotSet),
        new ViolationEvent(907, 7, shotClock, null, away.TeamId, 201, ViolationType.DefensiveGoaltending, 906, null)
    ];

    var shotRow = Row(snapshot, 416, "417", "582");

    var violationRow = Row(snapshot, 417, "418", "583");

    return new("ATL@BOS_20260128_0022500673.json", Game(1, "0022500673", home, away, events),
    [
        new(906, nameof(FieldGoalEvent.Outcome), BasketballEvidenceKind.Direct, [shotRow], "shotResult=Made; shotValue=2. No separate scoring award."),
        new(906, nameof(FieldGoalEvent.AssistingPlayerId), BasketballEvidenceKind.Parsed, [shotRow], "Description credits White with the assist; example identity 102 is curated from this game's participant rows."),
        new(906, nameof(FieldGoalEvent.ShotLocation), BasketballEvidenceKind.Unresolved, [shotRow], "Canonical transform of xLegacy/yLegacy is not verified; location remains null."),
        new(907, nameof(ViolationEvent.RelatedFieldGoalEventId), BasketballEvidenceKind.Linked, [shotRow, violationRow], "Manually inspected consecutive opposing-team shot/violation at Q4 5:31; the existing made shot carries the points."),
        new(905, nameof(FoulEvent.CommittingParticipants), BasketballEvidenceKind.Parsed, [Row(snapshot, 328, "329", "459")], "Description names Joe Mazzulla and personId=1628210; team is unresolved on this row, not player zero."),
        new(903, nameof(SubstitutionEvent.PlayerInId), BasketballEvidenceKind.Parsed, [Row(snapshot, 34, "35", "45")], "SUB: Garza FOR Williams supplies the incoming name; personId identifies outgoing Williams."),
        new(902, nameof(FreeThrowEvent.ParentFoulEventId), BasketballEvidenceKind.Linked,
            [Row(snapshot, 32, "33", "42"), Row(snapshot, 33, "34", "44")], "Selected shooting-foul/two-FT sequence manually associated; no missing field-goal attempt is manufactured.")
    ]);
}

static Scenario DoubleTechnical()
{
    const string snapshot = "sha256:29a95124c1254d2ff8ee08960397ae01cfdab01b901e94a1dff89bd17fe40aa7";

    var home = Team(21, "Brooklyn", "1610612751", Player(211, "Clowney", "1641730"));

    var away = Team(22, "Atlanta", "1610612737", Player(221, "Gueye", "1631243"));

    var clock = Clock(BasketballPeriod.ThirdQuarter, 337);

    BasketballPlayByPlayEvent[] events =
    [
        new FoulEvent(1001, 1, clock, null, [new(21, 211, null), new(22, 221, null)], null, FoulType.DoubleTechnical, null),
        new FoulEvent(1002, 2, clock with { ClockRemaining = TimeSpan.FromSeconds(335) }, null,
            [new(21, 211, null)], null, FoulType.Technical, null),
        new EjectionEvent(1003, 3, clock with { ClockRemaining = TimeSpan.FromSeconds(335) }, null, 21, 211, null)
    ];

    return new("ATL@BKN_20260403_0022501122.json", Game(2, "0022501122", home, away, events),
    [
        new(1001, nameof(FoulEvent.CommittingParticipants), BasketballEvidenceKind.Parsed, [Row(snapshot, 299, "300", "412")], "Double Technical - Clowney, Gueye names both charged participants; identities curated from the game's other rows."),
        new(1003, nameof(EjectionEvent.PlayerId), BasketballEvidenceKind.Direct, [Row(snapshot, 306, "307", "424")], "Clowney Ejection:Other; source personId maps to player 211.")
    ]);
}

static Scenario DoublePersonal()
{
    const string snapshot = "sha256:8be78eab417c2712d2a21f939786feb4ccd8108794007b765bf41421a0719e86";

    var home = Team(31, "Portland", "1610612757", Player(311, "Cissoko", "1631321"));

    var away = Team(32, "Brooklyn", "1610612751", Player(321, "Minott", "1631169"));

    BasketballPlayByPlayEvent[] events =
    [
        new FoulEvent(1101, 1, Clock(BasketballPeriod.ThirdQuarter, 493), null,
            [new(31, 311, null), new(32, 321, null)], null, FoulType.DoublePersonal, null)
    ];

    return new("BKN@POR_20260323_0022501045.json", Game(3, "0022501045", home, away, events),
    [
        new(1101, nameof(FoulEvent.CommittingParticipants), BasketballEvidenceKind.Parsed, [Row(snapshot, 330, "331", "475")], "Description lists Cissoko (3 PF), Minott (1 PF); both charge identities are retained, without storing cumulative foul totals.")
    ]);
}

static Scenario SecondOvertime()
{
    const string snapshot = "sha256:d81d921dcaa02a0922e8681a0903693bcd10f572b97687589d150b03030d7788";

    var home = Team(41, "Philadelphia", "1610612755", Player(411, "Bona", "1641737"));

    var away = Team(42, "Atlanta", "1610612737",
        Player(421, "Alexander-Walker", "1629638"), Player(422, "Daniels", "1630700"), Player(423, "Johnson", "1630552"));

    var clock = new BasketballClock(BasketballPeriod.Overtime, 2, TimeSpan.FromSeconds(59.6));

    BasketballPlayByPlayEvent[] events =
    [
        new PeriodBoundaryEvent(1201, 1, clock with { ClockRemaining = TimeSpan.FromMinutes(5) }, null, PeriodMarker.Start),
        new FieldGoalEvent(1202, 2, clock, null, 42, 421, FieldGoalType.TwoPointer, ShotOutcome.Missed,
            null, 411, 1, null, ShotStyle.Layup, [ShotModifier.Driving, ShotModifier.Reverse], CourtArea.NotSet),
        new ReboundEvent(1203, 3, clock, null, 42, null, ReboundType.NotSet, ReboundCredit.Team, 1202, null),
        new TimeoutEvent(1204, 4, clock, null, 42, TimeoutType.Team),
        new FieldGoalEvent(1205, 5, clock with { ClockRemaining = TimeSpan.FromSeconds(54.7) }, null,
            42, 422, FieldGoalType.TwoPointer, ShotOutcome.Made, 423, null, 3, null, ShotStyle.Layup, [], CourtArea.NotSet),
        new PeriodBoundaryEvent(1206, 6, clock with { ClockRemaining = TimeSpan.Zero }, null, PeriodMarker.End)
    ];

    return new("ATL@PHI_20251130_0022500304.json", Game(4, "0022500304", home, away, events),
    [
        new(1201, nameof(PeriodBoundaryEvent.Clock), BasketballEvidenceKind.Parsed, [Row(snapshot, 595, "596", "825")], "period=6 and Start of 2nd OT; source clock PT05M00.00S."),
        new(1202, nameof(FieldGoalEvent.Clock), BasketballEvidenceKind.Parsed, [Row(snapshot, 631, "632", "874")], "PT00M59.60S preserves fractional seconds in second overtime."),
        new(1202, nameof(FieldGoalEvent.BlockingPlayerId), BasketballEvidenceKind.Linked,
            [Row(snapshot, 631, "632", "874"), Row(snapshot, 632, "633", "874")], "Adjacent Bona BLOCK row supports this missed shot; shared actionNumber is not used as a unique ID or a second event."),
        new(1203, nameof(ReboundEvent.ReboundType), BasketballEvidenceKind.Unresolved, [Row(snapshot, 633, "634", "876")], "Team rebound is explicit; offensive/defensive classification is left unresolved without a verified rule."),
        new(1204, nameof(TimeoutEvent.TeamId), BasketballEvidenceKind.Parsed, [Row(snapshot, 634, "635", "878")], "Source teamId=0; personId=1610612737 and Hawks description identify the team."),
        new(1206, nameof(PeriodBoundaryEvent.OccurredAtUtc), BasketballEvidenceKind.Unresolved, [Row(snapshot, 650, "651", "897")], "Description provides minute-resolution 9:13 PM EST; no UTC date/time is invented here.")
    ]);
}

static Scenario CoachEjection()
{
    const string snapshot = "sha256:7b35ee7b1bea0e0e22eb4f63afbe7ab4c1888d5b07efa4dd90fa48c492658446";

    var coach = new BasketballGameCoach(501, "Rick Carlisle", [new(BasketballDataSource.NbaOfficial, "1320")]);

    var home = Team(51, "Indiana", "1610612754") with { Coaches = [coach] };

    var away = Team(52, "Brooklyn", "1610612751");

    BasketballPlayByPlayEvent[] events =
    [
        new EjectionEvent(1301, 1, Clock(BasketballPeriod.ThirdQuarter, 712), null, null, null, coach.CoachId)
    ];

    return new("BKN@IND_20251105_0022500173.json", Game(5, "0022500173", home, away, events),
    [
        new(1301, nameof(EjectionEvent.CoachId), BasketballEvidenceKind.Parsed, [Row(snapshot, 269, "270", "392")], "Rick Carlisle Ejection:Other; personId=1320 is a coach identity. Source charged team remains unset.")
    ]);
}

static BasketballGamePlayer Player(long id, string name, string sourceId) => new(id, name, [new(BasketballDataSource.NbaOfficial, sourceId)]);

static BasketballGameTeam Team(long id, string name, string sourceId, params BasketballGamePlayer[] players) => new(id, name, [new(BasketballDataSource.NbaOfficial, sourceId)], players);

static BasketballGamePlayByPlay Game(long id, string sourceId, BasketballGameTeam home, BasketballGameTeam away, BasketballPlayByPlayEvent[] events) => new(id, [new(BasketballDataSource.NbaOfficial, sourceId)], home, away, events);

static BasketballClock Clock(BasketballPeriod period, double seconds) => new(period, null, TimeSpan.FromSeconds(seconds));

static BasketballSourceRowReference Row(string snapshot, int index, string id, string number) => new(BasketballDataSource.NbaOfficial, snapshot, index, id, number);

internal sealed record Scenario(string SourceFile, BasketballGamePlayByPlay Game, IReadOnlyList<BasketballFieldEvidence> Evidence);
