using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class OpeningMembershipInference
{
    private const string Assumption = "Inferred missing break changes: assume no unrecorded within-period substitutions before the evidence; no synthetic events. Later contradictions invalidate only affected state.";

    internal static GameValidationContext Add(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        if (!context.OrdinaryNbaRules) { return context; }
        var full = context.OpeningLineups.ToList();
        var memberships = context.OpeningMemberships.ToList();
        foreach (var period in game.Events.GroupBy(e => BasketballValidator.Period(e.Clock)))
            foreach (var team in new[] { game.HomeTeam, game.AwayTeam })
            {
                var supplied = context.OpeningLineups.Where(l => l.TeamId == team.TeamId && l.Period == period.Key).ToArray();
                if (supplied.Length == 1 && supplied[0].Players.Distinct().Count() == 5 && supplied[0].Players.All(p => team.Players.Any(x => x.PlayerId == p)))
                {
                    memberships.AddRange(team.Players.Select(p => new OpeningPlayerMembership(period.Key, team.TeamId, p.PlayerId,
                        supplied[0].Players.Contains(p.PlayerId), supplied[0].Origin, supplied[0].Evidence)));
                    continue;
                }
                if (supplied.Length > 0) { continue; }
                var before = game.Events.Where(e => e.SequenceNumber < period.Min(p => p.SequenceNumber)).Select(e => e.SequenceNumber).DefaultIfEmpty(0).Max();
                var starterConflict = context.ReconstructionChecks.Any(c => c.Code == "REFERENCE.STARTER_AGREEMENT"
                    && c.Status == CheckStatus.Fail && c.EntityId == team.TeamId);
                var firstGap = context.Gaps.Where(g => g.Lineup && (g.Period == period.Key || g.Period is null
                    && g.SequenceNumber > before && g.SequenceNumber <= period.Max(e => e.SequenceNumber)))
                    .Where(g => g.TeamId is null || g.TeamId == team.TeamId).Select(g => g.SequenceNumber).DefaultIfEmpty(int.MaxValue).Min();
                var evidenceGroups = Groups(period).Select(group => new
                {
                    LastSequence = group.Max(e => e.SequenceNumber),
                    Substitutions = group.OfType<SubstitutionEvent>().Where(s => s.TeamId == team.TeamId)
                        .SelectMany(s => new[] { s.PlayerInId, s.PlayerOutId }.Distinct().Select(p => (Player: p, Substitution: s)))
                        .GroupBy(x => x.Player).ToDictionary(g => g.Key, g => g.Select(x => x.Substitution).ToArray()),
                    Actors = group.SelectMany(e => LineupDiagnostics.Actors(e, team.TeamId).Where(a => a.Required).Select(a => (a.Player, e.EventId)))
                        .GroupBy(x => x.Player).ToDictionary(g => g.Key, g => g.First().EventId)
                }).ToArray();
                var prior = new Dictionary<long, OpeningPlayerMembership>();
                foreach (var player in team.Players)
                {
                    bool? value = null;
                    var origin = MinuteEvidenceKind.Unknown;
                    var explanation = "No opening evidence or unique inference for this player.";
                    long? firstEvent = null;
                    if (period.Key == 1 && !starterConflict && player.Started is bool starter)
                    { value = starter; origin = MinuteEvidenceKind.SupportingFeed; explanation = "Explicit game-starter flag establishes Q1 membership only."; }
                    else
                    {
                        foreach (var group in evidenceGroups)
                        {
                            if (group.LastSequence >= firstGap) { break; }
                            var subs = group.Substitutions.GetValueOrDefault(player.PlayerId) ?? [];
                            if (subs.Length > 0)
                            {
                                var delta = subs.Count(s => s.PlayerInId == player.PlayerId) - subs.Count(s => s.PlayerOutId == player.PlayerId);
                                if (delta is not (-1 or 1)) { continue; }
                                value = delta == -1;
                                firstEvent = subs[0].EventId;
                                explanation = (value == true ? "First net change is an exit." : "First net change is an entry.") + " " + Assumption;
                            }
                            else
                            {
                                if (!group.Actors.TryGetValue(player.PlayerId, out var actor)) { continue; }
                                value = true;
                                firstEvent = actor;
                                explanation = "Required on-court action precedes any recorded membership change. " + Assumption;
                            }
                            origin = MinuteEvidenceKind.InferredBreakChanges;
                            break;
                        }
                    }
                    prior[player.PlayerId] = new(period.Key, team.TeamId, player.PlayerId, value, origin, explanation, firstEvent);
                }
                var count = prior.Values.Count(p => p.OnCourt == true);
                var unknown = prior.Values.Count(p => p.OnCourt is null);
                var conflict = count > 5 || count + unknown < 5;
                if (conflict)
                {
                    foreach (var key in prior.Keys.ToArray())
                        if (prior[key].Origin != MinuteEvidenceKind.SupportingFeed)
                            prior[key] = prior[key] with { OnCourt = null, Origin = MinuteEvidenceKind.Unknown,
                                Evidence = "First-action/first-change constraints conflict with five-player membership; do not invent an opening." };
                }
                else if (count == 5 || count + unknown == 5)
                {
                    foreach (var key in prior.Keys.Where(k => prior[k].OnCourt is null).ToArray())
                        prior[key] = prior[key] with { OnCourt = count != 5, Origin = MinuteEvidenceKind.InferredBreakChanges,
                            Evidence = "Unique remaining five-player constraint. " + Assumption };
                }
                memberships.AddRange(prior.Values);
                var opening = prior.Values.Where(p => p.OnCourt == true).Select(p => p.PlayerId).ToArray();
                if (!conflict && opening.Length == 5)
                    full.Add(new(period.Key, team.TeamId, opening, Assumption)
                    { IsInferred = true, Origin = MinuteEvidenceKind.InferredBreakChanges });
                checks.Add(new("LINEUP.OPENING_INFERENCE", !conflict && opening.Length == 5 ? CheckStatus.Pass : CheckStatus.NotAssessable,
                    conflict ? "Within-quarter constraints are inconsistent; opening remains ambiguous." : opening.Length == 5 ? Assumption
                        : "Some player memberships inferred independently; more than one complete opening five remains possible.", Period: period.Key, EntityId: team.TeamId));
            }
        return context with { OpeningLineups = full, OpeningMemberships = memberships };
    }

    private static IEnumerable<BasketballPlayByPlayEvent[]> Groups(IEnumerable<BasketballPlayByPlayEvent> events)
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
