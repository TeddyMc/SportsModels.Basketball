using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed class ReferenceBundle
{
    public List<(string Path, byte[] Bytes)> Files { get; } = [];
    public List<CheckResult> Checks { get; } = [];
    public List<(int Period, string Mode, JsonElement Box)> Boxes { get; } = [];

    public static ReferenceBundle Read(string directory, string gameId)
    {
        var result = new ReferenceBundle();
        var folder = Path.Combine(directory, gameId);
        if (!Directory.Exists(folder))
        {
            result.Checks.Add(new("REFERENCE.AVAILABILITY", CheckStatus.NotAssessable, "No downloaded reference bundle for this game."));
            return result;
        }
        foreach (var group in Directory.EnumerateFiles(folder, "*.json")
            .Where(x => !x.EndsWith(".retrieval.json", StringComparison.Ordinal))
            .GroupBy(x => Path.GetFileNameWithoutExtension(x)[..Path.GetFileNameWithoutExtension(x).LastIndexOf('-')]))
        {
            // Never select a revision by filesystem timestamp. Multiple revisions need an explicit input selection.
            var paths = group.Order(StringComparer.Ordinal).ToArray();
            foreach (var path in paths) { result.Files.Add((path, File.ReadAllBytes(path))); }
            if (paths.Length != 1)
            {
                result.Checks.Add(new("REFERENCE.REVISION", CheckStatus.NotAssessable, $"Multiple revisions for {group.Key}; select one in an isolated bundle."));
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(result.Files.Last().Bytes);
                if (!doc.RootElement.TryGetProperty("boxScoreTraditional", out var box)) { continue; }
                if (box.Text("gameId") != gameId) { throw new InvalidDataException("Reference game ID mismatch."); }
                var parts = Path.GetFileNameWithoutExtension(paths[0]).Split('-');
                var part = parts[^2];
                var period = int.Parse(part[1..], System.Globalization.CultureInfo.InvariantCulture);
                var mode = parts.Length == 3 ? "period" : parts[1];
                result.Boxes.Add((period, mode, box.Clone()));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException)
            {
                result.Checks.Add(new("REFERENCE.SCHEMA", CheckStatus.NotAssessable, exception.Message));
            }
        }
        if (result.Boxes.Count == 0)
        {
            result.Checks.Add(new("REFERENCE.AVAILABILITY", CheckStatus.NotAssessable, "No usable traditional-v3 box score; retained responses may be unavailable or unsupported schemas."));
        }
        return result;
    }

    public IEnumerable<JsonElement> Teams() => Boxes.Where(x => x.Period == 0)
        .SelectMany(x => new[] { x.Box.GetProperty("homeTeam"), x.Box.GetProperty("awayTeam") });

    public (List<StatisticLine> Statistics, List<LineupEvidence> Lineups) Observations(
        Func<string, long?> teamId, Func<string, long?> playerId, bool ordinaryRules)
    {
        var statistics = new List<StatisticLine>();
        var lineups = new List<LineupEvidence>();
        foreach (var (period, mode, box) in Boxes.OrderBy(x => x.Mode == "range" ? 0 : 1))
        {
            try
            {
                foreach (var side in new[] { "homeTeam", "awayTeam" })
                {
                    var team = box.GetProperty(side);
                    var id = teamId(team.Text("teamId"));
                    if (id is null) { continue; }
                    var teamStats = team.GetProperty("statistics");
                    if (mode == "opening")
                    {
                        Checks.Add(new("REFERENCE.OPENING_RANGE", CheckStatus.NotAssessable,
                            $"Requested opening-second range returned team minutes {teamStats.Text("minutes")}; boundary/rounding semantics unverified. No lineup asserted from these rows.", Period: period));
                        continue;
                    }
                    if (period > 0)
                    {
                        var minutes = SourceJson.Minutes(teamStats.Text("minutes"));
                        var expected = period <= 4 ? 60 : 25;
                        if (!ordinaryRules || minutes is null || Math.Abs(minutes.Value - expected) > 1.0 / 60)
                        {
                            Checks.Add(new("REFERENCE.PERIOD_FILTER", CheckStatus.NotAssessable,
                                $"p{period}: team minutes {minutes} do not verify a complete {expected}-minute player interval. Period parameter alone is not evidence.", Period: period));
                            continue;
                        }
                        Checks.Add(new("REFERENCE.PERIOD_FILTER", CheckStatus.Pass,
                            "Response interval supported by team player-minutes; period sums checked separately.", Period: period));
                    }
                    if (statistics.Any(x => x.TeamId == id && x.PlayerId is null && x.Period == period)) { continue; }
                    statistics.Add(Line(id.Value, null, period, teamStats));
                    var starters = new List<long>();
                    foreach (var person in team.GetProperty("players").EnumerateArray())
                    {
                        var player = playerId(person.Text("personId"));
                        if (player is null) { continue; }
                        statistics.Add(Line(id.Value, player, period, person.GetProperty("statistics")));
                        // Full-game position evidence supports Q1 only, never a later period opening five.
                        if (period == 0 && !string.IsNullOrWhiteSpace(person.Text("position"))) { starters.Add(player.Value); }
                    }
                    if (starters.Count == 5)
                    {
                        lineups.Add(new(1, id.Value, starters, "Full-game traditional-v3 starting positions; Q1 only."));
                    }
                }
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                Checks.Add(new("REFERENCE.SCHEMA", CheckStatus.NotAssessable, exception.Message, Period: period));
            }
        }
        return (statistics, lineups);
    }

    private static StatisticLine Line(long team, long? player, int period, JsonElement row)
    {
        var fields = new Dictionary<string, string>
        {
            ["PTS"]="points", ["FGM"]="fieldGoalsMade", ["FGA"]="fieldGoalsAttempted",
            ["3PM"]="threePointersMade", ["3PA"]="threePointersAttempted", ["FTM"]="freeThrowsMade",
            ["FTA"]="freeThrowsAttempted", ["OREB"]="reboundsOffensive", ["DREB"]="reboundsDefensive",
            ["REB"]="reboundsTotal", ["AST"]="assists", ["STL"]="steals", ["BLK"]="blocks",
            ["TOV"]="turnovers", ["PF"]="foulsPersonal", ["+/-"]="plusMinusPoints"
        };
        var values = new Dictionary<string, double>();
        var unknown = new List<string>();
        foreach (var (stat, field) in fields)
        {
            if (double.TryParse(row.Text(field), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) { values[stat] = value; }
            else { unknown.Add(stat); }
        }
        if (SourceJson.Minutes(row.Text("minutes")) is double minutes) { values["MIN"] = minutes; }
        else { unknown.Add("MIN"); }
        return new(team, player, period, values, unknown);
    }
}
