using System.Text.Json;
using NUnit.Framework;
using SportsModels.Data.Basketball.Application;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Application;

namespace SportsModels.Data.NbaOfficial.Tests.Unit;

[TestFixture]
public sealed class PlayerMinuteTests
{
    [Test]
    public void TraditionalStarterFieldsPopulateRolesWithoutACdnBox()
    {
        var references = new ReferenceBundle();
        references.TraditionalBoxSnapshots.Add(("traditional", JsonSerializer.SerializeToElement(new
        {
            homeTeam = new { teamId = "10", players = Enumerable.Range(101, 6).Select(p => new
                { personId = p.ToString(), position = p <= 105 ? "G" : "", statistics = new { minutes = p == 101 ? "12:30" : "0:00" } }).ToArray() },
            awayTeam = new { teamId = "20", players = Array.Empty<object>() }
        })));
        var parser = new NbaRosterStatus(references);
        Assert.That(parser.Apply(Player(101), "10", "101").Started, Is.True);
        Assert.That(parser.Apply(Player(101), "10", "101").Eligibility, Is.EqualTo(PlayerEligibility.Active));
        Assert.That(parser.Apply(Player(101), "10", "101").Played, Is.True);
        var bench = parser.Apply(Player(106), "10", "106");
        Assert.That(bench.Started, Is.False);
        Assert.That(bench.Eligibility, Is.EqualTo(PlayerEligibility.Unknown));
        Assert.That(bench.StatusEvidence.Single().SnapshotId, Is.EqualTo("traditional"));
    }

    [Test]
    public void RecoveredQ1RolesAndParticipationKeepTheirDiagnosticProvenance()
    {
        var events = new BasketballPlayByPlayEvent[] { Boundary(1, 720, true), Sub(2, 480, 106, 101), Boundary(3, 0, false) };
        var game = new BasketballGamePlayByPlay(1, [], Team(10, 101), Team(20, 201), events);
        var report = new BasketballValidator().Validate(game, Context() with { OpeningLineups = [] });
        var recovered = RosterLabelRecovery.Apply(game, report);
        var starter = recovered.HomeTeam.Players.Single(p => p.PlayerId == 106);
        var bench = recovered.HomeTeam.Players.Single(p => p.PlayerId == 101);
        Assert.That(starter.Started, Is.True);
        Assert.That(bench.Started, Is.False);
        Assert.That(starter.Eligibility, Is.EqualTo(PlayerEligibility.Active));
        Assert.That(starter.Played, Is.True);
        Assert.That(starter.StatusEvidence.Single(e => e.FieldName == "Started").Origin, Is.EqualTo(MinuteEvidenceKind.InferredBreakChanges));
        Assert.That(starter.StatusEvidence.Single(e => e.FieldName == "Eligibility").EventIds, Does.Contain(events[1].EventId));
        Assert.That(recovered.HomeTeam.Players.Single(p => p.PlayerId == 107).Started, Is.Null);
        Assert.That(recovered.HomeTeam.Players.Single(p => p.PlayerId == 107).Eligibility, Is.EqualTo(PlayerEligibility.Unknown));
    }

    [Test]
    public void RecoveryDoesNotOverwriteExplicitInactiveOrConflictingRoleEvidence()
    {
        var game = new BasketballGamePlayByPlay(1, [], Team(10, 101) with
        {
            Players = Team(10, 101).Players.Select(p => p.PlayerId == 106 ? p with
            { Eligibility = PlayerEligibility.Inactive, Played = false,
                StatusEvidence = [new("Started", BasketballDataSource.NbaOfficial, "conflict", "$.starter", "0/1")] } : p).ToArray()
        }, Team(20, 201), [Boundary(1, 720, true), Sub(2, 480, 106, 101), Boundary(3, 0, false)]);
        var report = new BasketballValidator().Validate(game, Context() with { OpeningLineups = [] });
        var player = RosterLabelRecovery.Apply(game, report).HomeTeam.Players.Single(p => p.PlayerId == 106);
        Assert.That(player.Started, Is.Null);
        Assert.That(player.Eligibility, Is.EqualTo(PlayerEligibility.Inactive));
        Assert.That(player.Played, Is.False);
    }

