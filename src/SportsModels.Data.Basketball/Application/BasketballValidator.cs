using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

/// <summary>Provider-independent diagnostic checks; uncertainty never changes canonical facts.</summary>
public sealed class BasketballValidator : IBasketballValidator
{
    public ValidationReport Validate(BasketballGamePlayByPlay game, GameValidationContext context)
    {
        var checks = new List<CheckResult>();
        CheckIdentities(game, checks);
        CheckTime(game, context, checks);
        CheckScores(game, context, checks);
        FoulDiagnostics.Check(game, context, checks);
        var stints = LineupDiagnostics.Reconstruct(game, context, checks);
        var statistics = GameStatistics.Derive(game, context, stints, checks);
        CheckReferences(statistics, context, checks);
        SequenceDiagnostics.Check(game, context, checks);
        return new(checks, statistics, stints);
    }

    private static void CheckIdentities(BasketballGamePlayByPlay game, List<CheckResult> checks)
    {
        var events = game.Events.GroupBy(e => e.EventId).ToDictionary(g => g.Key, g => g.First());
        var players = game.HomeTeam.Players.Concat(game.AwayTeam.Players).Select(p => p.PlayerId).ToHashSet();
        var coaches = game.HomeTeam.Coaches.Concat(game.AwayTeam.Coaches).Select(p => p.CoachId).ToHashSet();
        var teams = new HashSet<long> { game.HomeTeam.TeamId, game.AwayTeam.TeamId };
        var allIds = game.HomeTeam.Players.Concat(game.AwayTeam.Players).Select(p => p.PlayerId)
            .Concat(game.HomeTeam.Coaches.Concat(game.AwayTeam.Coaches).Select(c => c.CoachId))
            .Concat([game.HomeTeam.TeamId, game.AwayTeam.TeamId, game.GameId]).Concat(game.Events.Select(e => e.EventId)).ToArray();
        checks.Add(new("IDENTITY.UNIQUE", allIds.All(id => id > 0) && allIds.Distinct().Count() == allIds.Length
            ? CheckStatus.Pass : CheckStatus.Fail, "Internal IDs must be positive, allocated and distinct across entities.", Severity: FindingSeverity.Error));
        foreach (var item in game.Events)
        {
            foreach (var property in item.GetType().GetProperties().Where(p => p.Name != "EventId" && p.Name.EndsWith("Id", StringComparison.Ordinal)))
            {
                if (property.GetValue(item) is not long id) { continue; }
                var valid = property.Name.EndsWith("EventId", StringComparison.Ordinal) ? events.ContainsKey(id)
                    : property.Name.EndsWith("PlayerId", StringComparison.Ordinal) || property.Name is "PlayerInId" or "PlayerOutId" ? players.Contains(id)
                    : property.Name.EndsWith("CoachId", StringComparison.Ordinal) ? coaches.Contains(id)
                    : teams.Contains(id);
                if (!valid) { checks.Add(new("IDENTITY.REFERENCE", CheckStatus.Fail, $"Unresolved {property.Name}={id}.", item.EventId, Severity: FindingSeverity.Error)); }
                if (valid && property.Name.EndsWith("EventId", StringComparison.Ordinal))
                {
                    var target = events[id];
                    var typed = property.Name.Contains("FieldGoal", StringComparison.Ordinal) ? target is FieldGoalEvent
                        : property.Name.Contains("FreeThrow", StringComparison.Ordinal) ? target is FreeThrowEvent
                        : property.Name.Contains("Foul", StringComparison.Ordinal) ? target is FoulEvent : true;
                    if (!typed) { checks.Add(new("IDENTITY.LINK_TYPE", CheckStatus.Fail, $"{property.Name} points to {target.GetType().Name}.", item.EventId, Severity: FindingSeverity.Error)); }
                }
            }
            if (item is FieldGoalEvent { AttemptCredit: FieldGoalAttemptCredit.Player, ShooterPlayerId: null })
            {
                checks.Add(new("IDENTITY.SHOOTER", CheckStatus.Fail, "Player-credit attempt requires a resolved shooter.", item.EventId, Severity: FindingSeverity.Error));
            }
            if (item is FoulEvent foul)
            {
                foreach (var p in foul.CommittingParticipants)
                {
                    if (p.PlayerId is long player && !players.Contains(player) || p.CoachId is long coach && !coaches.Contains(coach)
                        || p.TeamId is long team && !teams.Contains(team) || p.PlayerId is not null && p.CoachId is not null)
                    {
                        checks.Add(new("IDENTITY.FOUL_PARTICIPANT", CheckStatus.Fail, "Charged actor metadata/type does not resolve.", item.EventId, Severity: FindingSeverity.Error));
                    }
                }
            }
        }
    }

