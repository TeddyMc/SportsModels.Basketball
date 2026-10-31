using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed record AlternateReplayResult(IReadOnlyList<LineupEvidence> Lineups, IReadOnlyList<CheckResult> Checks);

/// <summary>Corroborate an alternate official substitution stream before using later-period anchors.</summary>
internal static class AlternateLineupReplay
{
    internal static AlternateReplayResult Build(ReferenceBundle references, bool ordinaryRules,
        Func<string, long?> teamId, Func<string, long?> playerId)
    {
        var checks = new List<CheckResult>();
        var anchors = new List<LineupEvidence>();
        if (!ordinaryRules || references.AlternatePlayByPlay.Count != 1)
        {
            checks.Add(new("REFERENCE.ALTERNATE_LINEUPS", CheckStatus.NotAssessable,
                "A single matched alternate PBP snapshot, box-score starters and supported period rules are required."));
            return new(anchors, checks);
        }
        var alternate = references.AlternatePlayByPlay[0];
        var boxes = references.Teams().GroupBy(t => t.Text("teamId")).ToDictionary(g => g.Key, g => g.First());
        if (boxes.Count != 2)
        {
            checks.Add(new("REFERENCE.ALTERNATE_LINEUPS", CheckStatus.NotAssessable, "Full-game box-score rosters for both teams are required to corroborate alternate lineups."));
            return new(anchors, checks);
        }
        var active = boxes.ToDictionary(kv => kv.Key, kv => kv.Value.GetProperty("players").EnumerateArray()
            .Where(ReferenceBundle.IsStarter).Select(p => p.Key("personId")).Where(p => p != "").ToHashSet());
        var reliable = active.ToDictionary(kv => kv.Key, kv => kv.Value.Count == 5 && references.StarterAgreement(kv.Key) != false);
        var minutes = active.Keys.ToDictionary(t => t, _ => new Dictionary<string, double>());
        var candidates = new List<(int Period, string Team, string[] Players, int[] Rows)>();
        var boundaries = new Dictionary<int, (bool Start, bool End)>();
        var priorPeriod = 0;
        var priorClock = 0.0;

        foreach (var group in Groups(alternate.Rows))
        {
            var first = alternate.Rows[group[0]];
            var period = first.Number("period");
            double clock;
            try { clock = SourceJson.Remaining(first.Text("clock")); }
            catch (FormatException)
            {
                foreach (var key in reliable.Keys.ToArray()) { reliable[key] = false; }
                checks.Add(Check("REFERENCE.ALTERNATE_CLOCK", CheckStatus.Fail, "Invalid alternate clock.", alternate, group));
                continue;
            }
            var maximum = period <= 4 ? 720 : 300;
            if (period != priorPeriod)
            {
                if (priorPeriod != 0 && (period != priorPeriod + 1 || priorClock != 0))
                { foreach (var key in reliable.Keys.ToArray()) { reliable[key] = false; } }
                priorPeriod = period;
                priorClock = maximum;
            }
            if (period < 1 || clock < 0 || clock > priorClock || clock > maximum)
            {
                foreach (var key in reliable.Keys.ToArray()) { reliable[key] = false; }
                checks.Add(Check("REFERENCE.ALTERNATE_CLOCK", CheckStatus.Fail, "Nonmonotone/out-of-range alternate clock; no sorting or repaired time.", alternate, group));
            }
            var elapsed = Math.Max(0, priorClock - clock);
            foreach (var (key, lineup) in active)
            {
                foreach (var player in lineup) { minutes[key][player] = minutes[key].GetValueOrDefault(player) + elapsed; }
            }
            priorClock = clock;
            foreach (var index in group)
            {
                var row = alternate.Rows[index];
                if (row.Text("actionType") == "period")
                {
                    var seen = boundaries.GetValueOrDefault(period);
                    boundaries[period] = (seen.Start || row.Text("subType") == "start" && clock == maximum,
                        seen.End || row.Text("subType") == "end" && clock == 0);
                }
            }
            foreach (var (key, lineup) in active)
            {
                var subs = group.Where(i => alternate.Rows[i].Text("actionType") == "substitution" && alternate.Rows[i].Text("teamId") == key).ToArray();
                if (!ApplyBatch(lineup, subs.Select(i => alternate.Rows[i])))
                {
                    var previouslyReliable = reliable[key];
                    reliable[key] = false;
                    checks.Add(Check("REFERENCE.ALTERNATE_SUBSTITUTION", previouslyReliable ? CheckStatus.Fail : CheckStatus.NotAssessable,
                        $"Alternate substitution net membership for team {key} disagrees with provisional replay; prior state reliable={previouslyReliable}. Unknown prior state cannot establish another substitution contradiction. No lineup repair.", alternate, subs, period));
                }
                if (lineup.Count != 5) { reliable[key] = false; }
                if (subs.Length == 0)
                {
                    foreach (var index in group)
                    {
                        var row = alternate.Rows[index];
                        var actor = row.Key("personId");
                        if (row.Text("teamId") == key && actor != "" && row.Text("actionType") is "2pt" or "3pt" or "rebound" or "turnover"
                            && !lineup.Contains(actor))
                        {
                            var previouslyReliable = reliable[key];
                            reliable[key] = false;
                            checks.Add(Check("REFERENCE.ALTERNATE_ACTOR", previouslyReliable ? CheckStatus.Fail : CheckStatus.NotAssessable,
                                $"Alternate actor {actor} is outside team {key}'s provisional five; prior state reliable={previouslyReliable}. Unknown replay cannot establish an off-court contradiction.", alternate, [index], period));
                        }
                    }
                }
                if (clock == maximum) { candidates.Add((period, key, lineup.Order().ToArray(), group)); }
            }
        }

        var complete = priorPeriod >= 4 && priorClock == 0 && Enumerable.Range(1, Math.Max(0, priorPeriod)).All(p => boundaries.TryGetValue(p, out var b) && b.Start && b.End);
        foreach (var (key, box) in boxes)
        {
            var resolvedTeam = teamId(key);
            var compared = 0;
            var boxedPlayers = box.GetProperty("players").EnumerateArray().Select(p => p.Key("personId")).ToHashSet();
            if (minutes[key].Any(p => p.Value > 0 && !boxedPlayers.Contains(p.Key))) { reliable[key] = false; }
            var replayAssessable = complete && reliable[key];
            var liveBoxes = references.Boxes.Where(b => b.Mode == "liveboxscore" && b.Box.Number("gameStatus") == 3
                && b.Box.Text("gameClock") is "PT00M00.00S" or "PT00M00S").ToArray();
            foreach (var live in liveBoxes)
            {
                var liveTeam = new[] { live.Box.GetProperty("homeTeam"), live.Box.GetProperty("awayTeam") }.SingleOrDefault(t => t.Text("teamId") == key);
                if (liveTeam.ValueKind == JsonValueKind.Undefined) { continue; }
                var finalFive = liveTeam.GetProperty("players").EnumerateArray().Where(p => p.Text("oncourt") == "1").Select(p => p.Key("personId")).ToHashSet();
                var assessable = replayAssessable && finalFive.Count == 5;
                var matches = finalFive.SetEquals(active[key]);
                if (assessable && !matches) { reliable[key] = false; }
                checks.Add(new("REFERENCE.ALTERNATE_FINAL_FIVE", !assessable ? CheckStatus.NotAssessable : matches ? CheckStatus.Pass : CheckStatus.Fail,
                    $"Alternate substitution replay compared with final-status, zero-clock box-score oncourt flags. Complete reliable replay={replayAssessable}; flagged players={finalFive.Count}. Invalid/unknown replay or incomplete final-five flags cannot validate this comparison. Flags describe the final snapshot only.", EntityId: resolvedTeam,
                    Severity: assessable && !matches ? FindingSeverity.Warning : null));
            }
            foreach (var person in box.GetProperty("players").EnumerateArray())
            {
                var sourceId = person.Key("personId");
                var reported = SourceJson.Minutes(person.GetProperty("statistics").Text("minutes"));
                var replaySeconds = minutes[key].GetValueOrDefault(sourceId);
                if (reported is null)
                {
                    if (replaySeconds > 0) { reliable[key] = false; }
                    continue;
                }
                compared++;
                var difference = Math.Abs(replaySeconds - reported.Value * 60);
                // Nearest-second display is corroborated by positive and negative fractional deltas
                // in actual saved regulation/playoff/2OT responses. No tolerance repairs stint times.
                var agrees = difference <= 0.500001;
                if (!agrees) { reliable[key] = false; }
                checks.Add(new("REFERENCE.ALTERNATE_MINUTES", !replayAssessable ? CheckStatus.NotAssessable : agrees ? CheckStatus.Pass : CheckStatus.Fail,
                    $"Alternate replay vs full-game displayed minutes; nearest-second rounding interval ±0.5s, no widening for defects. Complete reliable replay={replayAssessable}; incomplete/contradictory replay totals are provisional, never passing minute validation. Snapshot {alternate.SnapshotId}.",
                    EntityId: playerId(sourceId), Stat: "MIN_SECONDS", Expected: reported.Value * 60, Observed: replaySeconds,
                    Severity: replayAssessable && !agrees ? FindingSeverity.Warning : null));
            }
            var qualified = complete && reliable[key] && compared > 0 && resolvedTeam is not null;
            checks.Add(new("REFERENCE.ALTERNATE_LINEUPS", qualified ? CheckStatus.Pass : CheckStatus.NotAssessable,
                $"Team {key}: complete boundaries={complete}, consistent five/actors={reliable[key]}, compared minute rows={compared}. Alternate NBA and box-score agreement is corroboration, not independent ground truth. Snapshot {alternate.SnapshotId}.", EntityId: resolvedTeam));
            if (!qualified) { continue; }
            foreach (var candidate in candidates.Where(c => c.Team == key && c.Period > 1))
            {
                var ids = candidate.Players.Select(playerId).ToArray();
                if (ids.Length != 5 || ids.Any(id => id is null)) { continue; }
                anchors.Add(new(candidate.Period, resolvedTeam!.Value, ids.Select(id => id!.Value).ToArray(),
                    $"Alternate official snapshot {alternate.SnapshotId}, opening-clock rows {string.Join(',', candidate.Rows)}; explicit substitutions replayed from box starters, all timed five/actor checks consistent and full-game minutes corroborated within display rounding.")
                { AfterOpeningClockSubstitutions = true });
            }
        }
        return new(anchors, checks);
    }

