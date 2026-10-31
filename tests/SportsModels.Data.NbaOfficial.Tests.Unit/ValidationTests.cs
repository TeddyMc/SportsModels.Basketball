using NUnit.Framework;
using SportsModels.Data.Basketball.Application;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class ValidationTests
{
    private readonly BasketballValidator validator = new();

    [Test]
    public void ClockIncreaseIsDetectedWithoutSorting()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), Shot(2, 600), Shot(3, 610)]);
        Assert.That(report.Checks.Any(c => c.Code == "TIME.ORDER" && c.Status == CheckStatus.Fail), Is.True);
    }

    [Test]
    public void ScoreMismatchHasOnePrimaryFaultAndDoesNotInventCorrectionEvents()
    {
        var events = new BasketballPlayByPlayEvent[] { Shot(1, 600), Shot(2, 500) };
        var report = Validate(events, Context() with { Scores = [new(1, 3, 0, 0), new(2, 5, 0, 1)] });
        Assert.That(report.Checks.Count(c => c.Code == "SCORE.OBSERVATION" && c.Status == CheckStatus.Fail), Is.EqualTo(1));
        Assert.That(report.Checks.Last(c => c.Code == "SCORE.OBSERVATION").Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(events, Has.Length.EqualTo(2));
    }

    [Test]
    public void UnknownHeaveDoesNotBecomeMissedThreeOrPassedScoring()
    {
        var heave = Shot(1, 1) with { ShooterPlayerId = null, AttemptCredit = FieldGoalAttemptCredit.Team,
            Outcome = ShotOutcome.NotSet, FieldGoalType = FieldGoalType.NotSet, ShotModifiers = [ShotModifier.Heave] };
        var report = Validate([heave], Context() with { Scores = [new(1, 0, 0, 0)] });
        Assert.That(report.Checks.Single(c => c.Code == "SCORE.OBSERVATION").Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(report.DerivedStatistics.Single(s => s.TeamId == 10 && s.PlayerId is null && s.Period == 0).Unknown, Does.Contain("PTS"));
        Assert.That(report.DerivedStatistics.Single(s => s.PlayerId == 101 && s.Period == 0).Values["FGA"], Is.Zero);
    }

    [Test]
    public void SameClockSubstitutionsAreValidatedAsOneBatch()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start),
            new SubstitutionEvent(1002, 2, Clock(600), null, 10, 101, 106),
            new SubstitutionEvent(1003, 3, Clock(600), null, 10, 102, 107), Boundary(4, 0, PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.SUBSTITUTION" && c.EntityId == 10).Status, Is.EqualTo(CheckStatus.Pass));
        Assert.That(report.DerivedStatistics.Single(s => s.TeamId == 10 && s.PlayerId is null && s.Period == 1).Values["MIN"], Is.EqualTo(60).Within(1e-8));
    }

    [Test]
    public void IllegalSubInvalidatesLaterMinutesWithoutCascadingErrors()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start),
            new SubstitutionEvent(1002, 2, Clock(600), null, 10, 106, 107),
            new SubstitutionEvent(1003, 3, Clock(500), null, 10, 101, 106), Boundary(4, 0, PeriodMarker.End)]);
        Assert.That(report.Checks.Count(c => c.Code == "LINEUP.SUBSTITUTION" && c.Status == CheckStatus.Fail), Is.EqualTo(1));
        Assert.That(report.Checks.Any(c => c.Code == "MINUTES.COVERAGE" && c.EntityId == 10 && c.Status == CheckStatus.NotAssessable), Is.True);
    }

    [Test]
    public void PreviousPeriodLineupIsNotAssumedToBeNextOpeningLineup()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), Boundary(2, 0, PeriodMarker.End),
            Boundary(3, 720, PeriodMarker.Start, 2), Boundary(4, 0, PeriodMarker.End, 2)]);
        Assert.That(report.Checks.Where(c => c.Code == "LINEUP.PERIOD_START" && c.Period == 2).All(c => c.Status == CheckStatus.NotAssessable), Is.True);
    }

    [Test]
    public void TechnicalFoulDoesNotIncreasePersonalFouls()
    {
        var foul = new FoulEvent(1001, 1, Clock(600), null, [new(10, 101, null)], null, FoulType.Technical, null);
        var report = Validate([foul], Context() with { FoulCounts = [new(1, 101, 0, 0)] });
        Assert.That(report.Checks.Single(c => c.Code == "FOUL.PERSONAL_OBSERVATION").Status, Is.EqualTo(CheckStatus.Pass));
    }

    [Test]
    public void IncorrectFoulDescriptionCountIsDetected()
    {
        var foul = new FoulEvent(1001, 1, Clock(600), null, [new(10, 101, null)], null, FoulType.Personal, null);
        var report = Validate([foul], Context() with { FoulCounts = [new(1, 101, 2, 0)] });
        Assert.That(report.Checks.Single(c => c.Code == "FOUL.PERSONAL_OBSERVATION").Status, Is.EqualTo(CheckStatus.Fail));
    }

    [Test]
    public void UnnumberedTechnicalFreeThrowDoesNotManufactureNumberingOrPossession()
    {
        var ft = new FreeThrowEvent(1001, 1, Clock(600), null, 10, 101, ShotOutcome.Made, null, null, null, FreeThrowCategory.Technical);
        var report = Validate([ft, Shot(2, 590)]);
        Assert.That(report.Checks.Single(c => c.Code == "FT.NUMBERING").Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(report.Checks.Single(c => c.Code == "POSSESSION.CONTROL").Status, Is.EqualTo(CheckStatus.NotAssessable));
    }

    [Test]
    public void MadeOrdinaryTerminalFreeThrowEstablishesOpponentEntitlement()
    {
        var ft = new FreeThrowEvent(1001, 1, Clock(600), null, 10, 101, ShotOutcome.Made, 2, 2, null, FreeThrowCategory.Ordinary);
        var report = Validate([ft, Shot(2, 590)]);
        Assert.That(report.Checks.Single(c => c.Code == "POSSESSION.CONTROL").Status, Is.EqualTo(CheckStatus.Fail));
    }

    [Test]
    public void SecondOvertimeUsesFiveMinutesAndPreservesFractionalStints()
    {
        var context = Context() with { OpeningLineups = Context().OpeningLineups.Select(x => x with { Period = 6 }).ToArray() };
        var report = Validate([Boundary(1, 300, PeriodMarker.Start, 6), Shot(2, 59.6) with { Clock = Clock(59.6, 6) }, Boundary(3, 0, PeriodMarker.End, 6)], context);
        Assert.That(report.Stints.Any(s => s.StartRemaining == 59.6 || s.EndRemaining == 59.6), Is.True);
        Assert.That(report.Checks.Where(c => c.Code == "MINUTES.TEAM_SUM").All(c => c.Status == CheckStatus.Pass), Is.True);
        Assert.That(report.DerivedStatistics.Single(s => s.TeamId == 10 && s.PlayerId is null && s.Period == 6).Values["MIN"], Is.EqualTo(25).Within(1e-8));
    }

    [Test]
    public void CountingStatisticsUseExactEqualityAndMissingReferencesCannotPass()
    {
        var reference = new StatisticLine(10, 101, 0, new Dictionary<string, double> { ["PTS"] = 3 }, []);
        var report = Validate([Shot(1, 600)], Context() with { ReferenceStatistics = [reference] });
        Assert.That(report.Checks.Single(c => c.Code == "BOX.RECONCILIATION" && c.Stat == "PTS").Status, Is.EqualTo(CheckStatus.Fail));
        Assert.That(Validate([Shot(1, 600)]).Checks.Where(c => c.Code == "BOX.RECONCILIATION").All(c => c.Status == CheckStatus.NotAssessable), Is.True);
    }

    [Test]
    public void PlusMinusUsesKnownLineupsAndUnknownSubstitutionClocksRemainUnknown()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), Shot(2, 600), Boundary(3, 0, PeriodMarker.End)]);
        var player = report.DerivedStatistics.Single(s => s.PlayerId == 101 && s.Period == 1);
        Assert.That(player.Unknown, Does.Not.Contain("+/-"));
        Assert.That(player.Values["+/-"], Is.EqualTo(2));
    }

    private ValidationReport Validate(IReadOnlyList<BasketballPlayByPlayEvent> events, GameValidationContext? context = null) =>
        validator.Validate(new(1, [], Team(10, 101), Team(20, 201), events), context ?? Context());

    [Test]
    public void JumpBallAndSameClockLostBallCreditAreOneControlChange()
    {
        var jump = new JumpBallEvent(1001, 1, Clock(500), null, 101, 201, 20, 202);
        var turnover = new TurnoverEvent(1002, 2, Clock(500), null, 10, 101, TurnoverType.LostBall, 201);
        var report = Validate([jump, turnover]);
        Assert.That(report.Checks.Any(c => c.Code == "POSSESSION.JUMP_TURNOVER" && c.Status == CheckStatus.Pass), Is.True);
        Assert.That(report.Checks.Any(c => c.Code == "POSSESSION.CONTROL" && c.Status == CheckStatus.Fail), Is.False);
    }

    [Test]
    public void DifferentClockTurnoverIsNotAutomaticallyGroupedWithJumpBall()
    {
        var jump = new JumpBallEvent(1001, 1, Clock(500), null, 101, 201, 20, 202);
        var turnover = new TurnoverEvent(1002, 2, Clock(490), null, 10, 101, TurnoverType.LostBall, 201);
        Assert.That(Validate([jump, turnover]).Checks.Any(c => c.Code == "POSSESSION.CONTROL" && c.Status == CheckStatus.Fail), Is.True);
    }

    [Test]
    public void ReferenceTurnoverDefinitionDoesNotEraseTeamOnlyTurnover()
    {
        var reference = new StatisticLine(10, null, 0, new Dictionary<string, double> { ["TOV"] = 0 }, [])
        { ComparisonKeys = new Dictionary<string, string> { ["TOV"] = "PLAYER_TOV" } };
        var turnover = new TurnoverEvent(1001, 1, Clock(500), null, 10, null, TurnoverType.ShotClock, null);
        var report = Validate([turnover], Context() with { ReferenceStatistics = [reference] });
        var team = report.DerivedStatistics.Single(s => s.TeamId == 10 && s.PlayerId is null && s.Period == 0);
        Assert.That(team.Values["TOV"], Is.EqualTo(1));
        Assert.That(team.Values["TEAM_ONLY_TOV"], Is.EqualTo(1));
        Assert.That(report.Checks.Single(c => c.Code == "BOX.RECONCILIATION" && c.Stat == "TOV").Status, Is.EqualTo(CheckStatus.Pass));
    }

    [Test]
    public void LocalScoreChecksRecoverAfterUnknownAttemptWithoutValidatingCumulativeTotals()
    {
        var unknown = Shot(1, 600) with { Outcome = ShotOutcome.NotSet };
        var report = Validate([unknown, Shot(2, 500)], Context() with { Scores = [new(1, 2, 0, 0), new(2, 4, 0, 1)] });
        Assert.That(report.Checks.Last(c => c.Code == "SCORE.OBSERVATION").Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(report.Checks.Single(c => c.Code == "SCORE.INTERVAL").Status, Is.EqualTo(CheckStatus.Pass));
    }

    [TestCase(600, CheckStatus.NotAssessable)]
    [TestCase(590, CheckStatus.Fail)]
    public void EjectionClockDoesNotMakeSameActionCreditProofOfLaterParticipation(int turnoverClock, CheckStatus expected)
    {
        var ejection = new EjectionEvent(1001, 1, Clock(600), null, 10, 101, null);
        var turnover = new TurnoverEvent(1002, 2, Clock(turnoverClock), null, 10, 101, TurnoverType.OffensiveFoul, null);
        Assert.That(Validate([ejection, turnover]).Checks.Single(c => c.Code == "FOUL.EJECTION_PARTICIPATION").Status, Is.EqualTo(expected));
    }

    [TestCase(null, CheckStatus.NotAssessable)]
    [TestCase(1.0, CheckStatus.Pass)]
    [TestCase(0.1, CheckStatus.Fail)]
    public void MinuteComparisonRequiresExplicitPrecisionEvidence(double? tolerance, CheckStatus expected)
    {
        var reference = new StatisticLine(10, 101, 1, new Dictionary<string, double> { ["MIN"] = 12.008 }, []);
        var context = Context() with { ReferenceStatistics = [reference], MinutesToleranceSeconds = tolerance,
            MinutesPrecisionEvidence = "Synthetic fixture with explicitly supplied precision bound." };
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), Boundary(2, 0, PeriodMarker.End)], context);
        Assert.That(report.Checks.Single(c => c.Code == "BOX.RECONCILIATION" && c.Stat == "MIN").Status, Is.EqualTo(expected));
    }

    [Test]
    public void MissingOpeningBoundaryCannotPassCompleteMinuteCoverage()
    {
        var report = Validate([Shot(1, 600), Boundary(2, 0, PeriodMarker.End)]);
        Assert.That(report.Checks.Where(c => c.Code == "MINUTES.COVERAGE").All(c => c.Status == CheckStatus.NotAssessable), Is.True);
    }

    [Test]
    public void TeamFoulsExcludeOrdinaryOffensiveAndDoublePersonalCharges()
    {
        var offensive = new FoulEvent(1002, 2, Clock(600), null, [new(10, 101, null)], null, FoulType.Offensive, null);
        var both = new FoulEvent(1003, 3, Clock(500), null, [new(10, 101, null), new(20, 201, null)], null, FoulType.DoublePersonal, null);
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), offensive, both], Context() with
        { TeamFouls = [new(2, 10, 0, false, 1), new(3, 10, 0, false, 2)] });
        Assert.That(report.Checks.Where(c => c.Code == "FOUL.TEAM_OBSERVATION").All(c => c.Status == CheckStatus.Pass), Is.True);
    }

    [Test]
    public void TeamPenaltyHonorsLastTwoMinutesAndResetsNextPeriod()
    {
        var first = new FoulEvent(1002, 2, Clock(110), null, [new(10, 101, null)], null, FoulType.Personal, null);
        var second = first with { EventId = 1003, SequenceNumber = 3, Clock = Clock(60) };
        var next = first with { EventId = 1006, SequenceNumber = 6, Clock = Clock(600, 2) };
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), first, second, Boundary(4, 0, PeriodMarker.End),
            Boundary(5, 720, PeriodMarker.Start, 2), next], Context() with
        { TeamFouls = [new(2, 10, 1, false, 1), new(3, 10, null, true, 2), new(6, 10, 1, false, 5)] });
        Assert.That(report.Checks.Where(c => c.Code == "FOUL.TEAM_OBSERVATION").All(c => c.Status == CheckStatus.Pass), Is.True);
    }

    [Test]
    public void IncorrectTeamFoulObservationIsDetected()
    {
        var foul = new FoulEvent(1002, 2, Clock(600), null, [new(10, 101, null)], null, FoulType.Personal, null);
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), foul], Context() with { TeamFouls = [new(2, 10, 2, false, 1)] });
        Assert.That(report.Checks.Single(c => c.Code == "FOUL.TEAM_OBSERVATION").Status, Is.EqualTo(CheckStatus.Fail));
    }

    private static BasketballGameTeam Team(long id, int firstPlayer) => new(id, id.ToString(), [],
        Enumerable.Range(firstPlayer, 7).Select(p => new BasketballGamePlayer(p, p.ToString(), [])).ToArray());

    [TestCase("FGA")]
    [TestCase("AST")]
    [TestCase("BLK")]
    [TestCase("STL")]
    [TestCase("TOV")]
    [TestCase("REB")]
    [TestCase("PF")]
    [TestCase("FTA")]
    public void StatisticalActorOutsideKnownFiveIsFlaggedAndLaterStateBecomesUnknown(string statistic)
    {
        var actorEvent = ActorEvent(statistic);
        var report = Validate([Boundary(1,720,PeriodMarker.Start), actorEvent,
            Shot(3,500), Boundary(4,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR" && c.EventId == actorEvent.EventId
            && c.EntityId == 106 && c.Stat == statistic).Status, Is.EqualTo(CheckStatus.Fail));
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR" && c.EventId == 1003).Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(report.Checks.Single(c => c.Code == "MINUTES.COVERAGE" && c.EntityId == 10).Status, Is.EqualTo(CheckStatus.NotAssessable));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void UnknownLineupOrSameClockSubstitutionDoesNotAssertOffCourtStat(bool sameClockSubstitution)
    {
        var items = new List<BasketballPlayByPlayEvent> { Boundary(1,720,PeriodMarker.Start) };
        if (sameClockSubstitution) { items.Add(new SubstitutionEvent(1099, 2, Clock(600), null, 10,101,106)); }
        items.Add(ActorEvent("FGA"));
        var context = sameClockSubstitution ? Context() : Context() with { OpeningLineups = [] };
        var report = Validate(items.ToArray(), context);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR").Status, Is.EqualTo(CheckStatus.NotAssessable));
    }

    [Test]
    public void BenchTechnicalAndTechnicalFreeThrowDoNotImplyIllegalOnCourtParticipation()
    {
        var foul = new FoulEvent(1002,2,Clock(600),null,[new(10,106,null)],null,FoulType.Bench,null);
        var freeThrow = (FreeThrowEvent)ActorEvent("FTA") with { EventId = 1003, SequenceNumber = 3, Category = FreeThrowCategory.Technical };
        var report = Validate([Boundary(1,720,PeriodMarker.Start),foul,freeThrow,Boundary(4,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Any(c => c.Code == "LINEUP.ACTOR" && c.Status == CheckStatus.Fail), Is.False);
        Assert.That(report.Checks.Single(c => c.Code == "MINUTES.COVERAGE" && c.EntityId == 10).Status, Is.EqualTo(CheckStatus.Pass));
    }

    [Test]
    public void DefensiveStatIsCheckedAgainstDefendersTeamRatherThanShootersFive()
    {
        var blocked = Shot(2,600) with { BlockingPlayerId = 201, Outcome = ShotOutcome.Missed };
        var report = Validate([Boundary(1,720,PeriodMarker.Start),blocked,Boundary(3,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR" && c.Stat == "BLK").Status, Is.EqualTo(CheckStatus.Pass));
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR" && c.Stat == "BLK").EntityId, Is.EqualTo(201));
    }

    [TestCase(false, CheckStatus.Fail)]
    [TestCase(true, CheckStatus.Pass)]
    public void StatAfterSubstitutionUsesChangedFive(bool incomingScores, CheckStatus expected)
    {
        var shot = Shot(3,500) with { ShooterPlayerId = incomingScores ? 106 : 101 };
        var report = Validate([Boundary(1,720,PeriodMarker.Start),new SubstitutionEvent(1002,2,Clock(600),null,10,101,106),
            shot,Boundary(4,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR").Status, Is.EqualTo(expected));
    }

    [Test]
    public void UnchangedPlayerCanBeAssessedAtAnotherPlayersSubstitutionClock()
    {
        var report = Validate([Boundary(1,720,PeriodMarker.Start),new SubstitutionEvent(1002,2,Clock(600),null,10,102,106),
            Shot(3,600),Boundary(4,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR").Status, Is.EqualTo(CheckStatus.Pass));
    }

    [Test]
    public void UnresolvedOpeningChangeBeforeFirstMappedEventCannotSilentlyValidateLineup()
    {
        var report = Validate([Boundary(2,720,PeriodMarker.Start),Shot(3,600),Boundary(4,0,PeriodMarker.End)],
            Context() with { Gaps = [new(1,["MIN"],true,false,"Unresolved substitution before mapped start marker")] });
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.ACTOR").Status, Is.EqualTo(CheckStatus.NotAssessable));
        Assert.That(report.Checks.Single(c => c.Code == "MINUTES.COVERAGE" && c.EntityId == 10).Status, Is.EqualTo(CheckStatus.NotAssessable));
    }

    private static BasketballPlayByPlayEvent ActorEvent(string statistic) => statistic switch
    {
        "FGA" => Shot(2,600) with { ShooterPlayerId = 106 },
        "AST" => Shot(2,600) with { AssistingPlayerId = 106 },
        "BLK" => Shot(2,600) with { TeamId = 20, ShooterPlayerId = 201, BlockingPlayerId = 106, Outcome = ShotOutcome.Missed },
        "STL" => new TurnoverEvent(1002,2,Clock(600),null,20,201,TurnoverType.BadPass,106),
        "TOV" => new TurnoverEvent(1002,2,Clock(600),null,10,106,TurnoverType.BadPass,null),
        "REB" => new ReboundEvent(1002,2,Clock(600),null,10,106,ReboundType.Defensive,ReboundCredit.Player,null,null),
        "PF" => new FoulEvent(1002,2,Clock(600),null,[new(10,106,null)],null,FoulType.Personal,null),
        "FTA" => new FreeThrowEvent(1002,2,Clock(600),null,10,106,ShotOutcome.Made,1,1,null,FreeThrowCategory.Ordinary),
        _ => throw new ArgumentException("Unsupported test statistic")
    };

    [Test]
    public void ZeroTimeRoundTripDoesNotCreateMinutesOrInvalidateFive()
    {
        var report = Validate([Boundary(1, 720, PeriodMarker.Start),
            new SubstitutionEvent(1002, 2, Clock(600), null, 10, 101, 106),
            new SubstitutionEvent(1003, 3, Clock(600), null, 10, 106, 101), Boundary(4, 0, PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.SUBSTITUTION").Status, Is.EqualTo(CheckStatus.Pass));
        Assert.That(report.Stints.Any(s => s.PlayerId == 106), Is.False);
    }

    [TestCase(106,107)]
    [TestCase(101,102)]
    public void NetZeroBatchWithNoPossibleLegalOrderDoesNotPass(int first, int second)
    {
        var report = Validate([Boundary(1,720,PeriodMarker.Start),new SubstitutionEvent(1002,2,Clock(600),null,10,first,second),
            new SubstitutionEvent(1003,3,Clock(600),null,10,second,first),Boundary(4,0,PeriodMarker.End)]);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.SUBSTITUTION").Status, Is.EqualTo(CheckStatus.Fail));
    }

    [TestCase(false, CheckStatus.Pass)]
    [TestCase(true, CheckStatus.NotAssessable)]
    public void PostChangeOpeningAnchorIsNotAppliedTwiceAndCanRecoverAfterBreakGap(bool gap, CheckStatus expected)
    {
        var context = Context() with
        {
            OpeningLineups = Context().OpeningLineups.Concat(new[] { new LineupEvidence(2, 10, [106,102,103,104,105], "Corroborated alternate opening")
                { AfterOpeningClockSubstitutions = true } }).ToArray(),
            Gaps = gap ? [new(4, ["MIN"], true, false, "Unresolved break row")] : []
        };
        var report = Validate([Boundary(1, 720, PeriodMarker.Start), Boundary(2, 0, PeriodMarker.End),
            Boundary(3, 720, PeriodMarker.Start, 2), new SubstitutionEvent(1004, 4, Clock(720, 2), null, 10, 101, 106),
            Boundary(5, 0, PeriodMarker.End, 2)], context);
        Assert.That(report.Checks.Single(c => c.Code == "LINEUP.BREAK_TRANSITION").Status, Is.EqualTo(expected));
        Assert.That(report.Checks.Single(c => c.Code == "MINUTES.COVERAGE" && c.Period == 2 && c.EntityId == 10).Status, Is.EqualTo(CheckStatus.Pass));
        Assert.That(report.Stints.Where(s => s.Period == 2 && s.PlayerId == 106).Sum(s => s.StartRemaining-s.EndRemaining), Is.EqualTo(720));
        Assert.That(report.ReferenceLineups, Has.Count.EqualTo(3));
    }

    private static GameValidationContext Context() => new(true, false,
        [new(1, 10, [101, 102, 103, 104, 105], "Known synthetic opening-five fixture"),
         new(1, 20, [201, 202, 203, 204, 205], "Known synthetic opening-five fixture")], [], [], [], false, "No reference fixture.");

    private static BasketballClock Clock(double seconds, int period = 1) => new(period <= 4 ? (BasketballPeriod)period : BasketballPeriod.Overtime,
        period <= 4 ? null : period - 4, TimeSpan.FromSeconds(seconds));

    private static PeriodBoundaryEvent Boundary(int sequence, double seconds, PeriodMarker marker, int period = 1) => new(1000 + sequence, sequence, Clock(seconds, period), null, marker);

    private static FieldGoalEvent Shot(int sequence, double seconds) => new(1000 + sequence, sequence, Clock(seconds), null,
        10, 101, FieldGoalType.TwoPointer, ShotOutcome.Made, null, null, null, null, ShotStyle.JumpShot, [], CourtArea.NotSet, FieldGoalAttemptCredit.Player);
}
