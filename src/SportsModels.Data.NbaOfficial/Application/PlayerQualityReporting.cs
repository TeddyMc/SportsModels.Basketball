using System.Globalization;
using System.Text;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class PlayerQualityReporting
{
    private readonly StringBuilder roster = new("game,season,competition,team,player,eligibility,started,played,not_playing_reason,not_playing_description,has_stats_box,has_cdn_box,has_cdn_pbp,eligibility_evidence,started_evidence,played_evidence\n");
    private readonly StringBuilder minutes = new("game,season,competition,team,player,scope,period,seconds,established_seconds,availability,evidence_kinds,reasons\n");
    private readonly Dictionary<(string Season, string Competition, PlayerEligibility Eligibility, bool Unused), int> statuses = [];
    private readonly Dictionary<(string Season, string Competition, MinuteScope Scope, MinuteAvailability Availability), int> availability = [];
    private readonly Dictionary<(string Season, string Competition, MinuteScope Scope, string Basis), int> origins = [];
    private readonly Dictionary<(string Season, string Competition), (HashSet<string> Games, int MissingReferenceGames)> games = [];

    internal void Add(string id, ConvertedGame converted, ValidationReport report)
    {
        var season = $"20{id.Substring(3, 2)}-{(int.Parse(id.Substring(3, 2), CultureInfo.InvariantCulture) + 1) % 100:00}";
        var competition = converted.Competition;
        var statsBox = converted.HasStatsBox;
        var cdnBox = converted.HasCdnBox;
        var cdnPbp = converted.HasCdnPbp;
        var group = (season, competition);
        if (!games.TryGetValue(group, out var counts)) { counts = ([], 0); }
        counts.Games.Add(id);
        if (!statsBox || !cdnBox || !cdnPbp) { counts.MissingReferenceGames++; }
        games[group] = counts;
        foreach (var team in new[] { converted.Game.HomeTeam, converted.Game.AwayTeam })
            foreach (var player in team.Players)
            {
                string Basis(string field) => string.Join('|', player.StatusEvidence.Where(e => e.FieldName == field)
                    .Select(e => e.Kind + "/" + e.Origin).Distinct());
                roster.AppendLine(string.Join(',', new object?[] { id, season, competition, team.TeamId, player.PlayerId,
                    player.Eligibility, player.Started, player.Played, player.NotPlayingReason, player.NotPlayingDescription, statsBox, cdnBox, cdnPbp,
                    Basis(nameof(player.Eligibility)), Basis(nameof(player.Started)), Basis(nameof(player.Played)) }.Select(Csv)));
                var key = (season, competition, player.Eligibility, player.Eligibility == PlayerEligibility.Active && player.Played == false);
                statuses[key] = statuses.GetValueOrDefault(key) + 1;
            }
        foreach (var observation in report.PlayerMinutes)
        {
            minutes.AppendLine(string.Join(',', new object?[] { id, season, competition, observation.TeamId, observation.PlayerId,
                observation.Scope, observation.Period, observation.Seconds, observation.EstablishedSeconds, observation.Availability, string.Join('|', observation.EvidenceKinds),
                string.Join(" | ", observation.Reasons) }.Select(Csv)));
            var key = (season, competition, observation.Scope, observation.Availability);
            availability[key] = availability.GetValueOrDefault(key) + 1;
            var basis = observation.EvidenceKinds.Contains(MinuteEvidenceKind.InferredBreakChanges) ? "InferredBreakChanges"
                : observation.EvidenceKinds.Contains(MinuteEvidenceKind.SupportingFeed) ? "SupportingFeed"
                : observation.EvidenceKinds.Contains(MinuteEvidenceKind.ExplicitWithinPeriod) ? "ExplicitWithinPeriod" : "Unknown";
            var originKey = (season, competition, observation.Scope, basis);
            origins[originKey] = origins.GetValueOrDefault(originKey) + 1;
        }
    }

    internal string RosterCsv => roster.ToString();
    internal string MinutesCsv => minutes.ToString();
    internal string SummaryCsv
    {
        get
        {
            var csv = new StringBuilder("season,competition,category,value,count\n");
            foreach (var (key, count) in games.OrderBy(p => p.Key))
            {
                csv.AppendLine(string.Join(',', new object?[] { key.Season, key.Competition, "Games", "All", count.Games.Count }.Select(Csv)));
                csv.AppendLine(string.Join(',', new object?[] { key.Season, key.Competition, "ReferenceCoverage", "MissingAnySupportingReference", count.MissingReferenceGames }.Select(Csv)));
            }
            foreach (var group in statuses.GroupBy(p => (p.Key.Season, p.Key.Competition, p.Key.Eligibility)).OrderBy(g => g.Key))
                csv.AppendLine(string.Join(',', new object?[] { group.Key.Season, group.Key.Competition, "Eligibility", group.Key.Eligibility, group.Sum(p => p.Value) }.Select(Csv)));
            foreach (var (key, count) in statuses.Where(p => p.Key.Unused).OrderBy(p => p.Key))
                csv.AppendLine(string.Join(',', new object?[] { key.Season, key.Competition, "ParticipationStatus", "ReportedActiveUnused", count }.Select(Csv)));
            foreach (var (key, count) in availability.OrderBy(p => p.Key))
                csv.AppendLine(string.Join(',', new object?[] { key.Season, key.Competition, key.Scope, key.Availability, count }.Select(Csv)));
            foreach (var (key, count) in origins.OrderBy(p => p.Key))
                csv.AppendLine(string.Join(',', new object?[] { key.Season, key.Competition, key.Scope + "/Evidence", key.Basis, count }.Select(Csv)));
            return csv.ToString();
        }
    }

    private static string Csv(object? value) => "\"" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Replace("\"", "\"\"") + "\"";
}
