using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed class ReferenceBundle
{
    public List<(string Path, byte[] Bytes)> Files { get; } = [];
    public List<CheckResult> Checks { get; } = [];
    public List<(int Period, string Mode, JsonElement Box)> Boxes { get; } = [];
    public List<(string TeamId, string CoachId, string Name)> Coaches { get; } = [];
    public List<(string TeamId, string PlayerId, string Name)> RosterPlayers { get; } = [];
    public bool FinalStatusVerified { get; private set; }
    public List<AlternatePlayByPlay> AlternatePlayByPlay { get; } = [];

    public static ReferenceBundle Parse(IReadOnlyList<(string Path, byte[] Bytes)> inputs, string gameId)
    {
        var result = new ReferenceBundle();
        if (inputs.Count == 0)
        {
            result.Checks.Add(new("REFERENCE.AVAILABILITY", CheckStatus.NotAssessable, "No downloaded reference bundle for this game."));
            return result;
        }
        result.Files.AddRange(inputs);
        foreach (var group in inputs
            .Where(x => !x.Path.EndsWith(".retrieval.json", StringComparison.Ordinal) && Path.GetFileNameWithoutExtension(x.Path).Contains('-'))
            .GroupBy(x => Path.GetFileNameWithoutExtension(x.Path)[..Path.GetFileNameWithoutExtension(x.Path).LastIndexOf('-')]))
        {
            // Never select a revision by filesystem timestamp. Multiple revisions need an explicit input selection.
            var paths = group.OrderBy(x => x.Path, StringComparer.Ordinal).DistinctBy(x => SourceJson.Hash(x.Bytes)).ToArray();
            if (paths.Length != 1)
            {
                result.Checks.Add(new("REFERENCE.REVISION", CheckStatus.NotAssessable, $"Multiple revisions for {group.Key}; select one in an isolated bundle."));
                continue;
            }
            try
            {
                using var doc = JsonDocument.Parse(paths[0].Bytes);
                var resource = Path.GetFileName(paths[0].Path).Split('-')[0];
                if (resource == "liveplaybyplay")
                {
                    var alternate = doc.RootElement.GetProperty("game");
                    if (alternate.Text("gameId") != gameId) { throw new InvalidDataException("Alternate PBP game identity mismatch."); }
                    var actions = alternate.GetProperty("actions").EnumerateArray().Select(r => r.Clone()).ToArray();
                    if (actions.Length == 0) { throw new InvalidDataException("Alternate PBP action stream is empty; unavailable for cross-feed comparison."); }
                    result.AlternatePlayByPlay.Add(new(SourceJson.Hash(paths[0].Bytes), actions));
                    continue;
                }
                if (resource == "liveboxscore")
                {
                    var liveBox = doc.RootElement.GetProperty("game");
                    if (liveBox.Text("gameId") != gameId) { throw new InvalidDataException("Alternate box-score game identity mismatch."); }
                    ValidateSides(liveBox);
                    result.FinalStatusVerified |= liveBox.Number("gameStatus") == 3;
                    result.Boxes.Add((0, "liveboxscore", liveBox.Clone()));
                    continue;
                }
                if (doc.RootElement.TryGetProperty("resultSets", out var tables))
                {
                    result.ReadTables(tables, gameId);
                }
                if (!doc.RootElement.TryGetProperty("boxScoreTraditional", out var box)) { continue; }
                if (box.Text("gameId") != gameId) { throw new InvalidDataException("Reference game ID mismatch."); }
                ValidateSides(box);
                var parts = Path.GetFileNameWithoutExtension(paths[0].Path).Split('-');
                var part = parts[^2];
                var period = int.Parse(part[1..], System.Globalization.CultureInfo.InvariantCulture);
                var mode = parts.Length == 3 ? "period" : parts[1];
                result.Boxes.Add((period, mode, box.Clone()));
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or FormatException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
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

    private static void ValidateSides(JsonElement box)
    {
        foreach (var side in new[] { "homeTeam", "awayTeam" })
        {
            var team = box.GetProperty(side);
            if (team.Key("teamId") == "" || team.GetProperty("players").ValueKind != JsonValueKind.Array
                || team.GetProperty("statistics").ValueKind != JsonValueKind.Object)
            { throw new InvalidDataException("Reference team/roster/statistics schema is unavailable."); }
        }
    }

    private void ReadTables(JsonElement tables, string gameId)
    {
        foreach (var table in tables.EnumerateArray())
        {
            var headers = table.GetProperty("headers").EnumerateArray().Select(x => x.GetString()!).ToArray();
            foreach (var row in table.GetProperty("rowSet").EnumerateArray())
            {
                string Value(string field)
                {
                    var index = Array.IndexOf(headers, field);
                    return index < 0 ? "" : row[index].ToString();
                }
                if (table.Text("name") == "Coaches")
                {
                    Coaches.Add((Value("TEAM_ID"), Value("COACH_ID"), Value("COACH_NAME")));
                }
                if (table.Text("name") == "CommonTeamRoster")
                {
                    RosterPlayers.Add((Value("TeamID"), Value("PLAYER_ID"), Value("PLAYER")));
                }
                if (table.Text("name") == "GameSummary")
                {
                    if (Value("GAME_ID") != gameId) { throw new InvalidDataException("Summary game ID mismatch."); }
                    FinalStatusVerified = Value("GAME_STATUS_ID") == "3";
                    Checks.Add(new("REFERENCE.GAME_STATUS", Value("GAME_STATUS_ID") == "3" ? CheckStatus.Pass : CheckStatus.NotAssessable,
                        $"Summary status={Value("GAME_STATUS_ID")}; date={Value("GAME_DATE_EST")}; season={Value("SEASON")}. Scheduled status on a retrospective response does not prove game completion."));
                }
            }
        }
    }

    public IEnumerable<JsonElement> Teams() => Boxes.Where(x => x.Period == 0).OrderBy(x => x.Mode == "liveboxscore" ? 1 : 0)
        .SelectMany(x => new[] { x.Box.GetProperty("homeTeam"), x.Box.GetProperty("awayTeam") });

    public (List<StatisticLine> Statistics, List<LineupEvidence> Lineups) Observations(
        Func<string, long?> teamId, Func<string, long?> playerId, bool ordinaryRules)
    {
        var statistics = new List<StatisticLine>();
        var lineups = new List<LineupEvidence>();
        foreach (var (period, mode, box) in Boxes.OrderBy(x => x.Mode == "liveboxscore" ? 2 : x.Mode == "range" ? 0 : 1))
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
                        Checks.Add(new("REFERENCE.PERIOD_FILTER", CheckStatus.NotAssessable,
                            $"{mode} p{period}: returned team minutes {minutes}. Period-only probes returned whole games; range probes change the selection but boundary inclusion/precision is not established. No period reconciliation is claimed.", Period: period));
                        continue;
                    }
                    if (statistics.Any(x => x.TeamId == id && x.PlayerId is null && x.Period == period)) { continue; }
                    var teamLine = Line(id.Value, null, period, teamStats);
                    if (mode == "liveboxscore")
                    {
                        var values = new Dictionary<string, double>(teamLine.Values);
                        // CDN totals include team rebounds, whereas the replay's REB is player credited.
                        foreach (var (key, field) in new[] { ("REB", "reboundsPersonal"), ("OREB", "reboundsOffensivePersonal"), ("DREB", "reboundsDefensivePersonal") })
                        {
                            if (double.TryParse(teamStats.Text(field), out var value)) { values[key] = value; }
                            else { values.Remove(key); }
                        }
                        teamLine = teamLine with { Values = values };
                    }
                    var persons = team.GetProperty("players").EnumerateArray().ToArray();
                    var parsedTurnovers = persons.Select(p => int.TryParse(p.GetProperty("statistics").Text("turnovers"), out var n) ? (int?)n : null).ToArray();
                    if (box.Text("gameId").Substring(3, 2) == "25" && parsedTurnovers.All(n => n is not null)
                        && teamLine.Values.TryGetValue("TOV", out var turnovers) && parsedTurnovers.Sum(n => n!.Value) == turnovers)
                    {
                        teamLine = teamLine with { ComparisonKeys = new Dictionary<string, string> { ["TOV"] = "PLAYER_TOV" } };
                        Checks.Add(new("REFERENCE.TURNOVER_DEFINITION", CheckStatus.Pass,
                            "2025-26 traditional-v3 team turnovers equal its complete player sum. Compare with player-credited replay turnovers; retain team-only turnovers separately.", EntityId: id));
                    }
                    statistics.Add(teamLine);
                    var starters = new List<long>();
                    foreach (var person in team.GetProperty("players").EnumerateArray())
                    {
                        var player = playerId(person.Text("personId"));
                        if (player is null) { continue; }
                        statistics.Add(Line(id.Value, player, period, person.GetProperty("statistics")));
                        // Full-game position evidence supports Q1 only, never a later period opening five.
                        if (period == 0 && IsStarter(person)) { starters.Add(player.Value); }
                    }
                    if (starters.Count == 5)
                    {
                        var agreement = StarterAgreement(team.Text("teamId"));
                        Checks.Add(new("REFERENCE.STARTER_AGREEMENT", agreement is null ? CheckStatus.NotAssessable : agreement.Value ? CheckStatus.Pass : CheckStatus.Fail,
                            "Compare complete five-player starting metadata across retained full-game box-score feeds. One available starter source is not cross-feed corroboration.",
                            Period: 1, EntityId: id, Severity: agreement == false ? FindingSeverity.Warning : null));
                        if (agreement != false) { lineups.Add(new(1, id.Value, starters, $"Full-game {mode} starter metadata; Q1 only.")); }
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

    internal static bool IsStarter(JsonElement player) => player.TryGetProperty("starter", out _)
        ? player.Text("starter") == "1" : !string.IsNullOrWhiteSpace(player.Text("position"));

    internal bool? StarterAgreement(string teamId)
    {
        var candidates = Teams().Where(t => t.Text("teamId") == teamId).Select(t => t.GetProperty("players").EnumerateArray()
            .Where(IsStarter).Select(p => p.Key("personId")).Where(p => p != "").ToHashSet()).Where(p => p.Count == 5).ToArray();
        return candidates.Length < 2 ? null : candidates.Skip(1).All(c => c.SetEquals(candidates[0]));
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
