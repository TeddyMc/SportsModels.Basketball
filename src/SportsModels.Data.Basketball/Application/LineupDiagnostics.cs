using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class LineupDiagnostics
{
    public static List<PlayerStint> Reconstruct(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        var stints = new List<PlayerStint>();
        var closing = new Dictionary<long, (int Period, HashSet<long>? Players)>();
        foreach (var period in game.Events.GroupBy(e => BasketballValidator.Period(e.Clock)))
        {
            foreach (var team in new[] { game.HomeTeam, game.AwayTeam })
            {
                var opening = context.OpeningLineups.Where(x => x.Period == period.Key && x.TeamId == team.TeamId).ToArray();
                HashSet<long>? lineup = opening.Length == 1 && opening[0].Players.Distinct().Count() == 5
                    && opening[0].Players.All(p => team.Players.Any(x => x.PlayerId == p)) ? opening[0].Players.ToHashSet() : null;
                var confidence = lineup is not null && context.OrdinaryNbaRules;
                checks.Add(new("LINEUP.PERIOD_START", confidence ? CheckStatus.Pass : CheckStatus.NotAssessable,
                    confidence ? opening[0].Evidence : "No reliable opening five. Previous-period closing lineup is not assumed unchanged across the break.",
                    Period: period.Key, EntityId: team.TeamId));
                var previousClock = period.Key <= 4 ? 720.0 : 300.0;
                var anchoredAfterOpening = confidence && opening[0].AfterOpeningClockSubstitutions;
                var openingPlayers = lineup?.ToHashSet();
                var starts = period.OfType<PeriodBoundaryEvent>().Where(e => e.Marker == PeriodMarker.Start).ToArray();
                var ends = period.OfType<PeriodBoundaryEvent>().Where(e => e.Marker == PeriodMarker.End).ToArray();
                var completeBoundaries = starts.Length == 1 && starts[0].Clock.ClockRemaining.TotalSeconds == previousClock
                    && ends.Length == 1 && ends[0].Clock.ClockRemaining == TimeSpan.Zero;
                if (!context.OrdinaryNbaRules) { lineup = null; }
                var firstSequence = period.Min(e => e.SequenceNumber);
                var previousSequence = game.Events.Where(e => e.SequenceNumber < firstSequence).Select(e => e.SequenceNumber).DefaultIfEmpty(0).Max();
                foreach (var group in ClockGroups(period))
                {
                    var clock = group[0].Clock.ClockRemaining.TotalSeconds;
                    var substitutions = group.OfType<SubstitutionEvent>().Where(s => s.TeamId == team.TeamId).ToArray();
                    if (anchoredAfterOpening && clock == (period.Key <= 4 ? 720.0 : 300.0))
                    {
                        var previous = closing.GetValueOrDefault(team.TeamId);
                        var sourceGap = context.Gaps.Any(g => g.Lineup && g.SequenceNumber > previousSequence
                            && g.SequenceNumber <= group.Max(e => e.SequenceNumber));
                        var before = previous.Period == period.Key - 1 ? previous.Players?.ToHashSet() : null;
                        var status = before is null || sourceGap ? CheckStatus.NotAssessable
                            : ApplyBatch(before, substitutions) && before.SetEquals(openingPlayers!) ? CheckStatus.Pass : CheckStatus.Fail;
                        var message = "Prior closing five plus observed opening-clock net changes compared with corroborated post-change opening five. Reanchor after this clock; no intermediate lineup asserted.";
                        checks.Add(new("LINEUP.BREAK_TRANSITION", status, message, Period: period.Key, EntityId: team.TeamId,
                            Severity: status == CheckStatus.Fail ? FindingSeverity.Warning : null));
                        if (substitutions.Length > 0)
                        {
                            checks.Add(new("LINEUP.SUBSTITUTION", status, message, substitutions[0].EventId, period.Key, team.TeamId,
                                Severity: status == CheckStatus.Fail ? FindingSeverity.Warning : null));
                        }
                        lineup = openingPlayers!.ToHashSet();
                        previousSequence = group.Max(e => e.SequenceNumber);
                        CheckActors(group, team.TeamId, null, true, checks, period.Key);
                        continue;
                    }
                    if (context.Gaps.Any(g => g.Lineup && g.SequenceNumber <= group.Max(e => e.SequenceNumber)
                        && g.SequenceNumber > previousSequence)) { lineup = null; }
                    previousSequence = group.Max(e => e.SequenceNumber);
                    if (clock < previousClock && lineup is not null)
                    {
                        foreach (var player in lineup) { stints.Add(new(period.Key, team.TeamId, player, previousClock, clock)); }
                    }
                    if (clock > previousClock) { lineup = null; }
                    previousClock = clock;
                    if (lineup is null)
                    {
                        if (substitutions.Length > 0) { checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.NotAssessable, "Prior lineup unknown; no speculative reconstruction.", substitutions[0].EventId, period.Key, team.TeamId)); }
                        CheckActors(group, team.TeamId, null, substitutions.Length > 0, checks, period.Key);
                        continue;
                    }
                    if (substitutions.Length > 0)
                    {
                        var valid = ApplyBatch(lineup, substitutions);
                        if (!valid)
                        {
                            checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.Fail,
                                "Same-clock batch contradicts prior/final membership, repeats an identical change or has no compatible legal substitution order. Downstream lineup becomes unknown.",
                                substitutions[0].EventId, period.Key, team.TeamId, Severity: FindingSeverity.Error));
                            lineup = null;
                            CheckActors(group, team.TeamId, null, true, checks, period.Key);
                            continue;
                        }
                        checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.Pass, "Simultaneous substitutions validated as a batch; no intermediate timed lineup.",
                            substitutions[0].EventId, period.Key, team.TeamId));
                    }
                    if (lineup.Count != 5)
                    {
                        checks.Add(new("LINEUP.SIZE", CheckStatus.Fail, "Ordinary timed lineup does not contain five distinct players; investigate exceptional rules.", Period: period.Key, EntityId: team.TeamId, Severity: FindingSeverity.Warning));
                        lineup = null;
                        CheckActors(group, team.TeamId, null, false, checks, period.Key);
                        continue;
                    }
                    if (CheckActors(group, team.TeamId, lineup, substitutions.Length > 0, checks, period.Key)) { lineup = null; }
                }
                // Do not append an unobserved interval to zero; a missing period-end is a coverage gap.
                checks.Add(new("MINUTES.COVERAGE", confidence && completeBoundaries && lineup is not null && previousClock == 0 ? CheckStatus.Pass : CheckStatus.NotAssessable,
                    "Stints integrate observed decreasing playing clock only, split at period boundaries. Missing starts/ends or lineups prevent full-period totals.",
                    Period: period.Key, EntityId: team.TeamId));
                closing[team.TeamId] = (period.Key, previousClock == 0 && completeBoundaries ? lineup?.ToHashSet() : null);
            }
        }
        return stints;
    }

    private static bool CheckActors(BasketballPlayByPlayEvent[] group, long teamId, HashSet<long>? lineup,
        bool ambiguousSubstitutionClock, List<CheckResult> checks, int period)
    {
        var contradiction = false;
        foreach (var item in group)
        {
            foreach (var (player, statistic, required) in Actors(item, teamId))
            {
                var ambiguousActor = ambiguousSubstitutionClock && (lineup is null || group.OfType<SubstitutionEvent>()
                    .Any(s => s.TeamId == teamId && (s.PlayerInId == player || s.PlayerOutId == player)));
                var known = required && lineup is not null && !ambiguousActor;
                var matches = lineup?.Contains(player) == true;
                var status = !known ? CheckStatus.NotAssessable : matches ? CheckStatus.Pass : CheckStatus.Fail;
                var reason = !required ? "Foul/free-throw context permits bench or replacement exceptions, or is unclassified; no mandatory on-court assertion."
                    : ambiguousActor ? "Statistical credit shares this player's substitution clock; no unique before/after order is asserted."
                    : lineup is null ? "Inferred lineup is unknown after missing evidence or an earlier contradiction."
                    : matches ? "Attributed player belongs to the five inferred from opening evidence and observed substitutions."
                    : "Attributed player is outside the reliably inferred five. Event retained; later lineup/minute state becomes unknown until corroborated recovery.";
                checks.Add(new("LINEUP.ACTOR", status, reason, item.EventId, period, player, statistic,
                    Severity: status == CheckStatus.Fail ? FindingSeverity.Warning : null));
                contradiction |= status == CheckStatus.Fail;
            }
        }
        return contradiction;
    }

    private static IEnumerable<(long Player, string Statistic, bool Required)> Actors(BasketballPlayByPlayEvent item, long team)
    {
        switch (item)
        {
            case FieldGoalEvent shot:
                if (shot.TeamId == team)
                {
                    if (shot.ShooterPlayerId is long shooter) { yield return (shooter, "FGA", true); }
                    if (shot.AssistingPlayerId is long assist) { yield return (assist, "AST", true); }
                }
                else if (shot.BlockingPlayerId is long blocker) { yield return (blocker, "BLK", true); }
                break;
            case FreeThrowEvent freeThrow when freeThrow.TeamId == team:
                yield return (freeThrow.ShooterPlayerId, "FTA", freeThrow.Category == FreeThrowCategory.Ordinary);
                break;
            case TurnoverEvent turnover:
                if (turnover.TeamId == team && turnover.ResponsiblePlayerId is long responsible) { yield return (responsible, "TOV", true); }
                else if (turnover.TeamId != team && turnover.StealingPlayerId is long stealing) { yield return (stealing, "STL", true); }
                break;
            case ReboundEvent rebound when rebound.TeamId == team && rebound.Credit == ReboundCredit.Player && rebound.ReboundingPlayerId is long player:
                yield return (player, "REB", true);
                break;
            case FoulEvent foul when FoulDiagnostics.CountsPersonal(foul.FoulType):
                foreach (var charged in foul.CommittingParticipants.Where(p => p.TeamId == team && p.PlayerId is not null))
                { yield return (charged.PlayerId!.Value, "PF", foul.FoulType is not (FoulType.FlagrantOne or FoulType.FlagrantTwo)); }
                break;
        }
    }

    private static bool ApplyBatch(HashSet<long> lineup, SubstitutionEvent[] substitutions)
    {
        if (substitutions.Any(s => s.PlayerOutId == s.PlayerInId)
            || substitutions.Select(s => (s.PlayerOutId, s.PlayerInId)).Distinct().Count() != substitutions.Length) { return false; }
        var next = lineup.ToHashSet();
        foreach (var player in substitutions.SelectMany(s => new[] { s.PlayerOutId, s.PlayerInId }).Distinct())
        {
            var membership = (lineup.Contains(player) ? 1 : 0)
                + substitutions.Count(s => s.PlayerInId == player) - substitutions.Count(s => s.PlayerOutId == player);
            if (membership is < 0 or > 1) { return false; }
            if (membership == 1) { next.Add(player); } else { next.Remove(player); }
        }
        if (next.Count != 5) { return false; }
        if (!HasCompatibleOrder(lineup, substitutions)) { return false; }
        lineup.Clear();
        lineup.UnionWith(next);
        return true;
    }

    // Establish existence only: never publish an arbitrary same-clock ordering as observed history.
    private static bool HasCompatibleOrder(HashSet<long> initial, SubstitutionEvent[] substitutions)
    {
        if (substitutions.Select(s => s.PlayerOutId).Distinct().Count() == substitutions.Length
            && substitutions.Select(s => s.PlayerInId).Distinct().Count() == substitutions.Length
            && substitutions.All(s => initial.Contains(s.PlayerOutId) && !initial.Contains(s.PlayerInId))) { return true; }
        var rejected = new HashSet<string>();
        bool Search(HashSet<long> active, int[] remaining)
        {
            if (remaining.Length == 0) { return true; }
            var key = string.Join(',', remaining);
            if (rejected.Contains(key)) { return false; }
            foreach (var index in remaining)
            {
                var sub = substitutions[index];
                if (!active.Contains(sub.PlayerOutId) || active.Contains(sub.PlayerInId)) { continue; }
                var next = active.ToHashSet();
                next.Remove(sub.PlayerOutId);
                next.Add(sub.PlayerInId);
                if (Search(next, remaining.Where(i => i != index).ToArray())) { return true; }
            }
            rejected.Add(key);
            return false;
        }
        return Search(initial, Enumerable.Range(0,substitutions.Length).ToArray());
    }

    private static IEnumerable<BasketballPlayByPlayEvent[]> ClockGroups(IEnumerable<BasketballPlayByPlayEvent> events)
    {
        var group = new List<BasketballPlayByPlayEvent>();
        foreach (var item in events)
        {
            if (group.Count > 0 && group[0].Clock.ClockRemaining != item.Clock.ClockRemaining)
            { yield return group.ToArray(); group.Clear(); }
            group.Add(item);
        }
        if (group.Count > 0) { yield return group.ToArray(); }
    }
}
