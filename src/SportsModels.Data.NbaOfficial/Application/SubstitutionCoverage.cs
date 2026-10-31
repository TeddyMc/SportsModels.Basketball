using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;

namespace SportsModels.Data.NbaOfficial.Application;

internal static class SubstitutionCoverage
{
    internal const string Header = "game,competition,source_rows,mapped_rows,unresolved_rows,source_batches,passed_batches,failed_batches,unassessable_batches,rows_in_passed_batches,rows_in_failed_batches,rows_in_unassessable_batches,team_periods,assessed_team_periods,alternate_outgoing,matched_alternate_outgoing,unmatched_alternate_outgoing,alternate_minute_passes,alternate_minute_failures,actor_passes,actor_failures,actor_unassessable,alternate_minute_unassessable";

    internal static string Row(string gameId, ConvertedGame converted, IReadOnlyList<CheckResult> checks) =>
        Calculate(gameId, converted, checks).Csv;

    internal static (string Csv, int[] Counts) Calculate(string gameId, ConvertedGame converted, IReadOnlyList<CheckResult> checks)
    {
        var source = converted.Rows.Where(r => r.Original.Text("actionType") == "Substitution").ToArray();
        var batches = new List<List<SourceRowDisposition>>();
        var block = 0;
        var prior = (Period: -1, Clock: "");
        var groups = new Dictionary<(int Block, string Team), List<SourceRowDisposition>>();
        foreach (var row in converted.Rows)
        {
            var current = (row.Original.Number("period"), row.Original.Text("clock"));
            if (current != prior) { block++; prior = current; }
            if (row.Original.Text("actionType") != "Substitution" || row.Disposition == "Duplicate") { continue; }
            var key = (block, row.Original.Key("teamId"));
            if (!groups.TryGetValue(key, out var group)) { group = []; groups[key] = group; batches.Add(group); }
            group.Add(row);
        }
        var counts = new Dictionary<CheckStatus, (int Batches, int Rows)>();
        foreach (var batch in batches)
        {
            var ids = batch.SelectMany(r => r.EventIds).ToHashSet();
            var result = checks.Where(c => c.Code == "LINEUP.SUBSTITUTION" && c.EventId is long id && ids.Contains(id)).ToArray();
            var status = batch.Any(r => r.Disposition == "Unresolved") || result.Length != 1 ? CheckStatus.NotAssessable : result[0].Status;
            var priorCount = counts.GetValueOrDefault(status);
            counts[status] = (priorCount.Batches + 1, priorCount.Rows + batch.Count);
        }
        var periods = checks.Where(c => c.Code == "MINUTES.COVERAGE").ToArray();
        var alternate = checks.Where(c => c.Code == "REFERENCE.SUBSTITUTION_COVERAGE" && c.ReferenceRows is not null).ToArray();
        var numbers = new[] { source.Length, source.Count(r => r.Disposition == "Mapped"), source.Count(r => r.Disposition == "Unresolved"),
            batches.Count, counts.GetValueOrDefault(CheckStatus.Pass).Batches, counts.GetValueOrDefault(CheckStatus.Fail).Batches,
            counts.GetValueOrDefault(CheckStatus.NotAssessable).Batches, counts.GetValueOrDefault(CheckStatus.Pass).Rows,
            counts.GetValueOrDefault(CheckStatus.Fail).Rows, counts.GetValueOrDefault(CheckStatus.NotAssessable).Rows,
            periods.Length, periods.Count(c => c.Status == CheckStatus.Pass), alternate.Length, alternate.Count(c => c.Status == CheckStatus.Pass),
            alternate.Count(c => c.Status == CheckStatus.Fail), checks.Count(c => c.Code == "REFERENCE.ALTERNATE_MINUTES" && c.Status == CheckStatus.Pass),
            checks.Count(c => c.Code == "REFERENCE.ALTERNATE_MINUTES" && c.Status == CheckStatus.Fail),
            checks.Count(c => c.Code == "LINEUP.ACTOR" && c.Status == CheckStatus.Pass),
            checks.Count(c => c.Code == "LINEUP.ACTOR" && c.Status == CheckStatus.Fail),
            checks.Count(c => c.Code == "LINEUP.ACTOR" && c.Status == CheckStatus.NotAssessable),
            checks.Count(c => c.Code == "REFERENCE.ALTERNATE_MINUTES" && c.Status == CheckStatus.NotAssessable) };
        return (gameId + ",\"" + converted.Competition.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"," + string.Join(',', numbers), numbers);
    }

    internal static string Summary(IReadOnlyList<(string Competition, int[] Counts)> games)
    {
        var text = new System.Text.StringBuilder("## Substitution coverage\n\nRows, clock/team batches and team-periods have different denominators. Assessable primary rows do not prove complete source history; review cross-feed discrepancies separately. Failed conversions are outside this table and remain in the run failure denominator.\n\n");
        text.AppendLine("| Competition | Games | Source rows | Rows in passed batches | Rows in failed batches | Rows in unassessable batches | Assessed / total team-periods | Matched / total alternate outgoing rows |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in games.GroupBy(g => g.Competition))
        {
            var totals = Enumerable.Range(0, group.First().Counts.Length).Select(i => group.Sum(g => g.Counts[i])).ToArray();
            text.AppendLine($"| {group.Key} | {group.Count()} | {totals[0]} | {totals[7]} | {totals[8]} | {totals[9]} | {totals[11]} / {totals[10]} | {totals[13]} / {totals[12]} |");
        }
        text.AppendLine("\nExact duplicate rows are excluded from effective batches but retained in source-row totals. Unresolved incoming participants remain unassessable. Per-game rows and all batch/minute denominators are in substitutions.csv.\n");
        return text.ToString();
    }
}