    private static bool ApplyBatch(HashSet<string> lineup, IEnumerable<JsonElement> substitutions)
    {
        var changes = substitutions.GroupBy(r => r.Key("personId")).ToArray();
        var next = lineup.ToHashSet();
        foreach (var player in changes)
        {
            if (player.Key == "" || player.Any(r => r.Text("subType") is not ("in" or "out"))) { return false; }
            var delta = player.Count(r => r.Text("subType") == "in") - player.Count(r => r.Text("subType") == "out");
            var membership = (lineup.Contains(player.Key) ? 1 : 0) + delta;
            if (membership is < 0 or > 1) { return false; }
            if (membership == 1) { next.Add(player.Key); } else { next.Remove(player.Key); }
        }
        if (next.Count != 5) { return false; }
        lineup.Clear();
        lineup.UnionWith(next);
        return true;
    }

    private static IEnumerable<int[]> Groups(JsonElement[] rows)
    {
        var group = new List<int>();
        for (var index = 0; index < rows.Length; index++)
        {
            if (group.Count > 0 && (rows[group[0]].Number("period") != rows[index].Number("period") || rows[group[0]].Text("clock") != rows[index].Text("clock")))
            { yield return group.ToArray(); group.Clear(); }
            group.Add(index);
        }
        if (group.Count > 0) { yield return group.ToArray(); }
    }

    private static CheckResult Check(string code, CheckStatus status, string message, AlternatePlayByPlay alternate, int[] indices, int? period = null) =>
        new(code, status, message, Period: period, Severity: status == CheckStatus.Fail ? FindingSeverity.Warning : null)
        {
            ReferenceRows = indices.Select(i => new BasketballSourceRowReference(BasketballDataSource.NbaOfficial, alternate.SnapshotId, i, null,
                alternate.Rows[i].Text("actionNumber"))).ToArray()
        };
}
