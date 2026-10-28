using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class FoulDiagnostics
{
    internal static bool CountsPersonal(FoulType type) => type is FoulType.Personal or FoulType.Shooting or FoulType.Offensive
        or FoulType.OffensiveCharge or FoulType.LooseBall or FoulType.FlagrantOne or FoulType.FlagrantTwo or FoulType.ClearPath
        or FoulType.AwayFromPlay or FoulType.TransitionTake or FoulType.PersonalTake or FoulType.DoublePersonal;

    internal static void Check(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        var counts = new Dictionary<long, int>();
        var known = context.OrdinaryNbaRules;
        var ejected = new Dictionary<long, BasketballClock>();
        foreach (var item in game.Events)
        {
            if (context.Gaps.Any(g => g.SequenceNumber <= item.SequenceNumber && g.Statistics.Contains("PF"))) { known = false; }
            if (item is FoulEvent foul)
            {
                if (foul.FoulType is FoulType.Other or FoulType.NotSet) { known = false; }
                if (CountsPersonal(foul.FoulType))
                {
                    foreach (var actor in foul.CommittingParticipants)
                    {
                        if (actor.PlayerId is long player) { counts[player] = counts.GetValueOrDefault(player) + 1; }
                        else { known = false; }
                    }
                }
            }
            foreach (var observation in context.FoulCounts.Where(x => x.SequenceNumber == item.SequenceNumber))
            {
                var actual = counts.GetValueOrDefault(observation.PlayerId);
                checks.Add(new("FOUL.PERSONAL_OBSERVATION", !known ? CheckStatus.NotAssessable : actual == observation.PersonalFouls ? CheckStatus.Pass : CheckStatus.Fail,
                    "Compare after-row personal-foul description count; technical/bench charges do not automatically increment personal fouls.", item.EventId,
                    BasketballValidator.Period(item.Clock), observation.PlayerId, "PF", observation.PersonalFouls, actual,
                    known && actual != observation.PersonalFouls ? FindingSeverity.Warning : null, [observation.SourceRow]));
            }
            if (item is EjectionEvent { PlayerId: long playerEjected }) { ejected[playerEjected] = item.Clock; }
            if (item is ReviewEvent) { ejected.Clear(); }
            long? actorId = item switch
            {
                FieldGoalEvent shot => shot.ShooterPlayerId,
                FreeThrowEvent ft => ft.ShooterPlayerId,
                TurnoverEvent turnover => turnover.ResponsiblePlayerId,
                SubstitutionEvent sub => sub.PlayerInId,
                _ => null
            };
            if (actorId is long actorPlayer && ejected.TryGetValue(actorPlayer, out var ejectionClock))
            {
                var sameActionClock = item.Clock == ejectionClock;
                checks.Add(new("FOUL.EJECTION_PARTICIPATION", sameActionClock ? CheckStatus.NotAssessable : CheckStatus.Fail,
                    sameActionClock ? "Same-clock statistical credit can describe the action that caused ejection; source row order alone does not prove later participation."
                        : "Player participates at a later clock after an observed ejection with no intervening review; investigate source revision/ruling.",
                    item.EventId, BasketballValidator.Period(item.Clock), actorPlayer, Severity: sameActionClock ? null : FindingSeverity.Warning));
            }
        }
        if (context.FoulCounts.Count == 0) { checks.Add(new("FOUL.PERSONAL_OBSERVATION", CheckStatus.NotAssessable, "No comparable personal-foul count observations.")); }
        CheckTeamFouls(game, context, checks);
    }

    private static void CheckTeamFouls(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        foreach (var period in game.Events.GroupBy(e => BasketballValidator.Period(e.Clock)))
        {
            var counts = new Dictionary<long, int>();
            var lateCounts = new Dictionary<long, int>();
            var known = context.OrdinaryNbaRules && period.OfType<PeriodBoundaryEvent>().Any(b => b.Marker == PeriodMarker.Start);
            var previousSequence = period.Min(e => e.SequenceNumber) - 1;
            foreach (var item in period)
            {
                if (context.Gaps.Any(g => g.SequenceNumber > previousSequence && g.SequenceNumber <= item.SequenceNumber && g.Statistics.Contains("PF"))) { known = false; }
                previousSequence = item.SequenceNumber;
                if (item is FoulEvent foul)
                {
                    if (foul.FoulType is FoulType.Other or FoulType.NotSet) { known = false; }
                    var teamCharge = CountsPersonal(foul.FoulType) && foul.FoulType is not (FoulType.Offensive or FoulType.OffensiveCharge or FoulType.DoublePersonal);
                    if (teamCharge)
                    {
                        foreach (var actor in foul.CommittingParticipants)
                        {
                            if (actor.TeamId is not long team) { known = false; continue; }
                            counts[team] = counts.GetValueOrDefault(team) + 1;
                            if (item.Clock.ClockRemaining.TotalSeconds <= 120) { lateCounts[team] = lateCounts.GetValueOrDefault(team) + 1; }
                        }
                    }
                }
                foreach (var observation in context.TeamFouls.Where(o => o.SequenceNumber == item.SequenceNumber))
                {
                    var count = counts.GetValueOrDefault(observation.TeamId);
                    var threshold = period.Key <= 4 ? 4 : 3;
                    var penalty = count > threshold || lateCounts.GetValueOrDefault(observation.TeamId) > 1;
                    var matches = observation.TeamFouls is int observed ? observed == count : observation.Penalty && penalty;
                    checks.Add(new("FOUL.TEAM_OBSERVATION", !known ? CheckStatus.NotAssessable : matches ? CheckStatus.Pass : CheckStatus.Fail,
                        "After-row team foul/PN observation. Rule 12-B V–VII: period resets, four regulation/three OT allowances, one late foul allowance; ordinary offensive and double-personal fouls excluded. Rare player-count exceptions remain warnings.",
                        item.EventId, period.Key, observation.TeamId, "TEAM_PF", observation.TeamFouls, count,
                        known && !matches ? FindingSeverity.Warning : null, [observation.SourceRow]));
                }
            }
        }
        if (context.TeamFouls.Count == 0) { checks.Add(new("FOUL.TEAM_OBSERVATION", CheckStatus.NotAssessable, "No comparable team-foul/penalty observations.")); }
    }
}