    [Test]
    public void LaterPeriodParticipationDoesNotInventAStartingRole()
    {
        var game = new BasketballGamePlayByPlay(1, [], Team(10, 101), Team(20, 201),
            [Boundary(1, 720, true, 2), Sub(2, 480, 106, 101) with { Clock = Clock(2, 480) }, Boundary(3, 0, false, 2)]);
        var report = new BasketballValidator().Validate(game, Context() with { OpeningLineups = [] });
        var recovered = RosterLabelRecovery.Apply(game, report).HomeTeam.Players.Single(p => p.PlayerId == 106);
        Assert.That(recovered.Started, Is.Null);
        Assert.That(recovered.Eligibility, Is.EqualTo(PlayerEligibility.Active));
        Assert.That(recovered.Played, Is.True);
    }

    [Test]
    public void ActiveUnusedAndInactiveAreDifferentCanonicalFactsWithSnapshotEvidence()
    {
        var box = JsonSerializer.SerializeToElement(new
        {
            homeTeam = new { teamId = "10", players = new[]
            {
                new { personId = "1626171", status = "INACTIVE", starter = "0", played = "0", notPlayingReason = "INACTIVE_INJURY", notPlayingDescription = "Left Hamstring: Strain" },
                new { personId = "1630561", status = "ACTIVE", starter = "0", played = "0", notPlayingReason = "", notPlayingDescription = "" }
            } },
            awayTeam = new { teamId = "20", players = Array.Empty<object>() }
        });
        var references = new ReferenceBundle();
        references.LiveBoxSnapshots.Add(("retained-live-box-hash", box));
        var parser = new NbaRosterStatus(references);
        var portis = parser.Apply(Player(106), "10", "1626171");
        var duke = parser.Apply(Player(107), "10", "1630561");
        Assert.That(portis.Eligibility, Is.EqualTo(PlayerEligibility.Inactive));
        Assert.That(duke.Eligibility, Is.EqualTo(PlayerEligibility.Active));
        Assert.That(portis.Played, Is.False);
        Assert.That(duke.Played, Is.False);
        Assert.That(portis.NotPlayingReason, Is.EqualTo("INACTIVE_INJURY"));
        Assert.That(portis.StatusEvidence.All(e => e.SnapshotId == "retained-live-box-hash"), Is.True);
        Assert.That(portis.StatusEvidence.Single(e => e.FieldName == "Eligibility").SourcePath, Is.EqualTo("$.game.homeTeam.players[0].status"));
        var missing = parser.Apply(Player(108), "10", "not-present");
        Assert.That(missing.Eligibility, Is.EqualTo(PlayerEligibility.Unknown));
        Assert.That(missing.Started, Is.Null);
        Assert.That(missing.Played, Is.Null);
    }

    [Test]
    public void DisagreeingCompleteStarterSourcesLeaveOnlyAffectedRoleFlagsUnknown()
    {
        var references = new ReferenceBundle();
        object Team(bool live) => new { teamId = "10", players = Enumerable.Range(101, 6).Select(p => new
        { personId = p.ToString(), starter = live ? (p >= 102 ? "1" : "0") : (p <= 105 ? "1" : "0"), status = "ACTIVE", played = "1" }).ToArray() };
        var empty = new { teamId = "20", players = Array.Empty<object>() };
        references.LiveBoxSnapshots.Add(("live", JsonSerializer.SerializeToElement(new { homeTeam = Team(true), awayTeam = empty })));
        references.TraditionalBoxSnapshots.Add(("traditional", JsonSerializer.SerializeToElement(new { homeTeam = Team(false), awayTeam = empty })));
        var parser = new NbaRosterStatus(references);
        Assert.That(parser.Apply(Player(101), "10", "101").Started, Is.Null);
        Assert.That(parser.Apply(Player(106), "10", "106").Started, Is.Null);
        Assert.That(parser.Apply(Player(102), "10", "102").Started, Is.True);
        Assert.That(parser.Apply(Player(101), "10", "101").StatusEvidence.Where(e => e.FieldName == "Started").Select(e => e.SnapshotId),
            Is.EquivalentTo(new[] { "live", "traditional" }));
    }

