using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Application;

internal static class NbaSourceDiagnostics
{
    internal static IEnumerable<CheckResult> PeriodBreaks(JsonElement[] rows, ReferenceBundle references, bool ordinaryRules)
    {
        foreach (var period in rows.Select(r => r.Number("period")).Where(p => p > 1).Distinct().Order())
        {
            if (!ordinaryRules)
            {
                yield return new("LINEUP.BREAK_SUBSTITUTIONS", CheckStatus.NotAssessable, "Unclassified competition: ordinary NBA period duration cannot be assumed.", Period: period);
                continue;
            }
            var maximum = period <= 4 ? 720 : 300;
            bool AtBoundary(JsonElement row, int p, double seconds)
            {
                if (row.Number("period") != p || row.Text("actionType") != "Substitution") { return false; }
                try { return SourceJson.Remaining(row.Text("clock")) == seconds; }
                catch (FormatException) { return false; }
            }
            var before = rows.Count(r => AtBoundary(r, period - 1, 0));
            var after = rows.Count(r => AtBoundary(r, period, maximum));
            var opening = references.Boxes.Where(b => b.Period == period && b.Mode == "opening").ToArray();
            var candidates = opening.SelectMany(b => new[] { "homeTeam", "awayTeam" }.Select(side =>
            {
                var team = b.Box.GetProperty(side);
                return $"team {team.Text("teamId")}: {team.GetProperty("players").GetArrayLength()} returned players, {team.GetProperty("statistics").Text("minutes")} player-minutes";
            }));
            yield return new("LINEUP.BREAK_SUBSTITUTIONS", CheckStatus.NotAssessable,
                $"Observed {before} substitution rows at prior period zero and {after} at this period's opening clock. "
                + (opening.Length == 0 ? "No opening-range reference is available. " : string.Join("; ", candidates) + ". ")
                + "Counts document observed break coverage, not proof that all changes were recorded. Range boundary semantics are unverified; no next-period five is inferred.", Period: period);
        }
    }
}
