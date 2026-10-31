using System.Text;
using System.Text.Json;
using SportsModels.Data.Basketball.Contracts;
using SportsModels.Data.NbaOfficial.Contracts;
using SportsModels.Data.NbaOfficial.Definitions;
using SportsModels.Data.NbaOfficial.Interfaces;

namespace SportsModels.Data.NbaOfficial.Application;

internal sealed class NbaConversion(IIdentityRegistry registry, IBasketballValidator validator, IConversionWorkspace workspace) : INbaConversion
{
    internal const string Version = "nba-conversion-6";

    public async Task<ConversionRunResult> RunAsync(ConversionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Limit is <= 0) { throw new ArgumentOutOfRangeException(nameof(request), "Limit must be positive."); }
        var runId = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        var run = workspace.CreateRun(request.OutputDirectory, runId);
        var summaries = new List<(string Game, string Key, string Competition, string Status, int Rows, int Mapped, int Unknown, int Events, int FailedChecks, int Assessed, int NotAssessable)>();
        var aggregate = new Dictionary<(string Code, CheckStatus Status), (int Count, HashSet<string> Games)>();
        var snapshots = new List<InputSnapshot>();
        var failureCount = 0;
        var findings = new StringBuilder("game,input,code,status,severity,event,period,entity,stat,expected,observed,evidence\n");
        var substitutions = new StringBuilder(SubstitutionCoverage.Header + "\n");
        var substitutionCoverage = new List<(string Competition, int[] Counts)>();
        var selected = 0;
        var seen = new HashSet<string>();
        var retained = new HashSet<string>();
        var implementation = new List<object>();
        foreach (var assembly in new[] { typeof(NbaConversion).Assembly, typeof(IBasketballValidator).Assembly })
        {
            var bytes = await workspace.ReadAsync(assembly.Location, cancellationToken);
            var relative = Path.Combine("implementation", Path.GetFileName(assembly.Location));
            await workspace.WriteBytesAsync(Path.Combine(run, relative), bytes, cancellationToken);
            implementation.Add(new { Path = relative, Sha256 = SourceJson.Hash(bytes) });
        }
        await workspace.WriteJsonAsync(Path.Combine(run, "started.json"), new { RunId = runId, ConverterVersion = Version,
            RegistryId = registry.RegistryId, RegistryStartRevision = registry.Revision, CreatedAtUtc = DateTime.UtcNow,
            InputDirectory = Path.GetFullPath(request.InputDirectory), ReferenceDirectory = Path.GetFullPath(request.ReferenceDirectory),
            Selection = request.GameIds, Limit = request.Limit, Complete = false }, cancellationToken);

