using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed class NbaRosterStatus
{
    private readonly Dictionary<(string Team, string Player), List<(JsonElement Row, string Snapshot, string Path)>> observations = [];
    private readonly Dictionary<(string Team, string Player), List<(bool Started, RosterFieldEvidence Evidence)>> traditionalStarters = [];
    private readonly Dictionary<(string Team, string Player), List<RosterFieldEvidence>> traditionalParticipation = [];

    internal NbaRosterStatus(ReferenceBundle references)
    {
        foreach (var (snapshot, box) in references.LiveBoxSnapshots)
        {
            foreach (var side in new[] { "homeTeam", "awayTeam" })
            {
                var sourceTeam = box.GetProperty(side);
                var index = 0;
                foreach (var row in sourceTeam.GetProperty("players").EnumerateArray())
                {
                    var key = (sourceTeam.Key("teamId"), row.Key("personId"));
                    if (!observations.TryGetValue(key, out var list)) { observations[key] = list = []; }
                    list.Add((row.Clone(), snapshot, $"$.game.{side}.players[{index}]"));
                    index++;
                }
            }
        }
        foreach (var (snapshot, box) in references.TraditionalBoxSnapshots)
            foreach (var side in new[] { "homeTeam", "awayTeam" })
            {
                var team = box.GetProperty(side);
                var roster = team.GetProperty("players").EnumerateArray().ToArray();
                var starters = roster.Where(ReferenceBundle.IsStarter).Select(p => p.Key("personId")).ToHashSet();
                for (var i = 0; i < roster.Length; i++)
                {
                    var person = roster[i];
                    if (!person.TryGetProperty("statistics", out var statistics)
                        || SourceJson.Minutes(statistics.Text("minutes")) is not > 0) { continue; }
                    var key = (team.Key("teamId"), person.Key("personId"));
                    if (!traditionalParticipation.TryGetValue(key, out var list)) { traditionalParticipation[key] = list = []; }
                    list.Add(new("Played", BasketballDataSource.NbaOfficial, snapshot,
                        $"$.boxScoreTraditional.{side}.players[{i}].statistics.minutes", statistics.Text("minutes"))
                    { Kind = RosterEvidenceKind.Participation, Explanation = "Positive reported full-game minutes establish retrospective participation and active eligibility; zero minutes alone do not." });
                }
                if (starters.Count != 5) { continue; }
                for (var i = 0; i < roster.Length; i++)
                {
                    var person = roster[i];
                    var key = (team.Key("teamId"), person.Key("personId"));
                    if (!traditionalStarters.TryGetValue(key, out var list)) { traditionalStarters[key] = list = []; }
                    var field = person.TryGetProperty("starter", out _) ? "starter" : "position";
                    if (!person.TryGetProperty(field, out var raw) || raw.ValueKind == JsonValueKind.Null) { continue; }
                    list.Add((starters.Contains(key.Item2), new("Started", BasketballDataSource.NbaOfficial, snapshot,
                        $"$.boxScoreTraditional.{side}.players[{i}].{field}", person.Text(field))));
                }
            }
    }

    internal BasketballGamePlayer Apply(BasketballGamePlayer canonical, string team, string player)
    {
        var result = ApplyLive(canonical, team, player);
        if (result.Started is null && traditionalStarters.TryGetValue((team, player), out var starters)
            && starters.Select(s => s.Started).Distinct().Count() == 1
            && !result.StatusEvidence.Any(e => e.FieldName == nameof(canonical.Started)))
        {
            result = result with { Started = starters[0].Started,
                StatusEvidence = result.StatusEvidence.Concat(starters.Select(s => s.Evidence)).ToArray() };
        }
        if (result.Started is null && traditionalStarters.TryGetValue((team, player), out var conflicting)
            && conflicting.Select(s => s.Started).Distinct().Count() > 1)
        {
            result = result with { StatusEvidence = result.StatusEvidence.Concat(conflicting.Select(s => s.Evidence)).ToArray() };
        }
        if (traditionalParticipation.TryGetValue((team, player), out var participation))
        {
            if (result.Played is null) { result = result with { Played = true,
                StatusEvidence = result.StatusEvidence.Concat(participation).ToArray() }; }
            if (result.Eligibility == PlayerEligibility.Unknown) { result = result with { Eligibility = PlayerEligibility.Active,
                StatusEvidence = result.StatusEvidence.Concat(participation.Select(e => e with { FieldName = "Eligibility" })).ToArray() }; }
        }
        return result;
    }

    private BasketballGamePlayer ApplyLive(BasketballGamePlayer canonical, string team, string player)
    {
        var candidates = observations.GetValueOrDefault((team, player))?.DistinctBy(x => (x.Snapshot, x.Path)).ToArray() ?? [];
        if (candidates.Length != 1) { return canonical; }

        var (person, snapshot, sourcePath) = candidates[0];
        var evidence = new List<RosterFieldEvidence>();
        string Read(string field, string canonicalField)
        {
            if (!person.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) { return ""; }
            var text = value.ToString();
            evidence.Add(new(canonicalField, BasketballDataSource.NbaOfficial, snapshot, sourcePath + "." + field, text));
            return text;
        }
        static bool? Flag(string value) => value.ToUpperInvariant() switch { "1" or "TRUE" => true, "0" or "FALSE" => false, _ => null };
        static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

        var eligibility = Read("status", nameof(canonical.Eligibility)).ToUpperInvariant() switch
            { "ACTIVE" => PlayerEligibility.Active, "INACTIVE" => PlayerEligibility.Inactive, _ => PlayerEligibility.Unknown };
        var started = Flag(Read("starter", nameof(canonical.Started)));
        if (started is bool declared && traditionalStarters.TryGetValue((team, player), out var other)
            && other.Any(s => s.Started != declared))
        {
            started = null;
            evidence.AddRange(other.Where(s => s.Started != declared).Select(s => s.Evidence));
        }
        return canonical with
        {
            Eligibility = eligibility,
            Started = started,
            Played = Flag(Read("played", nameof(canonical.Played))),
            NotPlayingReason = Optional(Read("notPlayingReason", nameof(canonical.NotPlayingReason))),
            NotPlayingDescription = Optional(Read("notPlayingDescription", nameof(canonical.NotPlayingDescription))),
            StatusEvidence = evidence
        };
    }
}
