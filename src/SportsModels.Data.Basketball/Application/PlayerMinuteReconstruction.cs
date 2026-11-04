using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal sealed record PlayerPeriodMinutes(long Team, long Player, int Period, decimal Seconds, bool Complete,
    bool Contradictory, bool Unused, bool Inactive, IReadOnlyList<string> Reasons)
{
    public IReadOnlyList<MinuteEvidenceKind> EvidenceKinds { get; init; } = [];
}

internal static class PlayerMinuteReconstruction
{
    private sealed class State(bool? on)
    {
        public bool? On { get; set; } = on;
        public decimal UnknownSeconds { get; set; }
        public bool Contradictory { get; set; }
        public decimal Seconds { get; set; }
        public bool IsInferred { get; set; }
        public MinuteEvidenceKind Origin { get; set; }
        public HashSet<MinuteEvidenceKind> Kinds { get; } = [];
        public string Evidence { get; set; } = "Opening state unknown.";
        public HashSet<string> Reasons { get; } = [];
    }

    internal static (List<PlayerStint> Stints, List<PlayerPeriodMinutes> Periods) Reconstruct(
        BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        var stints = new List<PlayerStint>();
        var results = new List<PlayerPeriodMinutes>();
        if (!context.OrdinaryNbaRules)
        {
            foreach (var period in game.Events.Select(e => BasketballValidator.Period(e.Clock)).Distinct())
                foreach (var team in new[] { game.HomeTeam, game.AwayTeam })
                    foreach (var player in team.Players)
                        results.Add(new(team.TeamId, player.PlayerId, period, 0, false, false, false,
                            player.Eligibility == PlayerEligibility.Inactive, ["Competition period-duration/player-count rules are unsupported."]));
            return (stints, results);
        }
        var participants = new[] { game.HomeTeam, game.AwayTeam }.ToDictionary(t => t.TeamId, t => game.Events
            .SelectMany(e => LineupDiagnostics.Actors(e, t.TeamId).Where(a => a.Required).Select(a => a.Player)
                .Concat(e is SubstitutionEvent sub && sub.TeamId == t.TeamId ? new[] { sub.PlayerInId, sub.PlayerOutId } : []))
            .ToHashSet());
        var maximum = Math.Max(4, game.Events.Select(e => BasketballValidator.Period(e.Clock)).DefaultIfEmpty(4).Max());
        foreach (var period in Enumerable.Range(1, maximum))
        {
            var events = game.Events.Where(e => BasketballValidator.Period(e.Clock) == period).ToArray();
            foreach (var team in new[] { game.HomeTeam, game.AwayTeam })
            {
                var anchors = context.OpeningLineups.Where(a => a.TeamId == team.TeamId && a.Period == period).ToArray();
                var opening = anchors.Length == 1 && anchors[0].Players.Distinct().Count() == 5
                    && anchors[0].Players.All(p => team.Players.Any(x => x.PlayerId == p)) && context.OrdinaryNbaRules ? anchors[0] : null;
                var seeds = context.OpeningMemberships.Where(m => m.TeamId == team.TeamId && m.Period == period).ToDictionary(m => m.PlayerId);
                var states = team.Players.ToDictionary(p => p.PlayerId, p => new State(opening is null ? seeds.GetValueOrDefault(p.PlayerId)?.OnCourt : opening.Players.Contains(p.PlayerId))
                {
                    IsInferred = opening?.IsInferred == true || seeds.GetValueOrDefault(p.PlayerId)?.Origin == MinuteEvidenceKind.InferredBreakChanges,
                    Origin = opening?.Origin ?? seeds.GetValueOrDefault(p.PlayerId)?.Origin ?? MinuteEvidenceKind.Unknown,
                    Evidence = $"Opening membership p{period}/team{team.TeamId}/player{p.PlayerId}; see OpeningMemberships."
                });
                var duration = period <= 4 ? 720.0 : 300.0;
                var previous = duration;
                var first = events.Select(e => e.SequenceNumber).DefaultIfEmpty(0).Min();
                var previousSequence = game.Events.Where(e => e.SequenceNumber < first).Select(e => e.SequenceNumber).DefaultIfEmpty(0).Max();
                var clockBroken = false;
                void Integrate(double until)
                {
                    var elapsed = (decimal)previous - (decimal)until;
                    foreach (var (player, state) in states)
                    {
                        if (state.On is null) { state.UnknownSeconds += elapsed; }
                        else if (state.On == true && elapsed > 0)
                        {
                            state.Seconds += elapsed;
                            stints.Add(new(period, team.TeamId, player, previous, until) { Evidence = state.Evidence, IsInferred = state.IsInferred, Origin = state.Origin });
                        }
                        if (state.On is not null && elapsed > 0) { state.Kinds.Add(state.Origin); }
                    }
                    previous = until;
                }
                foreach (var group in Groups(events))
                {
                    var clock = group[0].Clock.ClockRemaining.TotalSeconds;
                    if (clock < 0 || clock > previous || clock > duration)
                    {
                        foreach (var state in states.Values) { Bad(state, "Clock reversal/range contradiction; later intervals in this period remain unestablished."); }
                        clockBroken = true;
                        break;
                    }
                    var gaps = context.Gaps.Where(g => g.Lineup && (g.TeamId is null || g.TeamId == team.TeamId) && (g.Period is null || g.Period == period)
                        && g.SequenceNumber > previousSequence && g.SequenceNumber <= group.Max(e => e.SequenceNumber)).ToArray();
                    foreach (var gap in gaps.OrderBy(g => g.SequenceNumber))
                    {
                        if (gap.RemainingSeconds is double at && at <= previous && at >= clock) { Integrate(at); }
                        if (gap.RecordedPlayerOutId is long outgoing && states.TryGetValue(outgoing, out var outState))
                        {
                            var contradictory = outState.On == false;
                            if (contradictory) { Bad(outState, "Unresolved substitution's known outgoing player contradicts established off-court membership."); }
                            foreach (var (player, state) in states)
                                if (player != outgoing && (contradictory || state.On != true))
                                { state.On = null; state.Reasons.Add("Incoming identity unresolved; this off/unknown player might have entered."); }
                            outState.On = false;
                            outState.Origin = MinuteEvidenceKind.ExplicitWithinPeriod;
                            outState.Evidence = $"Known outgoing actor at unresolved source sequence {gap.SequenceNumber}; no incoming identity invented.";
                        }
                        else foreach (var state in states.Values) { state.On = null; state.Reasons.Add(gap.Reason); }
                    }
                    Integrate(clock);
                    previousSequence = group.Max(e => e.SequenceNumber);
                    var substitutions = group.OfType<SubstitutionEvent>().Where(s => s.TeamId == team.TeamId).ToArray();
                    if (clock == duration && opening?.AfterOpeningClockSubstitutions == true)
                    {
                        foreach (var (player, state) in states) { state.On = opening.Players.Contains(player); }
                        continue;
                    }
                    var involved = substitutions.SelectMany(s => new[] { s.PlayerInId, s.PlayerOutId }).ToHashSet();
                    var knownBatch = involved.All(p => states.TryGetValue(p, out var s) && s.On is not null);
                    var impossible = substitutions.Any(s => s.PlayerInId == s.PlayerOutId)
                        || knownBatch && !LineupDiagnostics.HasCompatibleOrder(states.Where(kv => kv.Value.On == true).Select(kv => kv.Key).ToHashSet(), substitutions)
                        || checks.Any(c => c.Code == "LINEUP.SUBSTITUTION" && c.Status == CheckStatus.Fail && substitutions.Any(s => s.EventId == c.EventId));
                    foreach (var player in involved)
                    {
                        if (!states.TryGetValue(player, out var state)) { continue; }
                        var delta = substitutions.Count(s => s.PlayerInId == player) - substitutions.Count(s => s.PlayerOutId == player);
                        if (impossible || delta is < -1 or > 1 || delta == 1 && state.On == true || delta == -1 && state.On == false)
                        { Bad(state, "Recorded substitution batch contradicts this player's established state; available intervals are retained, not a complete trusted total."); }
                        if (impossible || delta is < -1 or > 1) { continue; }
                        if (delta == 1) { state.On = true; }
                        if (delta == -1) { state.On = false; }
                        if (delta != 0) { state.IsInferred = false; state.Origin = MinuteEvidenceKind.ExplicitWithinPeriod; }
                        state.Evidence = $"Recorded substitution event IDs {string.Join(',', substitutions.Where(s => s.PlayerInId == player || s.PlayerOutId == player).Select(s => s.EventId))}.";
                    }
                    foreach (var item in group)
                        foreach (var (player, _, required) in LineupDiagnostics.Actors(item, team.TeamId))
                            if (required && !involved.Contains(player) && !gaps.Any(g => g.RemainingSeconds == clock && (g.RecordedPlayerOutId is null || g.RecordedPlayerOutId == player))
                                && states.TryGetValue(player, out var state))
                            {
                                if (state.On == false)
                                {
                                    Bad(state, $"On-court statistical event {item.EventId} contradicts this player's established off-court state.");
                                    if (!checks.Any(c => c.Code == "LINEUP.ACTOR" && c.EventId == item.EventId && c.EntityId == player && c.Status == CheckStatus.Fail))
                                        checks.Add(new("PLAYER.ACTOR_CONTRADICTION", CheckStatus.Fail, "Individual off-court state contradicts a required on-court credit; no complete five is needed for this check.",
                                            item.EventId, period, player, Severity: FindingSeverity.Warning));
                                }
                                else if (state.On is null && !state.Contradictory)
                                {
                                    state.On = true;
                                    state.IsInferred = false;
                                    state.Origin = MinuteEvidenceKind.ExplicitWithinPeriod;
                                    state.Evidence = $"Required on-court actor event {item.EventId} establishes membership from this clock, not before it.";
                                }
                            }
                    if (states.Values.Count(s => s.On == true) > 5)
                    {
                        var direct = substitutions.Where(s => s.PlayerInId != s.PlayerOutId).Select(s => s.PlayerInId)
                            .Concat(group.SelectMany(e => LineupDiagnostics.Actors(e, team.TeamId).Where(a => a.Required).Select(a => a.Player)))
                            .Where(p => states.TryGetValue(p, out var s) && s.On == true).ToHashSet();
                        if (direct.Count > 5) { direct.Clear(); }
                        foreach (var (player, state) in states.Where(kv => kv.Value.On == true && !direct.Contains(kv.Key)))
                        {
                            state.On = null;
                            state.Reasons.Add("Recorded states would exceed five players; which earlier membership changed is unestablished. Available earlier intervals retained.");
                        }
                        checks.Add(new("PLAYER.STATE_SIZE", CheckStatus.Fail, "Independent player states exceed five; ambiguous earlier memberships become unknown rather than inventing exits.",
                            Period: period, EntityId: team.TeamId, Severity: FindingSeverity.Warning));
                    }
                }
                var completeBoundaries = events.OfType<PeriodBoundaryEvent>().Count(e => e.Marker == PeriodMarker.Start && e.Clock.ClockRemaining.TotalSeconds == duration) == 1
                    && events.OfType<PeriodBoundaryEvent>().Count(e => e.Marker == PeriodMarker.End && e.Clock.ClockRemaining == TimeSpan.Zero) == 1;
                foreach (var player in team.Players)
                {
                    var state = states[player.PlayerId];
                    var seconds = state.Seconds;
                    var participated = participants[team.TeamId].Contains(player.PlayerId);
                    var reportedPositive = context.ReferenceStatistics.Any(s => s.PlayerId == player.PlayerId && s.Values.TryGetValue("MIN", out var value) && value > 0);
                    var unused = context.Final && player.Eligibility == PlayerEligibility.Active && player.Played == false && !participated && seconds == 0 && !reportedPositive;
                    var inactive = player.Eligibility == PlayerEligibility.Inactive;
                    var complete = unused || context.OrdinaryNbaRules && completeBoundaries && !clockBroken
                        && state.UnknownSeconds == 0 && !state.Contradictory && previous == 0;
                    if (!completeBoundaries) { state.Reasons.Add("Period start/end boundaries are missing or ambiguous."); }
                    if (state.UnknownSeconds > 0 || opening is null && !unused) { state.Reasons.Add("Opening or intervening membership is unestablished; observed later intervals remain available."); }
                    if (unused) { state.Reasons.Add("Final game roster explicitly declares active and played=false; confirmed unused, not inactive."); }
                    if (inactive) { state.Reasons.Add("Game-specific roster explicitly declares inactive; do not admit as a bench-zero history observation."); }
                    if (player.Played == false && (participated || seconds > 0 || reportedPositive))
                        state.Reasons.Add("Reported played=false conflicts with participation evidence; a confirmed zero is not manufactured.");
                    if (unused) { state.Kinds.Add(MinuteEvidenceKind.SupportingFeed); }
                    results.Add(new(team.TeamId, player.PlayerId, period, seconds, complete, state.Contradictory, unused, inactive, state.Reasons.ToArray())
                    { EvidenceKinds = state.Kinds.Order().ToArray() });
                }
            }
        }
        return (stints, results);
    }

