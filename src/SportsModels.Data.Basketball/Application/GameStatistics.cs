using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class GameStatistics
{
    internal static readonly string[] CountingStats = ["PTS", "FGM", "FGA", "3PM", "3PA", "FTM", "FTA", "OREB", "DREB", "REB", "AST", "STL", "BLK", "TOV", "PF"];

    private sealed class Accumulator(long team, long? player, int period)
    {
        public long Team { get; } = team;
        public long? Player { get; } = player;
        public int Period { get; } = period;
        public Dictionary<string, double> Values { get; } = CountingStats.Append("MIN").Append("+/-").ToDictionary(s => s, _ => 0.0);
        public HashSet<string> Unknown { get; } = [];
        public void Add(string stat, double value = 1) => Values[stat] += value;
        public StatisticLine ToContracts() => new(Team, Player, Period, Values, Unknown.Order().ToArray());
    }

    internal static List<StatisticLine> Derive(BasketballGamePlayByPlay game, GameValidationContext context,
        List<PlayerStint> stints, List<CheckResult> checks)
    {
        var periods = game.Events.Select(e => BasketballValidator.Period(e.Clock)).Distinct().Append(0).Order().ToArray();
        var players = game.HomeTeam.Players.Select(p => (p.PlayerId, game.HomeTeam.TeamId))
            .Concat(game.AwayTeam.Players.Select(p => (p.PlayerId, game.AwayTeam.TeamId))).ToDictionary(p => p.PlayerId, p => p.TeamId);
        var data = new Dictionary<(long Team, long? Player, int Period), Accumulator>();
        foreach (var period in periods)
        {
            foreach (var team in new[] { game.HomeTeam, game.AwayTeam })
            {
                data[(team.TeamId, null, period)] = new(team.TeamId, null, period);
                foreach (var p in team.Players) { data[(team.TeamId, p.PlayerId, period)] = new(team.TeamId, p.PlayerId, period); }
            }
        }
        IEnumerable<Accumulator> Lines(long team, long? player, int period) => new[] { data[(team, player, period)], data[(team, player, 0)] }.Distinct();
        void Count(long team, long? player, int period, string stat, double value = 1)
        {
            foreach (var line in Lines(team, null, period)) { line.Add(stat, value); }
            if (player is long p && data.ContainsKey((team, p, period))) { foreach (var line in Lines(team, p, period)) { line.Add(stat, value); } }
        }
        void Unknown(long team, long? player, int period, params string[] stats)
        {
            foreach (var line in Lines(team, null, period).Concat(player is long p && data.ContainsKey((team, p, period)) ? Lines(team, p, period) : []))
            { line.Unknown.UnionWith(stats); }
        }

        foreach (var e in game.Events)
        {
            var period = BasketballValidator.Period(e.Clock);
            switch (e)
            {
                case FieldGoalEvent shot:
                    var credited = shot.AttemptCredit == FieldGoalAttemptCredit.Player ? shot.ShooterPlayerId : null;
                    if (shot.AttemptCredit == FieldGoalAttemptCredit.NotSet)
                    { Unknown(shot.TeamId, credited, period, "FGA", "FGM", "3PA", "3PM", "PTS"); break; }
                    // Team-only heave qualification/counting remains unverified; retain the observed attempt,
                    // but do not present reference-comparable team shooting totals as settled.
                    if (shot.AttemptCredit == FieldGoalAttemptCredit.Team)
                    { Unknown(shot.TeamId, null, period, "FGA", "FGM", "3PA", "3PM"); }
                    Count(shot.TeamId, credited, period, "FGA");
                    if (shot.FieldGoalType == FieldGoalType.ThreePointer) { Count(shot.TeamId, credited, period, "3PA"); }
                    if (shot.FieldGoalType == FieldGoalType.NotSet) { Unknown(shot.TeamId, credited, period, "3PA", "3PM", "PTS"); }
                    if (shot.Outcome == ShotOutcome.NotSet) { Unknown(shot.TeamId, credited, period, "FGM", "3PM", "PTS"); }
                    if (shot.Outcome == ShotOutcome.Made)
                    {
                        Count(shot.TeamId, credited, period, "FGM");
                        if (shot.FieldGoalType == FieldGoalType.ThreePointer) { Count(shot.TeamId, credited, period, "3PM"); }
                        var points = BasketballValidator.Points(shot).Points;
                        if (points is not null) { Count(shot.TeamId, credited, period, "PTS", points.Value); }
                        if (shot.AssistingPlayerId is long assist) { Count(shot.TeamId, assist, period, "AST"); }
                    }
                    if (shot.BlockingPlayerId is long blocker && players.TryGetValue(blocker, out var blockingTeam)) { Count(blockingTeam, blocker, period, "BLK"); }
                    break;
                case FreeThrowEvent ft:
                    Count(ft.TeamId, ft.ShooterPlayerId, period, "FTA");
                    if (ft.Outcome == ShotOutcome.Made) { Count(ft.TeamId, ft.ShooterPlayerId, period, "FTM"); Count(ft.TeamId, ft.ShooterPlayerId, period, "PTS"); }
                    if (ft.Outcome == ShotOutcome.NotSet) { Unknown(ft.TeamId, ft.ShooterPlayerId, period, "FTM", "PTS"); }
                    break;
                case TurnoverEvent turnover:
                    Count(turnover.TeamId, turnover.ResponsiblePlayerId, period, "TOV");
                    if (turnover.StealingPlayerId is long stealing && players.TryGetValue(stealing, out var stealingTeam)) { Count(stealingTeam, stealing, period, "STL"); }
                    break;
                case ReboundEvent rebound when rebound.TeamId is long team:
                    if (rebound.Credit == ReboundCredit.Player)
                    {
                        Count(team, rebound.ReboundingPlayerId, period, "REB");
                        if (rebound.ReboundType == ReboundType.Offensive) { Count(team, rebound.ReboundingPlayerId, period, "OREB"); }
                        else if (rebound.ReboundType == ReboundType.Defensive) { Count(team, rebound.ReboundingPlayerId, period, "DREB"); }
                        else { Unknown(team, rebound.ReboundingPlayerId, period, "OREB", "DREB"); }
                    }
                    else { Unknown(team, null, period, "OREB", "DREB", "REB"); }
                    break;
                case FoulEvent foul:
                    var personal = foul.FoulType is FoulType.Personal or FoulType.Shooting or FoulType.Offensive or FoulType.OffensiveCharge
                        or FoulType.LooseBall or FoulType.FlagrantOne or FoulType.FlagrantTwo or FoulType.ClearPath or FoulType.AwayFromPlay
                        or FoulType.TransitionTake or FoulType.PersonalTake or FoulType.DoublePersonal;
                    if (personal)
                    {
                        foreach (var actor in foul.CommittingParticipants)
                        {
                            if (actor.TeamId is long t && actor.PlayerId is long p) { Count(t, p, period, "PF"); }
                            else if (actor.TeamId is long tUnknown) { Unknown(tUnknown, null, period, "PF"); }
                        }
                    }
                    if (foul.FoulType is FoulType.NotSet or FoulType.Other)
                    { foreach (var line in data.Values.Where(x => x.Period == 0 || x.Period == period)) { line.Unknown.Add("PF"); } }
                    break;
            }
        }
        foreach (var line in data.Values)
        {
            var knownMinutes = checks.Where(c => c.Code == "MINUTES.COVERAGE" && c.EntityId == line.Team && (line.Period == 0 || c.Period == line.Period)).ToArray();
            if (knownMinutes.Length == 0 || knownMinutes.Any(c => c.Status != CheckStatus.Pass)) { line.Unknown.UnionWith(["MIN", "+/-"]); }
            line.Values["MIN"] = stints.Where(s => s.TeamId == line.Team && (line.Player is null || s.PlayerId == line.Player)
                && (line.Period == 0 || s.Period == line.Period)).Sum(s => (s.StartRemaining - s.EndRemaining) / 60);
            // Scoring attribution at a substitution clock is not inferred from positive-duration stints.
            line.Unknown.Add("+/-");
            foreach (var gap in context.Gaps)
            {
                var gapPeriod = game.Events.LastOrDefault(e => e.SequenceNumber <= gap.SequenceNumber)?.Clock;
                if (line.Period == 0 || gapPeriod is null || line.Period == BasketballValidator.Period(gapPeriod)) { line.Unknown.UnionWith(gap.Statistics); }
            }
            if (context.HasUnresolvedCredits) { line.Unknown.UnionWith(["STL", "BLK", "AST"]); }
            if (!context.OrdinaryNbaRules) { line.Unknown.Add("PF"); }
            var v = line.Values;
            var assessable = !line.Unknown.Overlaps(["PTS", "FGM", "3PM", "FTM"]);
            var arithmetic = v["PTS"] == 2 * (v["FGM"] - v["3PM"]) + 3 * v["3PM"] + v["FTM"];
            checks.Add(new("STAT.ARITHMETIC", !assessable ? CheckStatus.NotAssessable : arithmetic ? CheckStatus.Pass : CheckStatus.Fail,
                "PTS = 2*(FGM-3PM)+3*3PM+FTM; unknown scoring is not zero.", Period: line.Period, EntityId: line.Player ?? line.Team, Severity: assessable && !arithmetic ? FindingSeverity.Error : null));
            var shootingKnown = !line.Unknown.Overlaps(["FGM", "FGA", "3PM", "3PA", "FTM", "FTA"]);
            var subsets = v["FGM"] <= v["FGA"] && v["3PM"] <= v["3PA"] && v["3PA"] <= v["FGA"] && v["3PM"] <= v["FGM"] && v["FTM"] <= v["FTA"];
            checks.Add(new("STAT.SUBSETS", !shootingKnown ? CheckStatus.NotAssessable : subsets ? CheckStatus.Pass : CheckStatus.Fail,
                "Makes cannot exceed attempts; threes are a subset of field goals.", Period: line.Period, EntityId: line.Player ?? line.Team, Severity: shootingKnown && !subsets ? FindingSeverity.Error : null));
        }
        return data.Values.Select(x => x.ToContracts()).ToList();
    }
}
