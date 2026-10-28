# NBA conversion and diagnostic reports

The converter consumes local Stats `playbyplayv3` snapshots and the unified basketball contracts. Acquisition and conversion are separate commands. Scope and decisions belong to [issue #13](https://github.com/TeddyMc/SportsModels.Basketball/issues/13).

```powershell
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- references C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/references '0022500315,0022500173'
dotnet run --project src/SportsModels.Data.NbaOfficial.Host -- convert C:/Data/Basketball/NbaOfficial/stats/playbyplayv3 C:/Data/Basketball/NbaOfficial/references C:/Data/Basketball/converted C:/Data/Basketball/identities.sqlite
```

Append a quoted comma-separated game selection to conversion to run a subset. Without a selection it processes all local JSON files. Quoting preserves the selection through PowerShell argument parsing. Exit zero means no structural conversion failures; individual games can still be partial and have failed or unassessable checks.

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

Coordinates, distance/zone transformations, occurrence UTC precision, team-heave counting exceptions and some foul/possession exceptions remain unverified and explicitly reported. Special-format/unclassified games do not receive ordinary NBA rule assumptions. Reference absence never counts as passing validation. Turnover discrepancies may involve team-only credit definitions; agreement between NBA endpoints is corroboration rather than independent ground truth. Findings require review before attribution to source defects or repair under #15.

Ordinary NUnit tests use saved small source/reference fixtures and synthetic seeded defects without NBA network access. Source selections retain original filenames, snapshot hashes and row indices; they are parser fixtures, not complete-game correctness proofs.