    [Test]
    public void ConfirmedActiveUnusedIsARealZeroWhileInactiveIsNotBenchZero()
    {
        var home = Team(10, 101) with { Players = Team(10, 101).Players.Select(p => p.PlayerId == 106
            ? p with { Eligibility = PlayerEligibility.Active, Played = false, Started = false }
            : p.PlayerId == 107 ? p with { Eligibility = PlayerEligibility.Inactive, Played = false, Started = false } : p).ToArray() };
        var report = new BasketballValidator().Validate(new(1, [], home, Team(20, 201), []), Context() with { OpeningLineups = [] });
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Regulation).Seconds, Is.EqualTo(0));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Regulation).Availability, Is.EqualTo(MinuteAvailability.ConfirmedUnused));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 107 && p.Scope == MinuteScope.Regulation).Availability, Is.EqualTo(MinuteAvailability.Inactive));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 107 && p.Scope == MinuteScope.Regulation).Seconds, Is.Null);
    }

    [TestCase(PlayerEligibility.Active)]
    [TestCase(PlayerEligibility.Inactive)]
    public void ParticipationStatusConflictDoesNotErasePlausiblePositiveIntervals(PlayerEligibility eligibility)
    {
        var home = Team(10, 101) with { Players = Team(10, 101).Players.Select(p => p.PlayerId == 106
            ? p with { Eligibility = eligibility, Played = false, Started = false } : p).ToArray() };
        var events = new BasketballPlayByPlayEvent[] { Boundary(1, 720, true), Sub(2, 600, 101, 106), Sub(3, 300, 106, 101), Boundary(4, 0, false) };
        var report = new BasketballValidator().Validate(new(1, [], home, Team(20, 201), events), Context());
        var observation = report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Period && p.Period == 1);
        Assert.That(observation.Seconds, Is.EqualTo(300m));
        Assert.That(observation.Availability, Is.EqualTo(MinuteAvailability.WithComparisonWarnings));
    }

    [Test]
    public void ExplicitStintSurvivesUnknownOpeningAndUnknownTeammates()
    {
        var events = new BasketballPlayByPlayEvent[] { Boundary(1, 720, true), Sub(3, 300, 101, 106), Sub(4, 120, 106, 101), Boundary(5, 0, false) };
        var report = Validate(events, Context() with { OpeningLineups = [], Gaps = [new(2, ["MIN"], true, false, "Known missing change") { Period = 1, TeamId = 10 }] });
        var minute = report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Period && p.Period == 1);
        Assert.That(minute.Seconds, Is.Null);
        Assert.That(minute.EstablishedSeconds, Is.EqualTo(180m));
        Assert.That(minute.Availability, Is.EqualTo(MinuteAvailability.Unestablished));
        Assert.That(report.Stints.Where(s => s.PlayerId == 106).Sum(s => (decimal)s.StartRemaining - (decimal)s.EndRemaining), Is.EqualTo(180m));
    }

    [Test]
    public void FirstExitSupportsAnExplicitlyTaggedBreakInferenceWithoutChangingEvents()
    {
        var report = Validate([Boundary(1, 720, true), Sub(2, 480, 106, 101), Boundary(3, 0, false)], Context() with { OpeningLineups = [] });
        Assert.That(report.Stints.Where(s => s.PlayerId == 106).Sum(s => (decimal)s.StartRemaining - (decimal)s.EndRemaining), Is.EqualTo(240m));
        Assert.That(report.Stints.Where(s => s.PlayerId == 106).All(s => s.Origin == MinuteEvidenceKind.InferredBreakChanges), Is.True);
        Assert.That(report.OpeningMemberships.Single(m => m.PlayerId == 106 && m.Period == 1).Origin, Is.EqualTo(MinuteEvidenceKind.InferredBreakChanges));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Regulation).Seconds, Is.Null);
    }

    [Test]
    public void FiveDistinctFirstExitsEstablishAnInferredOpeningAndRemainingPlayersStayOff()
    {
        var events = new List<BasketballPlayByPlayEvent> { Boundary(1, 720, true) };
        for (var i = 0; i < 5; i++) { events.Add(Sub(i * 2 + 2, 600 - i * 50, 101 + i, 106)); events.Add(Sub(i * 2 + 3, 590 - i * 50, 106, 101 + i)); }
        events.Add(Boundary(20, 0, false));
        var report = Validate(events, Context() with { OpeningLineups = [] });
        var opening = report.ReferenceLineups.Single(l => l.TeamId == 10 && l.Period == 1);
        Assert.That(opening.Players, Is.EquivalentTo(new long[] { 101, 102, 103, 104, 105 }));
        Assert.That(opening.Origin, Is.EqualTo(MinuteEvidenceKind.InferredBreakChanges));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 107 && p.Scope == MinuteScope.Period && p.Period == 1).Seconds, Is.EqualTo(0m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 107 && p.Scope == MinuteScope.Period && p.Period == 1).EvidenceKinds,
            Does.Contain(MinuteEvidenceKind.InferredBreakChanges));
    }

    [Test]
    public void ConflictingStarterMetadataDoesNotOverrideConsistentWithinQuarterEvidence()
    {
        var events = new List<BasketballPlayByPlayEvent> { Boundary(1, 720, true) };
        for (var i = 0; i < 5; i++) { events.Add(Sub(i * 2 + 2, 600 - i * 50, 101 + i, 106)); events.Add(Sub(i * 2 + 3, 590 - i * 50, 106, 101 + i)); }
        events.Add(Boundary(20, 0, false));
        var home = Team(10, 101) with { Players = Team(10, 101).Players.Select(p => p with { Started = p.PlayerId is >= 102 and <= 106 }).ToArray() };
        var context = Context() with
        {
            OpeningLineups = [],
            ReconstructionChecks = [new("REFERENCE.STARTER_AGREEMENT", CheckStatus.Fail, "Conflicting retained starter sets", EntityId: 10)]
        };
        var report = new BasketballValidator().Validate(new(1, [], home, Team(20, 201), events), context);
        Assert.That(report.OpeningMemberships.Single(m => m.TeamId == 10 && m.PlayerId == 101).OnCourt, Is.True);
        Assert.That(report.OpeningMemberships.Single(m => m.TeamId == 10 && m.PlayerId == 101).Origin, Is.EqualTo(MinuteEvidenceKind.InferredBreakChanges));
        Assert.That(report.PlayerMinutes.Single(m => m.PlayerId == 101 && m.Scope == MinuteScope.Period && m.Period == 1).Seconds, Is.EqualTo(710m));
    }

    [Test]
    public void SixFirstExitCandidatesDoNotBecomeAnInventedOpeningFive()
    {
        var events = Enumerable.Range(0, 6).Select(i => (BasketballPlayByPlayEvent)Sub(i + 2, 600 - i * 20, 101 + i, 107)).Prepend(Boundary(1, 720, true)).Append(Boundary(20, 0, false)).ToArray();
        var report = Validate(events, Context() with { OpeningLineups = [] });
        Assert.That(report.ReferenceLineups.Any(l => l.TeamId == 10), Is.False);
        Assert.That(report.OpeningMemberships.Where(m => m.TeamId == 10).All(m => m.OnCourt is null), Is.True);
        Assert.That(report.PlayerMinutes.Any(p => p.TeamId == 10 && p.Scope == MinuteScope.Period && p.Availability == MinuteAvailability.Contradictory), Is.True);
    }

    [Test]
    public void SupportingFeedAndMissingBreakInferenceHaveDistinctTags()
    {
        var supplied = Validate([Boundary(1, 720, true), Boundary(2, 0, false)], Context());
        var inferred = Validate([Boundary(1, 720, true), Sub(2, 480, 106, 101), Boundary(3, 0, false)], Context() with { OpeningLineups = [] });
        Assert.That(supplied.Stints.All(s => s.Origin == MinuteEvidenceKind.SupportingFeed), Is.True);
        Assert.That(inferred.Stints.Where(s => s.PlayerId == 106).All(s => s.Origin == MinuteEvidenceKind.InferredBreakChanges), Is.True);
    }

    [Test]
    public void UnresolvedIncomingIdentityPreservesOtherPlayersAndKnownOutgoingMinutes()
    {
        var context = Context() with { Gaps = [new(2, ["MIN", "+/-"], true, false, "Unresolved incoming name")
            { Period = 1, TeamId = 10, RecordedPlayerOutId = 101, RemainingSeconds = 300 }] };
        var report = Validate([Boundary(1, 720, true), Boundary(3, 0, false)], context);
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.Period && p.Period == 1).Seconds, Is.EqualTo(420m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 103 && p.Scope == MinuteScope.Period && p.Period == 1).Seconds, Is.EqualTo(720m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 203 && p.Scope == MinuteScope.Period && p.Period == 1).Seconds, Is.EqualTo(720m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Period && p.Period == 1).Seconds, Is.Null);
        Assert.That(report.DerivedStatistics.Single(p => p.PlayerId == 103 && p.Period == 1).Unknown, Does.Not.Contain("MIN"));
    }

    [Test]
    public void AnUnresolvedTimedGapRetainsIntervalsBeforeIt()
    {
        var context = Context() with { Gaps = [new(2, ["MIN"], true, false, "Unknown change") { Period = 1, TeamId = 10, RemainingSeconds = 300 }] };
        var report = Validate([Boundary(1, 720, true), Boundary(3, 0, false)], context);
        var observation = report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.Period && p.Period == 1);
        Assert.That(observation.EstablishedSeconds, Is.EqualTo(420m));
        Assert.That(observation.Seconds, Is.Null);
    }

    [Test]
    public void ARequiredActionCanRecoverOnlyThePlayersLaterIntervalAfterAGap()
    {
        var shot = new FieldGoalEvent(1003, 3, Clock(1, 600), null, 10, 106, FieldGoalType.TwoPointer, ShotOutcome.Made,
            null, null, null, null, ShotStyle.JumpShot, [], CourtArea.NotSet, FieldGoalAttemptCredit.Player);
        var context = Context() with { OpeningLineups = [], Gaps = [new(1, ["MIN"], true, false, "Unknown earlier state") { Period = 1, TeamId = 10 }] };
        var report = Validate([Boundary(2, 720, true), shot, Sub(4, 300, 106, 101), Boundary(5, 0, false)], context);
        var observation = report.PlayerMinutes.Single(p => p.PlayerId == 106 && p.Scope == MinuteScope.Period && p.Period == 1);
        Assert.That(observation.Seconds, Is.Null);
        Assert.That(observation.EstablishedSeconds, Is.EqualTo(300m));
        Assert.That(report.Stints.Where(s => s.PlayerId == 106).All(s => s.StartRemaining <= 600), Is.True);
    }

    [Test]
    public void InvalidPairAffectsInvolvedPlayersWithoutDeletingOtherPlayersMinutes()
    {
        var report = Validate([Boundary(1, 720, true), Sub(2, 300, 101, 102), Boundary(3, 0, false)], Context());
        var bad = report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.Period && p.Period == 1);
        var unaffected = report.PlayerMinutes.Single(p => p.PlayerId == 103 && p.Scope == MinuteScope.Period && p.Period == 1);
        Assert.That(bad.Availability, Is.EqualTo(MinuteAvailability.Contradictory));
        Assert.That(bad.Seconds, Is.Null);
        Assert.That(unaffected.Seconds, Is.EqualTo(720m));
        Assert.That(unaffected.Availability, Is.EqualTo(MinuteAvailability.PlausibleUncorroborated));
    }

    [Test]
    public void MinuteComparisonWarningDoesNotRejectACompletePlayerReconstruction()
    {
        var events = Enumerable.Range(1, 4).SelectMany(p => new[] { Boundary(p * 2, 720, true, p), Boundary(p * 2 + 1, 0, false, p) }).ToArray();
        var context = Context() with
        {
            OpeningLineups = Enumerable.Range(1, 4).SelectMany(p => Context().OpeningLineups.Select(l => l with { Period = p })).ToArray(),
            ReferenceStatistics = [new(10, 101, 0, new Dictionary<string, double> { ["MIN"] = 48.02 }, [])],
            MinutesToleranceSeconds = 0.5
        };
        var report = Validate(events, context);
        var player = report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.FullGame);
        Assert.That(player.Seconds, Is.EqualTo(2880m));
        Assert.That(player.Availability, Is.EqualTo(MinuteAvailability.WithComparisonWarnings));
        Assert.That(report.DerivedStatistics.Single(p => p.PlayerId == 101 && p.Period == 0).Unknown, Does.Not.Contain("MIN"));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 102 && p.Scope == MinuteScope.FullGame).Availability, Is.EqualTo(MinuteAvailability.PlausibleUncorroborated));
    }

    [Test]
    public void RegulationAndOvertimeAreSeparateDecimalTotals()
    {
        var events = Enumerable.Range(1, 5).SelectMany(p => new[] { Boundary(p * 2, p <= 4 ? 720 : 300, true, p), Boundary(p * 2 + 1, 0, false, p) }).ToArray();
        var context = Context() with { OpeningLineups = Enumerable.Range(1, 5).SelectMany(p => Context().OpeningLineups.Select(l => l with { Period = p })).ToArray() };
        var report = Validate(events, context);
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.Regulation).Seconds, Is.EqualTo(2880m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.FullGame).Seconds, Is.EqualTo(3180m));
        Assert.That(report.PlayerMinutes.Single(p => p.PlayerId == 101 && p.Scope == MinuteScope.Period && p.Period == 5).Seconds, Is.EqualTo(300m));
    }

    private static BasketballGamePlayer Player(long id) => new(id, id.ToString(), []);
    private static BasketballGameTeam Team(long id, int first) => new(id, id.ToString(), [], Enumerable.Range(first, 7).Select(i => Player(i)).ToArray());
    private static GameValidationContext Context() => new(true, true,
        [new(1, 10, [101, 102, 103, 104, 105], "Known opening"), new(1, 20, [201, 202, 203, 204, 205], "Known opening")], [], [], [], false, "References unavailable.");
    private static ValidationReport Validate(IReadOnlyList<BasketballPlayByPlayEvent> events, GameValidationContext context) =>
        new BasketballValidator().Validate(new(1, [], Team(10, 101), Team(20, 201), events), context);
    private static BasketballClock Clock(int period, double seconds) => new(period <= 4 ? (BasketballPeriod)period : BasketballPeriod.Overtime,
        period <= 4 ? null : period - 4, TimeSpan.FromSeconds(seconds));
    private static PeriodBoundaryEvent Boundary(int sequence, double seconds, bool start, int period = 1) => new(1000 + sequence, sequence,
        Clock(period, seconds), null, start ? PeriodMarker.Start : PeriodMarker.End);
    private static SubstitutionEvent Sub(int sequence, double seconds, long outgoing, long incoming) => new(1000 + sequence, sequence, Clock(1, seconds), null, 10, outgoing, incoming);
}
