using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.Basketball.Application;

internal static class SequenceDiagnostics
{
    public static void Check(BasketballGamePlayByPlay game, GameValidationContext context, List<CheckResult> checks)
    {
        long? control = null;
        long? openingWinner = null;
        long? pendingMissTeam = null;
        FreeThrowEvent? previousFt = null;
        var events = game.Events.ToDictionary(e => e.EventId);
        var firstPeriod = true;
        foreach (var e in game.Events)
        {
            var period = BasketballValidator.Period(e.Clock);
            if (context.Gaps.Any(g => g.Possession && g.SequenceNumber <= e.SequenceNumber
                && (game.Events.LastOrDefault(x => x.SequenceNumber < e.SequenceNumber)?.SequenceNumber ?? 0) < g.SequenceNumber))
            { control = null; pendingMissTeam = null; }
            if (!context.OrdinaryNbaRules) { control = null; }
            switch (e)
            {
                case PeriodBoundaryEvent boundary:
                    pendingMissTeam = null;
                    previousFt = null;
                    control = boundary.Marker == PeriodMarker.Start && period is >= 2 and <= 4 && openingWinner is not null
                        && context.OrdinaryNbaRules ? period == 4 ? openingWinner : Opponent(game, openingWinner.Value) : null;
                    if (boundary.Marker == PeriodMarker.Start && period is >= 2 and <= 4)
                    { checks.Add(new("POSSESSION.PERIOD_ENTITLEMENT", control is null ? CheckStatus.NotAssessable : CheckStatus.Pass,
                        "Rule 6 I(b): first possession winner opens Q4, opponent Q2/Q3; intervening penalties can invalidate entitlement.", boundary.EventId, period)); }
                    break;
                case JumpBallEvent jump:
                    control = jump.GainingTeamId;
                    pendingMissTeam = null;
                    if (period == 1 && firstPeriod) { openingWinner = jump.GainingTeamId; firstPeriod = false; }
                    checks.Add(new("POSSESSION.JUMP", control is null ? CheckStatus.NotAssessable : CheckStatus.Pass,
                        "Control established from tip recipient; named jumper alone does not establish winner.", e.EventId, period));
                    break;
                case FieldGoalEvent shot:
                    CheckControl(shot.TeamId, control, e, checks);
                    if (shot.Outcome == ShotOutcome.Missed) { pendingMissTeam = shot.TeamId; control = null; }
                    else if (shot.Outcome == ShotOutcome.Made)
                    {
                        // A supported linked shooting foul prevents prematurely flipping an and-one.
                        var continuation = game.Events.OfType<FoulEvent>().Any(f => f.RelatedFieldGoalEventId == shot.EventId);
                        control = continuation ? null : Opponent(game, shot.TeamId);
                        pendingMissTeam = null;
                    }
                    else { control = null; pendingMissTeam = null; }
                    break;
                case ReboundEvent rebound:
                    var compatible = pendingMissTeam is not null && rebound.TeamId is not null && rebound.ReboundType switch
                    { ReboundType.Offensive => rebound.TeamId == pendingMissTeam, ReboundType.Defensive => rebound.TeamId != pendingMissTeam, _ => true };
                    var known = pendingMissTeam is not null && rebound.TeamId is not null && rebound.ReboundType is ReboundType.Offensive or ReboundType.Defensive
                        && rebound.Credit == ReboundCredit.Player;
                    checks.Add(new("POSSESSION.REBOUND", !known ? CheckStatus.NotAssessable : compatible ? CheckStatus.Pass : CheckStatus.Fail,
                        "Player rebound classification checked against the pending missed attempt; team/dead-ball rebound semantics remain uncertain.", e.EventId, period,
                        Severity: known && !compatible ? FindingSeverity.Error : null));
                    control = known && compatible ? rebound.TeamId : null;
                    pendingMissTeam = null;
                    break;
                case TurnoverEvent turnover:
                    CheckControl(turnover.TeamId, control, e, checks);
                    control = Opponent(game, turnover.TeamId);
                    pendingMissTeam = null;
                    if (turnover.StealingPlayerId is long stealing)
                    {
                        var opponent = turnover.TeamId == game.HomeTeam.TeamId ? game.AwayTeam : game.HomeTeam;
                        checks.Add(new("POSSESSION.STEAL_TEAM", opponent.Players.Any(p => p.PlayerId == stealing) ? CheckStatus.Pass : CheckStatus.Fail,
                            "Auxiliary steal belongs to the opponent and causes no second possession change.", e.EventId, period, Severity: FindingSeverity.Error));
                    }
                    break;
                case FreeThrowEvent ft:
                    var numbered = ft.AttemptNumber is > 0 && ft.TotalAttempts is > 0;
                    var valid = numbered && ft.AttemptNumber <= ft.TotalAttempts;
                    checks.Add(new("FT.NUMBERING", !numbered ? CheckStatus.NotAssessable : valid ? CheckStatus.Pass : CheckStatus.Fail,
                        "Unnumbered technical FTs retain null attempt/count; one FT does not prove an and-one.", e.EventId, period, Severity: numbered && !valid ? FindingSeverity.Error : null));
                    if (previousFt is not null && ft.ParentFoulEventId is not null && previousFt.ParentFoulEventId == ft.ParentFoulEventId
                        && previousFt.Category == ft.Category && ft.Category == FreeThrowCategory.Ordinary)
                    {
                        var matches = ft.ShooterPlayerId == previousFt.ShooterPlayerId && ft.TeamId == previousFt.TeamId
                            && ft.TotalAttempts == previousFt.TotalAttempts && ft.AttemptNumber == previousFt.AttemptNumber + 1;
                        checks.Add(new("FT.SEQUENCE", matches ? CheckStatus.Pass : CheckStatus.NotAssessable,
                            matches ? "Interrupted sequence retains shooter/team/award numbering across administrative events."
                                : "Replacement shooter, retry, cancellation or conflicting sequence requires evidence; not assumed illegal.", e.EventId, period));
                    }
                    if (ft.ParentFoulEventId is long parent && events.GetValueOrDefault(parent) is FoulEvent foul)
                    {
                        var sameTeam = foul.CommittingParticipants.Any(p => p.TeamId == ft.TeamId);
                        checks.Add(new("FT.FOUL_TEAM", sameTeam ? CheckStatus.Fail : CheckStatus.Pass,
                            "Causal foul charge belongs to the opposing team for the supported award.", e.EventId, period, Severity: sameTeam ? FindingSeverity.Warning : null));
                    }
                    else { checks.Add(new("FT.PARENT", CheckStatus.NotAssessable, "Unresolved causal foul is not proof of an invalid FT.", e.EventId, period)); }
                    if (ft.Category == FreeThrowCategory.Ordinary && valid && ft.AttemptNumber == ft.TotalAttempts && ft.Outcome == ShotOutcome.Made)
                    { control = Opponent(game, ft.TeamId); pendingMissTeam = null; }
                    else if (ft.Category == FreeThrowCategory.Ordinary && valid && ft.AttemptNumber == ft.TotalAttempts && ft.Outcome == ShotOutcome.Missed)
                    { control = null; pendingMissTeam = ft.TeamId; }
                    else { control = null; pendingMissTeam = null; }
                    previousFt = ft;
                    break;
                case FoulEvent:
                case ReviewEvent:
                case ViolationEvent:
                    // Whistles, retained-possession penalties and review rulings can interrupt control.
                    control = null;
                    break;
            }
        }
        checks.Add(new("FOUL.TEAM_PENALTY", CheckStatus.NotAssessable, "Team-foul/penalty marker semantics and exceptional counting have not been independently verified."));
        checks.Add(new("FOUL.DISQUALIFICATION", CheckStatus.NotAssessable, "Foul-out participation requires verified player-count exception and complete lineup evidence; technicals are not blanket personal fouls."));
    }

    private static long Opponent(BasketballGamePlayByPlay game, long team) => team == game.HomeTeam.TeamId ? game.AwayTeam.TeamId : game.HomeTeam.TeamId;

    private static void CheckControl(long team, long? control, BasketballPlayByPlayEvent e, List<CheckResult> checks) =>
        checks.Add(new("POSSESSION.CONTROL", control is null ? CheckStatus.NotAssessable : control == team ? CheckStatus.Pass : CheckStatus.Fail,
            control is null ? "Prior control/entitlement unknown; no cascading error."
                : "Offensive action compared with established control/inbound entitlement; missing passes/inbounds are not required rows.",
            e.EventId, BasketballValidator.Period(e.Clock), Expected: control, Observed: team,
            Severity: control is not null && control != team ? FindingSeverity.Warning : null));
}