    internal static IReadOnlyList<PlayerMinuteObservation> Observations(IReadOnlyList<PlayerPeriodMinutes> periods,
        IReadOnlyList<CheckResult> checks, GameValidationContext context)
    {
        var result = new List<PlayerMinuteObservation>();
        foreach (var player in periods.GroupBy(p => (p.Team, p.Player)))
        {
            void Add(IEnumerable<PlayerPeriodMinutes> source, MinuteScope scope, int? period)
            {
                var selected = source.ToArray();
                var reasons = selected.SelectMany(p => p.Reasons).ToHashSet();
                var warnings = checks.Concat(context.ReconstructionChecks).Where(c => c.Status == CheckStatus.Fail && c.EntityId == player.Key.Player
                    && (c.Code is "BOX.RECONCILIATION" or "REFERENCE.ALTERNATE_MINUTES") && (c.Stat is "MIN" or "MIN_SECONDS")).ToArray();
                foreach (var warning in warnings) { reasons.Add(warning.Evidence); }
                var corroborated = checks.Any(c => c.Code == "BOX.RECONCILIATION" && c.Status == CheckStatus.Pass && c.EntityId == player.Key.Player && c.Stat == "MIN"
                    && (scope == MinuteScope.Period ? c.Period == period : c.Period == 0 && (scope == MinuteScope.FullGame || player.All(p => p.Period <= 4))));
                var inactive = selected.Any(p => p.Inactive);
                var bad = selected.Any(p => p.Contradictory);
                var complete = selected.Length > 0 && selected.All(p => p.Complete);
                var unused = selected.Length > 0 && selected.All(p => p.Unused);
                var status = inactive ? MinuteAvailability.Inactive : bad ? MinuteAvailability.Contradictory
                    : !complete ? MinuteAvailability.Unestablished : unused ? MinuteAvailability.ConfirmedUnused
                    : warnings.Length > 0 ? MinuteAvailability.WithComparisonWarnings : corroborated ? MinuteAvailability.Corroborated : MinuteAvailability.PlausibleUncorroborated;
                var seconds = selected.Sum(p => p.Seconds);
                result.Add(new(player.Key.Team, player.Key.Player, scope, period, complete && !inactive && !bad ? seconds : null, seconds, status, reasons.ToArray())
                { EvidenceKinds = selected.SelectMany(p => p.EvidenceKinds).Distinct().Order().ToArray() });
            }
            foreach (var period in player) { Add([period], MinuteScope.Period, period.Period); }
            Add(player.Where(p => p.Period <= 4), MinuteScope.Regulation, null);
            Add(player, MinuteScope.FullGame, null);
        }
        return result;
    }

    private static void Bad(State state, string reason) { state.Contradictory = true; state.On = null; state.Reasons.Add(reason); }

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