    private static void CheckTime(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        BasketballPlayByPlayEvent? previous = null;
        foreach (var e in game.Events)
        {
            var period = Period(e.Clock);
            var maximum = period <= 4 ? 720 : 300;
            checks.Add(new("TIME.CLOCK_RANGE", !context.OrdinaryNbaRules ? CheckStatus.NotAssessable
                : e.Clock.ClockRemaining.TotalSeconds <= maximum && e.Clock.ClockRemaining >= TimeSpan.Zero ? CheckStatus.Pass : CheckStatus.Fail,
                context.OrdinaryNbaRules ? "NBA regulation 12 minutes, overtime 5 minutes." : "Unsupported competition/season duration.", e.EventId, period, Severity: FindingSeverity.Error));
            if (previous is not null)
            {
                var ordered = e.SequenceNumber > previous.SequenceNumber && period >= Period(previous.Clock)
                    && (period != Period(previous.Clock) || e.Clock.ClockRemaining <= previous.Clock.ClockRemaining);
                checks.Add(new("TIME.ORDER", ordered ? CheckStatus.Pass : CheckStatus.Fail,
                    "Original equal-clock sequence retained; clock increases require investigation, never automatic sorting.", e.EventId, period,
                    Severity: ordered ? null : FindingSeverity.Warning));
            }
            previous = e;
        }
        foreach (var group in game.Events.GroupBy(e => Period(e.Clock)))
        {
            var boundaries = group.OfType<PeriodBoundaryEvent>().ToArray();
            checks.Add(new("TIME.BOUNDARIES", boundaries.Count(e => e.Marker == PeriodMarker.Start) == 1
                && boundaries.Count(e => e.Marker == PeriodMarker.End) == 1 ? CheckStatus.Pass : CheckStatus.Fail,
                "One start and end per observed period; this does not establish final game status.", Period: group.Key, Severity: FindingSeverity.Warning));
        }
        checks.Add(new("TIME.COMPLETENESS", context.Final && context.OrdinaryNbaRules
            ? Enumerable.Range(1, game.Events.Select(e => Period(e.Clock)).DefaultIfEmpty(4).Max()).All(p => game.Events.Any(e => Period(e.Clock) == p))
                ? CheckStatus.Pass : CheckStatus.Fail : CheckStatus.NotAssessable,
            "Expected completed-game periods require independently verified final status and competition rules."));
    }

    private static void CheckScores(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        var home = 0;
        var away = 0;
        var known = true;
        var events = game.Events.GroupBy(e => e.SequenceNumber).ToDictionary(g => g.Key, g => g.First());
        var scores = context.Scores.ToLookup(s => s.SequenceNumber);
        var maximum = game.Events.Select(e => e.SequenceNumber).Concat(context.Scores.Select(s => s.SequenceNumber)).DefaultIfEmpty().Max();
        for (var sequence = 1; sequence <= maximum; sequence++)
        {
            if (context.Gaps.Any(g => g.SequenceNumber == sequence && g.Statistics.Contains("PTS"))) { known = false; }
            if (events.TryGetValue(sequence, out var item))
            {
                var (team, points) = Points(item);
                if (points is null) { known = false; }
                else if (team == game.HomeTeam.TeamId) { home += points.Value; }
                else if (team == game.AwayTeam.TeamId) { away += points.Value; }
            }
            foreach (var observation in scores[sequence])
            {
                var matches = (observation.Home is null || observation.Home == home) && (observation.Away is null || observation.Away == away);
                checks.Add(new("SCORE.OBSERVATION", !known ? CheckStatus.NotAssessable : matches ? CheckStatus.Pass : CheckStatus.Fail,
                    $"After-row observation home={observation.Home}, away={observation.Away}; effective stream home={home}, away={away}. Source corrections/unknown scoring are not synthetic adjustments.",
                    events.GetValueOrDefault(sequence)?.EventId, Expected: observation.Home is not null ? observation.Home : observation.Away,
                    Observed: observation.Home is not null ? home : away, Severity: known && !matches ? FindingSeverity.Warning : null, SourceRows: [observation.SourceRow]));
                if (known && !matches) { known = false; }
            }
        }
        if (context.Scores.Count == 0) { checks.Add(new("SCORE.OBSERVATION", CheckStatus.NotAssessable, "No source score observations.")); }
    }

