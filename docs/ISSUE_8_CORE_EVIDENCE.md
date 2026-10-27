# Issue #8 core implementation evidence

This file is a delivery record of scope, verification and limitations. It is not runtime configuration or an additional specification. The consumer-facing guide is [BASKETBALL_CORE_CONTRACTS.md](BASKETBALL_CORE_CONTRACTS.md); PR #14 also records verification, so this evidence file has less ongoing value than that guide.

The user explicitly approved implementing only the specified core slice on 2026-10-27 after the full issue's Needs Clarification status was identified. The remaining schema is outside this approval. Branch: `task/8-basketball-core-contracts`, based on `origin/master` at `eb92d36`.

## Acceptance evidence

- Read repository instructions, coding practices, both workflow skills, issue #8 and its comments (none), and parent epic #4. Existing ingestion supplies raw data; no ingestion files or dependencies changed. Solution integration is the shared-file change. Existing untracked `.vs/` content is excluded.
- Added one dependency-free .NET 8 onion project with the exact twelve approved core declarations in `SportsModels.Data.Basketball.Contracts`: four enums, clock, source identity, game/team/player metadata, abstract event, substitution and free throw. Explicit enum values, internal long identities and nullable fields match the specification.
- Flat events retain sequence, clock and nullable UTC occurrence time. Free throws have nullable typed parent-foul IDs; there are no nested attempts, generic relationships or cumulative aggregates.
- The runnable consumer demonstrates internal/provider identities (including leading zeroes), home/away metadata, polymorphism, same-clock ordering, a substitution between attempts, querying a shared parent ID, null timestamps and a known UTC timestamp. The omitted foul record and synthetic second-overtime clock are explicitly documented as limitations.
- No constructor/getter tests were added: these declarations introduce no validation behavior. Existing NUnit coverage remains unchanged. The example supplies the requested deterministic construction/consumer evidence.

## Verification

- `dotnet build SportsModels.Basketball.sln --source https://api.nuget.org/v3/index.json`: passed, 0 warnings, 0 errors.
- `dotnet test SportsModels.Basketball.sln --no-build --no-restore`: passed 33, failed 0, skipped 0.
- `dotnet run --no-build --no-restore --project examples/SportsModels.Data.Basketball.Examples`: exit 0; four events printed in sequence, free throws separated by substitution, two attempts referencing foul 900, overtime 2 with 0.7 seconds remaining, UTC timestamp ending in Z.
- `git diff --check`: passed.

The initial ordinary build failed during vulnerability-data retrieval from configured NuGet sources; an elevated retry still failed on an inaccessible machine-level private feed. Restricting sources to public NuGet for the successful build retained vulnerability checks without altering repository/machine package configuration.

Sequential review covered declaration/specification correspondence and example semantics, then performance (no defensive copying or new runtime infrastructure), then layering/readability and dependency scope. No material issues remained.

## Limitations and handoff

Full issue #8 remains open. Other event types, coordinates, provenance, taxonomies and correction behavior still require specification. The sample is a partial synthetic stream with an unresolved parent foul, not a complete reconstructed game or live overtime fixture. Records do not enforce UTC kind, ordering, period consistency or reference resolution. Conversion/QA belongs to #13, and roster/box-score enrichment remains separate follow-up work. Contract compilation does not prove actual source reconstruction correctness.
