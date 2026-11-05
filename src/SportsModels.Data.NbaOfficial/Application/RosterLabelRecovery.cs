using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Application;

/// <summary>Expose established diagnostic facts without treating missing metadata as bench/inactive.</summary>
internal static class RosterLabelRecovery
{
    internal static BasketballGamePlayByPlay Apply(BasketballGamePlayByPlay game, ValidationReport report)
    {
        BasketballGameTeam Recover(BasketballGameTeam team) => team with
        {
            Players = team.Players.Select(player =>
            {
                var evidence = player.StatusEvidence.ToList();
                var started = player.Started;
                var eligibility = player.Eligibility;
                var played = player.Played;
                var opening = report.OpeningMemberships.Where(m => m.TeamId == team.TeamId
                    && m.PlayerId == player.PlayerId && m.Period == 1 && m.OnCourt is not null).ToArray();
                var contradictory = report.PlayerMinutes.Any(m => m.TeamId == team.TeamId && m.PlayerId == player.PlayerId
                    && m.Scope == MinuteScope.Period && m.Period == 1 && m.Availability == MinuteAvailability.Contradictory);
                if (started is null && opening.Length == 1 && !contradictory
                    && !player.StatusEvidence.Any(e => e.FieldName == nameof(player.Started)))
                {
                    started = opening[0].OnCourt;
                    evidence.Add(new(nameof(player.Started), BasketballDataSource.NbaOfficial, "",
                        "$.Validation.OpeningMemberships", started.ToString()!)
                    { Kind = RosterEvidenceKind.OpeningMembership, Origin = opening[0].Origin,
                        Explanation = opening[0].Evidence,
                        EventIds = opening[0].FirstEventId is long id ? [id] : [] });
                }
                var changes = game.Events.OfType<SubstitutionEvent>().Where(s => s.TeamId == team.TeamId
                    && (s.PlayerInId == player.PlayerId || s.PlayerOutId == player.PlayerId)).Select(s => s.EventId).ToArray();
                var intervals = report.Stints.Where(s => s.TeamId == team.TeamId && s.PlayerId == player.PlayerId
                    && s.StartRemaining > s.EndRemaining).ToArray();
                if (changes.Length > 0 || intervals.Length > 0)
                {
                    var origin = changes.Length > 0 ? MinuteEvidenceKind.ExplicitWithinPeriod
                        : intervals.Any(s => s.Origin == MinuteEvidenceKind.InferredBreakChanges)
                            ? MinuteEvidenceKind.InferredBreakChanges : MinuteEvidenceKind.SupportingFeed;
                    RosterFieldEvidence Participation(string field) => new(field, BasketballDataSource.NbaOfficial, "",
                        changes.Length > 0 ? "$.Events" : "$.Validation.Stints", "participated")
                    { Kind = RosterEvidenceKind.Participation, Origin = origin, EventIds = changes,
                        Explanation = "Recorded substitution participation or established positive on-court interval. Retrospective evidence; not pre-tip availability." };
                    if (eligibility == PlayerEligibility.Unknown) { eligibility = PlayerEligibility.Active; evidence.Add(Participation(nameof(player.Eligibility))); }
                    if (played is null) { played = true; evidence.Add(Participation(nameof(player.Played))); }
                }
                return player with { Started = started, Eligibility = eligibility, Played = played, StatusEvidence = evidence };
            }).ToArray()
        };
        return game with { HomeTeam = Recover(game.HomeTeam), AwayTeam = Recover(game.AwayTeam) };
    }
}
