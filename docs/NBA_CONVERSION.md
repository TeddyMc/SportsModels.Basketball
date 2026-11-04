# NBA conversion and diagnostic reports

The converter consumes local Stats `playbyplayv3` snapshots and the unified basketball contracts. Acquisition and conversion are separate commands. Scope and decisions belong to [issue #13](https://github.com/TeddyMc/SportsModels.Basketball/issues/13).

## Acquire a complete input bundle

Use the normal acquisition command to download primary Stats PBP together with full-game Stats traditional boxscores, CDN PBP and CDN boxscores:

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- acquire 2021-10-01 2026-06-30 C:/Data/Basketball/NbaOfficial
```

The root contains `stats/playbyplayv3`, `references`, and `_acquisition`. Subsequent conversion uses those first two paths. Dates are inclusive, must end before today UTC and use the NBA game date. A continuous multi-year range includes any summer games discovered by the official scoreboard; inspect competition coverage rather than assuming only regular-season games. [Issue #22](https://github.com/TeddyMc/SportsModels.Basketball/issues/22) owns the combined five-season audit and acquisition/process refinements.

One invocation includes sequential five-second request pacing, up to six attempts per transiently failing resource, a thirty-second retry delay and a thirty-minute cooldown after three consecutive transient failures. There are at most twelve cooldowns per invocation. Timeouts, connection failures, HTTP 403/408/429 and 5xx can recover; other HTTP failures, malformed/empty payloads, damaged retained data and storage errors remain explicit failures. The final report records remaining gaps; unavailable upstream data cannot be guaranteed. A stalled or incomplete run can be resumed with the identical command. Ctrl+C cancels request delays and cooldowns as well as network requests.

`_acquisition/runs` contains bounded JSONL progress and a JSON completion report. The summary counts expected/discovered dates, discovered games and four expected resources per game, downloaded/skipped files and remaining failures. Exit zero requires complete discovery and all expected resources; an early halt or any gap returns one. Invalid setup returns two and user cancellation returns 130. A failed discovery date never counts as an empty successful day. Download completion is separate from conversion/validation success.

Successful per-date discovery metadata (including empty days) is cached with its content hash. Retained PBP and reference snapshots must match the requested game and have nonempty action/roster schemas. Primary hashes are recorded on first verified adoption and checked on later resumes; reference hashes come from content-addressed filenames. Corrupt or ambiguous retained files are reported and left untouched. Raw responses are never reserialized or overwritten. Cached discovery describes that retained snapshot; use a fresh root for an intentional source refresh. Run one acquisition per root at a time.

Applications resolve `INbaDataAcquisition` through `PlayByPlayModule` and call `RunAsync(new DataAcquisitionRequest(start, end, root), token)`. The request exposes pacing, retry/cooldown limits and `IProgress<DataAcquisitionProgress>` for a caller's single action/button and status display. `DataAcquisitionResult.Complete` reflects acquisition coverage only. Conversion remains an independent local-data operation.

## Local converted datasets

Downstream tasks should begin with the local index `C:/Data/Basketball/converted/catalog.json` and [issue #22](https://github.com/TeddyMc/SportsModels.Basketball/issues/22). The catalog identifies the deliberately selected immutable run for each season and its coverage status. It is maintained when a run is published for downstream inspection; it is not advanced just because a newer directory exists. Raw data and generated outputs are local and are not committed to Git.

The five-season diagnostic outputs currently live under `C:/Data/Basketball/converted/five-season-20261102/<run-id>/`. Read canonical unified game JSON from each selected run's `games/`. Its matching `reports/`, `games.csv`, `checks.csv`, `findings.csv`, `substitutions.csv` and `manifest.json` describe uncertainty, assessment denominators, source lineage and retained inputs. Use one run's data and evidence together; keep its registry backup and retained snapshots. Modelling consumers do not need to parse raw provider JSON to use the canonical events.

These outputs are provisional diagnostics. Primary PBP is retained for all discovered games, but older-season supporting references and the final cross-season review remain incomplete. Zero structural conversion failures do not certify clean data or complete on-court history. The catalog records those limitations; #15 owns any subsequently agreed cleaned subset. Refresh the catalog with actual coverage when a replacement run is ready for inspection, preserving old runs.

## Convert and inspect

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- references C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/stats '0022500315,0022500173'
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- references C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/stats all boxscoretraditionalv3 2025-12-01 2025-12-01
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- convert C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/references C:/Data/Basketball/converted C:/Data/Basketball/identities.sqlite
```

Append a quoted comma-separated game selection to conversion to run a subset. Without a selection it processes all local JSON files. Quoting preserves the selection through PowerShell argument parsing. Exit zero means no structural conversion failures; individual games can still be partial and have failed or unassessable checks.

Supporting acquisition accepts optional game IDs, endpoint names and an inclusive date range. Use `all` to omit a selection. Endpoints are `boxscoretraditionalv3`, `boxscoretraditionalv3-full` (full game only), `boxscoretraditionalv3-range`, `boxscoretraditionalv3-opening`, `boxscoresummaryv2`, `commonteamroster`, `gamerotation`, `liveplaybyplay` and `liveboxscore`. The latter two explicitly select the alternate official CDN feeds. Rotation availability is not assumed. Date selection uses the existing final-game discovery and can run without local PBP; interval probes require discovered or local period evidence. Acquisition never downloads a PBP endpoint that was not selected. New responses are retained under `<root>/<endpoint>/<gameId>/`; conversion also reads the earlier `<root>/<gameId>/` layout. Point conversion at the corresponding root. To schedule aligned acquisition, invoke the existing PBP date-range command and the reference date-range command separately; no scheduler or refresh window is activated here.

Use `references-resume` with the same arguments to acquire missing files: retained JSON and content hashes are verified before skipping network requests, and missing requests are paced two seconds apart. Five consecutive failures halt the run. `_acquisition/` retains completion counts, failures and halt status; downloaded counts do not establish schema usability or complete coverage. Normal `references` still requests new snapshots. Neither mode overwrites old revisions. Conversion refuses ambiguous revisions rather than selecting by timestamp.

Each new run has a unique directory. A completion `manifest.json` identifies retained input bytes, converter assemblies and hashes, registry identity/revision and a registry backup. `started.json` without a completion manifest identifies an interrupted run. Existing runs are never overwritten. Canonical polymorphic games are under `games/`; companion original rows, field evidence and diagnostics are under `reports/`. `games.csv`, `checks.csv`, `findings.csv` and `summary.md` provide filterable results, denominators and readable coverage. No automatic retention cleanup runs.

Provider identities remain strings and resolve through typed SQLite mappings. Internal IDs are allocated, not provider-ID casts. An unresolved cross-provider candidate is retained without guessing. Exact retained snapshots preserve event IDs; cross-snapshot reuse requires an unchanged row and unique matching action ID. Changed or ambiguous correspondence receives a new snapshot-scoped identity with a finding. Action numbers are not unique identifiers. Keep the registry: rebuilding it invalidates dependent identities.

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- backup-registry C:/Data/Basketball/identities.sqlite C:/Data/Basketball/identities-backup.sqlite
```

Use a new backup filename. Restore while converters are stopped, preserving the original registry identity and history. Do not replace a live database or mix unrelated registry backups with saved output.

Every source row has a disposition. An auxiliary block/steal row can support one field-goal/turnover event. Exact duplicate rows contribute no second event. Conflicting action IDs and unsupported correction instructions remain unresolved companion rows. Original array ordering, descriptions and fields are retained. Typed canonical links require supported context; no synthetic correction or score-adjustment event is emitted.

Examples: a team heave with blank result and zero placeholder value retains unknown outcome/value and does not credit a player's attempt. A generic free-throw label can obtain a retained-possession category from a supported causal foul. A coach roster can resolve a coach identity while the source's missing charged-team value remains null. Ambiguous surname matches remain unresolved.

Written given-name prefixes such as `Jal. Williams`, `Jay. Williams` and `St. Curry` can match a literal prefix of the verified game-roster given name with an exact surname. Candidate uniqueness is still required within the applicable team or matched alternate incoming rows. `J. Williams` remains unresolved when both Jalen and Jaylin are compatible; no fuzzy spelling, invented nickname or arbitrary pairing is introduced.

Checks distinguish pass, fail, not assessable and not applicable. Severity is separate. Unknown lineups prevent dependent minutes checks without preventing independent scoring checks. Comparable counting statistics use exact comparisons. Full-game displayed minutes use a nearest-second rounding interval of ±0.5 seconds when the alternate feed is available: inspected regulation, playoff and double-overtime snapshots have fractional differences on both sides of the displayed second. Disagreements remain findings; the bound is never widened to fit them. Canonical events contain facts; running scores, lineups, stints, possession and derived statistics are diagnostics.

`substitutions.csv` distinguishes primary substitution rows, clock/team batches and team-period denominators, including unresolved rows and unassessable batches. Rows belonging to an assessed batch are counted separately from batches. Exact cross-feed outgoing matches and discrepancies, and alternate replay minute results, include games that cannot supply reliable lineup anchors. Structural conversion failures are reported in `games.csv` and must be included when reporting dataset coverage.

Cross-feed substitution coverage is checked in both directions: alternate rows missing from the primary snapshot and primary rows lacking a unique alternate counterpart. Snapshot disagreements do not establish which revision is correct. The per-game table also counts incoming IDs explicitly linked to alternate source rows and duplicate primary substitution rows.

`LINEUP.ACTOR` checks attributed shooters, assists, blocks, steals, rebounds, turnovers and ordinary personal fouls against the inferred five for the relevant team, including defensive credits stored on the opponent's event. Each credited actor has a pass/fail/not-assessable result and statistic label; `substitutions.csv` includes per-game denominators. A contradiction invalidates subsequent lineup-dependent state until a corroborated opening restores it. Unknown lineups and that player's same-clock substitutions are unassessable; unchanged players can still be assessed at another player's substitution clock. Technical/bench/coach actions do not establish on-court participation; nonordinary free throws and flagrant-foul contexts remain unassessable where eligibility/replacement exceptions are not resolved. An attributed player can therefore be checked without falsely asserting every named participant must be on court.

## Current verification limits

Actual period-only traditional-v3 probes returned full-game data. Range probes changed selections but did not establish reliable boundary inclusion or opening-second precision. These responses are retained, but period reconciliation is not claimed. Full-game starting metadata establishes Q1 only. CDN boxes use the explicit `starter` flag; a bench player's position is not starter evidence. Season rosters can reflect trades and are not by themselves historical game participation evidence.

For supported ordinary games, a unique matched alternate PBP snapshot can establish later-period anchors by replay from box starters, but only when all period boundaries, five-player membership, eligible actor checks and every available full-game player-minute comparison agree. Any final-status zero-clock CDN box `oncourt` flags are checked against the replayed final five; these flags are a final snapshot, never a per-event lineup. Event `personIdsFilter` lists do not establish a complete on-court five. Alternate replay and another NBA endpoint are corroboration, not independent ground truth; matching totals alone cannot validate substitution timing.

Later anchors describe the five after all opening-clock changes. The validator compares the primary stream's prior closing five plus observed break changes with that anchor, reports discrepancies and then resumes from the corroborated state without applying those changes twice. Missing primary break rows remain findings. Same-clock round trips are evaluated by net membership with no positive-duration intermediate stint or invented unique order. Incoming IDs may be linked to alternate rows only with exact outgoing context and a unique compatible incoming actor; ambiguous cases stay unresolved. Detailed reports retain anchor explanations and source-row references.

Coordinates/zone transformations, occurrence UTC precision, team-heave counting exceptions and some foul/possession exceptions remain unverified and explicitly reported. Shot distance is populated when a description's explicit feet label exactly corroborates its numeric field; source zeros without that evidence remain unresolved. Special-format/unclassified games do not receive ordinary NBA rule assumptions. Reference absence never counts as passing validation.

The inspected 2025–26 traditional-v3 team turnover field equals its complete player sum in 22 of 22 pilot team responses. When that response-level identity holds, `ComparisonKeys` directs its `TOV` comparison to replay `PLAYER_TOV`. Replay still retains total `TOV` and `TEAM_ONLY_TOV` separately; no basketball turnovers are removed to force agreement. This normalization is restricted to the verified season/schema. Agreement between NBA endpoints remains corroboration rather than independent ground truth.

Same-clock jump-ball/lost-ball/steal credits are grouped when compatible actors and winner establish one control change. Same-clock credits after an ejection do not prove later participation. Reports include local score intervals that can be assessed after a prior unresolved score while cumulative totals remain unknown. `LINEUP.BREAK_SUBSTITUTIONS` records observed end/start-clock substitution counts and opening-range player counts without claiming complete break coverage or inferring an opening five.

Team-foul/PN observations are checked with per-period counters and bounded penalty logic under [2025–26 NBA Rule 12-B V–VII](https://cdn.nba.com/manage/2026/01/Official-2025-26-NBA-Playing-Rules.pdf). Ordinary offensive and double-personal charges do not increment team counters. Late-period and overtime allowances are checked separately; rare player-count exceptions remain investigation warnings. Personal foul observations use separate player counters.

The NBA documents a [2025–26 heave exception](https://www.nba.com/news/nba-heaves-rule-change-early-results), but source labels alone do not establish eligibility. Blank-result team attempts therefore remain unresolved.

Ordinary NUnit tests use saved small source/reference fixtures and synthetic seeded defects without NBA network access. Source selections retain original filenames, snapshot hashes and row indices; they are parser fixtures, not complete-game correctness proofs.