    private static void CheckReferences(List<StatisticLine> derived, GameValidationContext context, List<CheckResult> checks)
    {
        if (context.ReferenceStatistics.Count == 0)
        {
            foreach (var stat in GameStatistics.CountingStats.Append("MIN").Append("+/-"))
            {
                checks.Add(new("BOX.RECONCILIATION", CheckStatus.NotAssessable, context.ReferenceLimitation, Stat: stat));
            }
            return;
        }
        foreach (var reference in context.ReferenceStatistics)
        {
            var line = derived.SingleOrDefault(x => x.TeamId == reference.TeamId && x.PlayerId == reference.PlayerId && x.Period == reference.Period);
            foreach (var stat in GameStatistics.CountingStats.Append("MIN").Append("+/-"))
            {
                var expected = reference.Values.GetValueOrDefault(stat);
                var actual = line?.Values.GetValueOrDefault(stat);
                var known = line is not null && !line.Unknown.Contains(stat) && !reference.Unknown.Contains(stat)
                    && reference.Values.ContainsKey(stat) && (stat != "MIN" || context.MinutesToleranceSeconds is not null);
                var matches = stat == "MIN" ? Math.Abs(expected - (actual ?? 0)) * 60 <= context.MinutesToleranceSeconds : expected == actual;
                // Minute precision/rounding has not been verified merely by receiving a value.
                checks.Add(new("BOX.RECONCILIATION", !known ? CheckStatus.NotAssessable : matches ? CheckStatus.Pass : CheckStatus.Fail,
                    stat == "MIN" ? context.MinutesPrecisionEvidence
                    : !known ? "Affected conversion/derived/reference coverage is unknown."
                    : "Comparable normalized counting statistics use exact equality; agreement is corroboration, not independent ground truth.",
                    Period: reference.Period, EntityId: reference.PlayerId ?? reference.TeamId, Stat: stat, Expected: expected, Observed: actual,
                    Severity: known && !matches ? FindingSeverity.Warning : null));
            }
        }
        foreach (var whole in context.ReferenceStatistics.Where(x => x.Period == 0))
        {
            var periods = context.ReferenceStatistics.Where(x => x.Period > 0 && x.TeamId == whole.TeamId && x.PlayerId == whole.PlayerId).ToArray();
            foreach (var stat in GameStatistics.CountingStats)
            {
                var complete = periods.Select(x => x.Period).Distinct().Count() == derived.Where(x => x.Period > 0).Select(x => x.Period).Distinct().Count()
                    && periods.Length > 0 && periods.All(x => !x.Unknown.Contains(stat) && x.Values.ContainsKey(stat)) && whole.Values.ContainsKey(stat);
                var sum = periods.Sum(x => x.Values.GetValueOrDefault(stat));
                checks.Add(new("BOX.PERIOD_SUM", !complete ? CheckStatus.NotAssessable : sum == whole.Values[stat] ? CheckStatus.Pass : CheckStatus.Fail,
                    "Only verified complete period reference sets are summed.", EntityId: whole.PlayerId ?? whole.TeamId, Stat: stat,
                    Expected: whole.Values.GetValueOrDefault(stat), Observed: complete ? sum : null, Severity: complete && sum != whole.Values.GetValueOrDefault(stat) ? FindingSeverity.Warning : null));
            }
        }
    }

    internal static int Period(BasketballClock clock) => clock.Period == BasketballPeriod.Overtime ? 4 + (clock.OvertimePeriodNumber ?? 0) : (int)clock.Period;

    internal static (long? Team, int? Points) Points(BasketballPlayByPlayEvent e) => e switch
    {
        FieldGoalEvent { Outcome: ShotOutcome.Made, FieldGoalType: FieldGoalType.TwoPointer } s => (s.TeamId, 2),
        FieldGoalEvent { Outcome: ShotOutcome.Made, FieldGoalType: FieldGoalType.ThreePointer } s => (s.TeamId, 3),
        FieldGoalEvent { Outcome: ShotOutcome.Missed } s => (s.TeamId, 0),
        FieldGoalEvent s => (s.TeamId, null),
        FreeThrowEvent { Outcome: ShotOutcome.Made } s => (s.TeamId, 1),
        FreeThrowEvent { Outcome: ShotOutcome.Missed } s => (s.TeamId, 0),
        FreeThrowEvent s => (s.TeamId, null),
        _ => (null, 0)
    };
}
