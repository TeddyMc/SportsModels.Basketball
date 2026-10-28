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
        var ejected = new HashSet<long>();
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
            if (item is EjectionEvent { PlayerId: long playerEjected }) { ejected.Add(playerEjected); }
            if (item is ReviewEvent) { ejected.Clear(); }
            long? actorId = item switch
            {
                FieldGoalEvent shot => shot.ShooterPlayerId,
                FreeThrowEvent ft => ft.ShooterPlayerId,
                TurnoverEvent turnover => turnover.ResponsiblePlayerId,
                SubstitutionEvent sub => sub.PlayerInId,
                _ => null
            };
            if (actorId is long actorPlayer && ejected.Contains(actorPlayer))
            {
                checks.Add(new("FOUL.EJECTION_PARTICIPATION", CheckStatus.Fail, "Player participates after an observed ejection with no intervening review; investigate source revision/ruling.",
                    item.EventId, BasketballValidator.Period(item.Clock), actorPlayer, Severity: FindingSeverity.Warning));
            }
        }
        if (context.FoulCounts.Count == 0) { checks.Add(new("FOUL.PERSONAL_OBSERVATION", CheckStatus.NotAssessable, "No comparable personal-foul count observations.")); }
    }
}
