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

    private static BasketballGameTeam Team(long id, int firstPlayer) => new(id, id.ToString(), [],
        Enumerable.Range(firstPlayer, 7).Select(p => new BasketballGamePlayer(p, p.ToString(), [])).ToArray());

    private static GameValidationContext Context() => new(true, false,
        [new(1, 10, [101, 102, 103, 104, 105], "Known synthetic opening-five fixture"),
         new(1, 20, [201, 202, 203, 204, 205], "Known synthetic opening-five fixture")], [], [], [], false, "No reference fixture.");

    private static BasketballClock Clock(double seconds, int period = 1) => new(period <= 4 ? (BasketballPeriod)period : BasketballPeriod.Overtime,
        period <= 4 ? null : period - 4, TimeSpan.FromSeconds(seconds));

    private static PeriodBoundaryEvent Boundary(int sequence, double seconds, PeriodMarker marker, int period = 1) => new(1000 + sequence, sequence, Clock(seconds, period), null, marker);

    private static FieldGoalEvent Shot(int sequence, double seconds) => new(1000 + sequence, sequence, Clock(seconds), null,
        10, 101, FieldGoalType.TwoPointer, ShotOutcome.Made, null, null, null, null, ShotStyle.JumpShot, [], CourtArea.NotSet, FieldGoalAttemptCredit.Player);
}
