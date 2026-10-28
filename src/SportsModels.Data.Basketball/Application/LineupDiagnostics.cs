using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class LineupDiagnostics
{
    public static List<PlayerStint> Reconstruct(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        var stints = new List<PlayerStint>();
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
                var starts = period.OfType<PeriodBoundaryEvent>().Where(e => e.Marker == PeriodMarker.Start).ToArray();
                var ends = period.OfType<PeriodBoundaryEvent>().Where(e => e.Marker == PeriodMarker.End).ToArray();
                var completeBoundaries = starts.Length == 1 && starts[0].Clock.ClockRemaining.TotalSeconds == previousClock
                    && ends.Length == 1 && ends[0].Clock.ClockRemaining == TimeSpan.Zero;
                if (!context.OrdinaryNbaRules) { lineup = null; }
                var previousSequence = period.Min(e => e.SequenceNumber) - 1;
                foreach (var group in ClockGroups(period))
                {
                    var clock = group[0].Clock.ClockRemaining.TotalSeconds;
                    if (context.Gaps.Any(g => g.Lineup && g.SequenceNumber <= group.Max(e => e.SequenceNumber)
                        && g.SequenceNumber > previousSequence)) { lineup = null; }
                    previousSequence = group.Max(e => e.SequenceNumber);
                    if (clock < previousClock && lineup is not null)
                    {
                        foreach (var player in lineup) { stints.Add(new(period.Key, team.TeamId, player, previousClock, clock)); }
                    }
                    if (clock > previousClock) { lineup = null; }
                    previousClock = clock;
                    var substitutions = group.OfType<SubstitutionEvent>().Where(s => s.TeamId == team.TeamId).ToArray();
                    if (lineup is null)
                    {
                        if (substitutions.Length > 0) { checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.NotAssessable, "Prior lineup unknown; no speculative reconstruction.", substitutions[0].EventId, period.Key, team.TeamId)); }
                        continue;
                    }
                    if (substitutions.Length > 0)
                    {
                        var outgoing = substitutions.Select(s => s.PlayerOutId).ToArray();
                        var incoming = substitutions.Select(s => s.PlayerInId).ToArray();
                        var valid = outgoing.Distinct().Count() == outgoing.Length && incoming.Distinct().Count() == incoming.Length
                            && substitutions.All(s => s.PlayerInId != s.PlayerOutId) && outgoing.All(lineup.Contains)
                            && incoming.All(p => !lineup.Contains(p));
                        if (!valid)
                        {
                            checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.Fail,
                                "Same-clock batch has duplicate/absent outgoing, already-active incoming or identical actors. Downstream lineup becomes unknown.",
                                substitutions[0].EventId, period.Key, team.TeamId, Severity: FindingSeverity.Error));
                            lineup = null;
                            continue;
                        }
                        lineup.ExceptWith(outgoing);
                        lineup.UnionWith(incoming);
                        checks.Add(new("LINEUP.SUBSTITUTION", CheckStatus.Pass, "Simultaneous substitutions validated as a batch; no intermediate timed lineup.",
                            substitutions[0].EventId, period.Key, team.TeamId));
                    }
                    if (lineup.Count != 5)
                    {
                        checks.Add(new("LINEUP.SIZE", CheckStatus.Fail, "Ordinary timed lineup does not contain five distinct players; investigate exceptional rules.", Period: period.Key, EntityId: team.TeamId, Severity: FindingSeverity.Warning));
                        lineup = null;
                        continue;
                    }
                    // Actors at a substitution clock can belong to either side of the batch; do not invent order.
                    if (substitutions.Length == 0)
                    {
                        foreach (var item in group)
                        {
                            var actors = item switch
                            {
                                FieldGoalEvent f when f.TeamId == team.TeamId => new[] { f.ShooterPlayerId, f.AssistingPlayerId },
                                FreeThrowEvent f when f.TeamId == team.TeamId && f.Category == FreeThrowCategory.Ordinary => [f.ShooterPlayerId],
                                TurnoverEvent t when t.TeamId == team.TeamId => [t.ResponsiblePlayerId],
                                ReboundEvent r when r.TeamId == team.TeamId && r.Credit == ReboundCredit.Player => [r.ReboundingPlayerId],
                                _ => Array.Empty<long?>()
                            };
                            foreach (var actor in actors.OfType<long>())
                            {
                                if (!lineup.Contains(actor))
                                {
                                    checks.Add(new("LINEUP.ACTOR", CheckStatus.Fail, "Required on-court actor absent; affected later lineup/minutes unknown.", item.EventId, period.Key, actor, Severity: FindingSeverity.Warning));
                                    lineup = null;
                                    break;
                                }
                            }
                            if (lineup is null) { break; }
                        }
                    }
                }
                // Do not append an unobserved interval to zero; a missing period-end is a coverage gap.
                checks.Add(new("MINUTES.COVERAGE", confidence && completeBoundaries && lineup is not null && previousClock == 0 ? CheckStatus.Pass : CheckStatus.NotAssessable,
                    "Stints integrate observed decreasing playing clock only, split at period boundaries. Missing starts/ends or lineups prevent full-period totals.",
                    Period: period.Key, EntityId: team.TeamId));
            }
        }
        return stints;
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
