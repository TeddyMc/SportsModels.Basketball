# NBA conversion and diagnostic reports

The converter consumes local Stats `playbyplayv3` snapshots and the unified basketball contracts. Acquisition and conversion are separate commands. Scope and decisions belong to [issue #13](https://github.com/TeddyMc/SportsModels.Basketball/issues/13).

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- references C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/stats '0022500315,0022500173'
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- references C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/stats all boxscoretraditionalv3 2025-12-01 2025-12-01
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- convert C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/references C:/Data/Basketball/converted C:/Data/Basketball/identities.sqlite
```

Append a quoted comma-separated game selection to conversion to run a subset. Without a selection it processes all local JSON files. Quoting preserves the selection through PowerShell argument parsing. Exit zero means no structural conversion failures; individual games can still be partial and have failed or unassessable checks.

Supporting acquisition accepts optional game IDs, endpoint names and an inclusive date range. Use `all` to omit a selection. Endpoints are `boxscoretraditionalv3`, `boxscoretraditionalv3-range`, `boxscoretraditionalv3-opening`, `boxscoresummaryv2` and `commonteamroster`. Date selection uses the existing final-game discovery and can run without local PBP; interval probes require discovered or local period evidence. It never downloads PBP implicitly. New responses are retained under `<root>/<endpoint>/<gameId>/`; conversion also reads the earlier `<root>/<gameId>/` layout. Point conversion at the corresponding root. To schedule aligned acquisition, invoke the existing PBP date-range command and the reference date-range command separately; no scheduler or refresh window is activated here.

Each new run has a unique directory. A completion `manifest.json` identifies retained input bytes, converter assemblies and hashes, registry identity/revision and a registry backup. `started.json` without a completion manifest identifies an interrupted run. Existing runs are never overwritten. Canonical polymorphic games are under `games/`; companion original rows, field evidence and diagnostics are under `reports/`. `games.csv`, `checks.csv`, `findings.csv` and `summary.md` provide filterable results, denominators and readable coverage. No automatic retention cleanup runs.

Provider identities remain strings and resolve through typed SQLite mappings. Internal IDs are allocated, not provider-ID casts. An unresolved cross-provider candidate is retained without guessing. Exact retained snapshots preserve event IDs; cross-snapshot reuse requires an unchanged row and unique matching action ID. Changed or ambiguous correspondence receives a new snapshot-scoped identity with a finding. Action numbers are not unique identifiers. Keep the registry: rebuilding it invalidates dependent identities.

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- backup-registry C:/Data/Basketball/identities.sqlite C:/Data/Basketball/identities-backup.sqlite
```

Use a new backup filename. Restore while converters are stopped, preserving the original registry identity and history. Do not replace a live database or mix unrelated registry backups with saved output.

Every source row has a disposition. An auxiliary block/steal row can support one field-goal/turnover event. Exact duplicate rows contribute no second event. Conflicting action IDs and unsupported correction instructions remain unresolved companion rows. Original array ordering, descriptions and fields are retained. Typed canonical links require supported context; no synthetic correction or score-adjustment event is emitted.

Examples: a team heave with blank result and zero placeholder value retains unknown outcome/value and does not credit a player's attempt. A generic free-throw label can obtain a retained-possession category from a supported causal foul. A coach roster can resolve a coach identity while the source's missing charged-team value remains null. Ambiguous surname matches remain unresolved.

Checks distinguish pass, fail, not assessable and not applicable. Severity is separate. Unknown lineups prevent dependent minutes checks without preventing independent scoring checks. Comparable counting statistics use exact comparisons. Minute comparison requires explicit precision evidence; the converter currently supplies no assumed tolerance. Canonical events contain facts; running scores, lineups, stints, possession and derived statistics are diagnostics.

## Current verification limits

Actual period-only traditional-v3 probes returned full-game data. Range probes changed selections but did not establish reliable boundary inclusion or opening-second precision. These responses are retained, but period reconciliation is not claimed. Full-game starting positions establish Q1 only; later opening lineups remain unknown without sufficient evidence. Season rosters can reflect trades and are not by themselves historical game participation evidence.

Coordinates/zone transformations, occurrence UTC precision, team-heave counting exceptions and some foul/possession exceptions remain unverified and explicitly reported. Shot distance is populated when a description's explicit feet label exactly corroborates its numeric field; source zeros without that evidence remain unresolved. Special-format/unclassified games do not receive ordinary NBA rule assumptions. Reference absence never counts as passing validation.

The inspected 2025–26 traditional-v3 team turnover field equals its complete player sum in 22 of 22 pilot team responses. When that response-level identity holds, `ComparisonKeys` directs its `TOV` comparison to replay `PLAYER_TOV`. Replay still retains total `TOV` and `TEAM_ONLY_TOV` separately; no basketball turnovers are removed to force agreement. This normalization is restricted to the verified season/schema. Agreement between NBA endpoints remains corroboration rather than independent ground truth.

Same-clock jump-ball/lost-ball/steal credits are grouped when compatible actors and winner establish one control change. Same-clock credits after an ejection do not prove later participation. Reports include local score intervals that can be assessed after a prior unresolved score while cumulative totals remain unknown. `LINEUP.BREAK_SUBSTITUTIONS` records observed end/start-clock substitution counts and opening-range player counts without claiming complete break coverage or inferring an opening five.

Team-foul/PN observations are checked with per-period counters and bounded penalty logic under [2025–26 NBA Rule 12-B V–VII](https://cdn.nba.com/manage/2026/01/Official-2025-26-NBA-Playing-Rules.pdf). Ordinary offensive and double-personal charges do not increment team counters. Late-period and overtime allowances are checked separately; rare player-count exceptions remain investigation warnings. Personal foul observations use separate player counters.

The NBA documents a [2025–26 heave exception](https://www.nba.com/news/nba-heaves-rule-change-early-results), but source labels alone do not establish eligibility. Blank-result team attempts therefore remain unresolved.

Ordinary NUnit tests use saved small source/reference fixtures and synthetic seeded defects without NBA network access. Source selections retain original filenames, snapshot hashes and row indices; they are parser fixtures, not complete-game correctness proofs.
