using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;

namespace SportsModels.Data.NbaOfficial.Definitions;

internal sealed class NbaRosterStatus
{
    private readonly Dictionary<(string Team, string Player), List<(JsonElement Row, string Snapshot, string Path)>> observations = [];

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
    }

    internal BasketballGamePlayer Apply(BasketballGamePlayer canonical, string team, string player)
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

        return canonical with
        {
            Eligibility = Read("status", nameof(canonical.Eligibility)).ToUpperInvariant() switch
            { "ACTIVE" => PlayerEligibility.Active, "INACTIVE" => PlayerEligibility.Inactive, _ => PlayerEligibility.Unknown },
            Started = Flag(Read("starter", nameof(canonical.Started))),
            Played = Flag(Read("played", nameof(canonical.Played))),
            NotPlayingReason = Optional(Read("notPlayingReason", nameof(canonical.NotPlayingReason))),
            NotPlayingDescription = Optional(Read("notPlayingDescription", nameof(canonical.NotPlayingDescription))),
            StatusEvidence = evidence
        };
    }
}