        foreach (var path in workspace.EnumerateInputs(request.InputDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected >= (request.Limit ?? int.MaxValue)) { break; }
            var gameId = "unknown";
            var key = "";
            var inputs = new List<InputSnapshot>();
            var dispositions = new List<SourceRowDisposition>();
            byte[]? inputBytes = null;
            try
            {
                var bytes = await workspace.ReadAsync(path, cancellationToken);
                inputBytes = bytes;
                var hash = SourceJson.Hash(bytes);
                key = hash;
                using var document = JsonDocument.Parse(bytes);
                gameId = document.RootElement.GetProperty("game").Text("gameId");
                if (request.GameIds is not null && !request.GameIds.Contains(gameId)) { continue; }
                if (gameId.Length != 10 || gameId.Any(c => !char.IsAsciiDigit(c))) { throw new InvalidDataException("Invalid game key."); }
                key = gameId + "-" + hash[..16];
                if (!seen.Add(key)) { continue; }
                selected++;
                inputs.Add(await Retain(path, bytes, "playbyplayv3", run, retained, cancellationToken));
                var references = workspace.ReadReferences(request.ReferenceDirectory, gameId);
                foreach (var (referencePath, referenceBytes) in references.Files)
                {
                    inputs.Add(await Retain(referencePath, referenceBytes, Path.GetFileName(referencePath).Split('-')[0], run, retained, cancellationToken));
                }
                var converted = registry.InTransaction(() => new NbaGameConverter(registry).Convert(bytes, hash, references));
                dispositions = converted.Rows;
                var validation = validator.Validate(converted.Game, converted.Context);
                var checks = converted.Checks.Concat(validation.Checks).ToArray();
                var substitutionSummary = SubstitutionCoverage.Calculate(gameId, converted, checks);
                substitutions.AppendLine(substitutionSummary.Csv);
                substitutionCoverage.Add((converted.Competition, substitutionSummary.Counts));
                var partial = dispositions.Any(r => r.Disposition == "Unresolved") || checks.Any(c => c.Status == CheckStatus.NotAssessable);
                var status = partial ? "Partial" : "ConvertedWithDiagnostics";
                var report = new GameConversionReport(runId, gameId, converted.Competition, status, registry.RegistryId, registry.Revision,
                    inputs, dispositions, converted.Evidence, checks, validation, null);
                await workspace.WriteJsonAsync(Path.Combine(run, "reports", key + ".json"), report, cancellationToken);
                await workspace.WriteJsonAsync(Path.Combine(run, "games", key + ".json"), converted.Game, cancellationToken);
                summaries.Add((gameId, key, converted.Competition, status, dispositions.Count, dispositions.Count(r => r.Disposition == "Mapped" || r.Disposition == "Supporting"),
                    dispositions.Count(r => r.Disposition == "Unresolved"), converted.Game.Events.Count,
                    checks.Count(c => c.Status == CheckStatus.Fail), checks.Count(c => c.Status is CheckStatus.Pass or CheckStatus.Fail), checks.Count(c => c.Status == CheckStatus.NotAssessable)));
                AddChecks(checks, gameId, key, aggregate, findings);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException
                or FormatException or OverflowException or IOException or UnauthorizedAccessException)
            {
                if (key == "") { key = "unreadable-" + Guid.NewGuid().ToString("N"); }
                if (inputs.Count == 0) { selected++; }
                if (inputs.Count == 0 && inputBytes is not null)
                { inputs.Add(await Retain(path, inputBytes, "playbyplayv3", run, retained, cancellationToken)); }
                failureCount++;
                var check = new CheckResult("CONVERSION.STRUCTURE", CheckStatus.Fail, exception.Message, Severity: FindingSeverity.Error);
                var report = new GameConversionReport(runId, gameId, "Unknown", "Failed", registry.RegistryId, registry.Revision,
                    inputs, dispositions, [], [check], null, exception.Message);
                await workspace.WriteJsonAsync(Path.Combine(run, "reports", key + ".failure.json"), report, cancellationToken);
                summaries.Add((gameId, key, "Unknown", "Failed", dispositions.Count, 0, dispositions.Count, 0, 1, 1, 0));
                AddChecks([check], gameId, key, aggregate, findings);
            }
            snapshots.AddRange(inputs);
        }
        var summary = new StringBuilder("game,input,competition,status,rows,accounted_mapped_or_supporting,unresolved,events,failed_checks,assessed_checks,not_assessable\n");
        foreach (var s in summaries)
        { summary.AppendLine(string.Join(',', new[] { s.Game, s.Key, s.Competition, s.Status, s.Rows.ToString(), s.Mapped.ToString(), s.Unknown.ToString(), s.Events.ToString(), s.FailedChecks.ToString(), s.Assessed.ToString(), s.NotAssessable.ToString() }.Select(Csv))); }
        var aggregates = new StringBuilder("code,status,results,affected_games\n");
        foreach (var (category, counts) in aggregate.OrderBy(x => x.Key.Code).ThenBy(x => x.Key.Status))
        { aggregates.AppendLine($"{Csv(category.Code)},{category.Status},{counts.Count},{counts.Games.Count}"); }
        await workspace.WriteTextAsync(Path.Combine(run, "games.csv"), summary.ToString(), cancellationToken);
        await workspace.WriteTextAsync(Path.Combine(run, "checks.csv"), aggregates.ToString(), cancellationToken);
        await workspace.WriteTextAsync(Path.Combine(run, "findings.csv"), findings.ToString(), cancellationToken);
        await workspace.WriteTextAsync(Path.Combine(run, "substitutions.csv"), substitutions.ToString(), cancellationToken);
        registry.Backup(Path.Combine(run, "registry.sqlite"));
        await workspace.WriteTextAsync(Path.Combine(run, "summary.md"), $"# NBA conversion diagnostics\n\nRun `{runId}`, converter `{Version}`, registry `{registry.RegistryId}` at revision {registry.Revision}.\n\n"
            + $"Attempted {summaries.Count}; converted {summaries.Count - failureCount}; failed {failureCount}. Source rows {summaries.Sum(s => s.Rows)}; unresolved {summaries.Sum(s => s.Unknown)}.\n\n"
            + "These outputs are retrospective diagnostic data. Converted does not mean fully validated or clean. Scoring/foul coverage, lineup/minutes coverage and wider possession/stat coverage must be assessed separately in each game's checks.\n\n"
            + "Reference absence, unverified interval/precision, coordinates, team heaves, coach-team metadata and uncertain period-opening lineups remain explicit limitations. No source repair is inferred. See games.csv, checks.csv and findings.csv for denominators and evidence; detailed original rows and field evidence are in reports/.\n\n"
            + "## Competition coverage\n\n" + string.Join('\n', summaries.GroupBy(s => s.Competition).Select(g => $"- {g.Key}: {g.Count()}")) + "\n\n"
            + SubstitutionCoverage.Summary(substitutionCoverage)
            + "## Priorities for #15\n\n" + string.Join('\n', aggregate.Where(x => x.Key.Status is CheckStatus.Fail or CheckStatus.NotAssessable)
                .OrderByDescending(x => x.Value.Games.Count).Take(20).Select(x => $"- {x.Key.Code}: {x.Key.Status}, {x.Value.Games.Count} games, {x.Value.Count} results.")) + "\n", cancellationToken);
        await workspace.WriteJsonAsync(Path.Combine(run, "manifest.json"), new
        {
            RunId = runId, ConverterVersion = Version, CheckVersion = Version,
            RegistryId = registry.RegistryId, RegistryRevision = registry.Revision,
            AssemblyVersion = typeof(NbaConversion).Assembly.GetName().Version?.ToString(),
            Implementation = implementation,
            Complete = true, Attempted = summaries.Count, Converted = summaries.Count - failureCount, Failed = failureCount,
            Inputs = snapshots.DistinctBy(s => s.SnapshotId).ToArray(), CompletedAtUtc = DateTime.UtcNow,
            HistoricalInputsAvailable = true, RegistrySnapshot = "registry.sqlite"
        }, cancellationToken);
        return new(runId, run, summaries.Count, summaries.Count - failureCount, failureCount);
    }

    private async Task<InputSnapshot> Retain(string path, byte[] bytes, string endpoint, string run, HashSet<string> retained, CancellationToken cancellationToken)
    {
        var hash = SourceJson.Hash(bytes);
        var relative = Path.Combine("inputs", hash + ".json");
        if (!retained.Add(hash))
        {
            if (!(await workspace.ReadAsync(Path.Combine(run, relative), cancellationToken)).SequenceEqual(bytes))
            { throw new InvalidDataException("Retained snapshot hash collision or corruption."); }
        }
        else { await workspace.WriteBytesAsync(Path.Combine(run, relative), bytes, cancellationToken); }
        DateTime? retrieved = null;
        var sidecar = path + ".retrieval.json";
        if (workspace.Exists(sidecar))
        {
            try
            {
                using var metadata = JsonDocument.Parse(await workspace.ReadAsync(sidecar, cancellationToken));
                if (metadata.RootElement.TryGetProperty("RetrievedAtUtc", out var time) && time.TryGetDateTime(out var value))
                { retrieved = value.ToUniversalTime(); }
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
            { /* Raw bytes remain retained; unavailable optional metadata supplies no retrieval timestamp. */ }
        }
        return new(hash, Path.GetFullPath(path), relative, hash, bytes.LongLength, endpoint, retrieved, true);
    }

    private static void AddChecks(IEnumerable<CheckResult> checks, string game, string key,
        Dictionary<(string Code, CheckStatus Status), (int Count, HashSet<string> Games)> aggregate, StringBuilder findings)
    {
        foreach (var check in checks)
        {
            var category = (check.Code, check.Status);
            var current = aggregate.GetValueOrDefault(category, (0, new HashSet<string>()));
            current.Item2.Add(game);
            aggregate[category] = (current.Item1 + 1, current.Item2);
            if (check.Status == CheckStatus.Pass || check.Status == CheckStatus.NotApplicable) { continue; }
            findings.AppendLine(string.Join(',', new[] { game, key, check.Code, check.Status.ToString(), check.Severity?.ToString(), check.EventId?.ToString(),
                check.Period?.ToString(), check.EntityId?.ToString(), check.Stat, check.Expected?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                check.Observed?.ToString(System.Globalization.CultureInfo.InvariantCulture), check.Evidence }.Select(Csv)));
        }
    }

    private static string Csv(string? value) => "\"" + (value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
