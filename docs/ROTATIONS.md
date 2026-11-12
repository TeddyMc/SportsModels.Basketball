# Historical rotation viewer

The local viewer supports [issue #27](https://github.com/TeddyMc/SportsModels.Basketball/issues/27): 2025–26 NBA regular-season player histories and two-team game pages. It uses Python 3.11+ and the standard library, alongside the existing Python modelling tools. No model fitting or NBA network access occurs.

From the repository root:

```powershell
python analysis/rotations/viewer.py prepare --database C:/Data/Basketball/analysis/rotations/season.sqlite
python analysis/rotations/viewer.py serve --database C:/Data/Basketball/analysis/rotations/season.sqlite
```

Open <http://127.0.0.1:8727>. Choose a team, then a player; dates/opponents link to games, and game player names link back to the relevant team/player history. Direct links retain team context. All teams and associated players come from the selected season's game rosters, so a traded player can appear in multiple directories. Only this season and regular season are supported in this first version.

The landing page uses full team names. Team directories sort players by descending average regulation minutes in positive-minute appearances for that team. Each player has a season-average on-court-rate heatmap: each cell is the proportion of known endpoint states that were on court across those positive-minute games. DNP/inactive/zero-minute games are excluded; unknown states are excluded per cell, with the denominator shown on hover. Players with no positive-minute appearances remain listed at the bottom with N/A and unknown cells.

Preparation resolves the selected immutable run from `C:/Data/Basketball/converted/catalog.json` (override with `--catalog`). Read [NBA_CONVERSION.md](NBA_CONVERSION.md) for data coverage and uncertainty. It reads canonical `games/`, matching `reports/`, `games.csv` and the manifest, not raw provider JSON. Manifest discovery dates/away/home provenance supply the historical completed-fixture index. Missing canonical games retain fixture rows when manifest team/date provenance is available. Undiscovered fixtures are outside the catalog's coverage; the viewer cannot manufacture those. Future dates are omitted.

Preparation is a one-time full-season scan. SQLite stores indexed reusable per-game/per-player timelines; HTML is rendered only when requested. Cell details are embedded in each requested page, with no request on hover. Source catalog, manifest, canonical/report and optional model/ledger hashes are retained in SQLite's `info` table. Use a new database filename after changing inputs or viewer reconstruction code; preparation refuses to overwrite an existing index. Generated databases belong outside Git. The server binds only to `127.0.0.1`; stop with Ctrl+C. Change `--port` if needed.

## Optional saved model metadata

Supply the existing typical-minute Bayesian results and its source participation ledger during preparation:

```powershell
python analysis/rotations/viewer.py prepare --database C:/Data/Basketball/analysis/rotations/with-model.sqlite --results C:/Data/Basketball/analysis/bayesian-drift-cap-typical-v2-20261109/results.json --ledger C:/Data/Basketball/analysis/minutes-five-plus-five-typical-v2-20261109/participation-ledger.json
```

The adapter joins provider game ID, internal team ID and internal player ID, requiring matching identity registries. Use results/ledger prepared from the same selected canonical snapshot. `Rows[i].Observation` pairs with the first model's `PredictionsMinutes[i]`; these are pre-game expected typical-minute means. Post-observation means are not used. The ledger supplies inclusion and all available exclusions, including excluded/nonplaying rows absent from the model results. `TypicalRegulationSeconds` supplies adjusted observations. No adjustment, admission, or prediction rules are duplicated here. Missing metadata is N/A, including predictions for excluded rows the model did not score.

## Reading cells and rows

Each heatmap has 96 cells ending at 0:30, 1:00, …, 48:00. All events at or before a cell endpoint are applied in sequence. Court intervals include their start and exclude their end; a substitution at 6:00 affects the 6:00 cell. At 12:00, 24:00, 36:00 and 48:00, closing-quarter membership and closing-quarter foul thresholds are shown; next-quarter opening events apply to the next cell. Overtime is excluded from timelines, minutes and scoring rate; the final game differential can include overtime.

Blue means established on court, pale grey means established off court, and hatching means unknown. Missing complete minute observations remain N/A rather than zero; established positive intervals can still be shown. Actual minutes are the upstream regulation observation. Overlapping primary stints are merged; alternate stints are never silently substituted. Interval/total disagreements and failed upstream checks are flagged. These provisional diagnostics do not certify exact substitution timing.

Hover (or inspect the accessible label of a focused cell) for elapsed/quarter clock, court state, cumulative played minutes, personal fouls, team players in foul trouble, points, points per minute **played**, and score differential. The foul threshold is quarter + 1 (2/3/4/5). Personal-charge types match the converter; technical/bench fouls do not count. Points per minute is N/A until positive played time exists. Positive score differential means the player's team is trailing; negative means leading. Recorded canonical score diagnostics can restore a known score after an unresolved scoring event; unknown cumulative player points/fouls remain N/A. Malformed/nonchronological evidence is flagged.

Player histories retain every represented completed team fixture, including inactive, DNP, zero-minute, missing-PBP and absent-roster rows. Absence from a recorded game roster is labelled separately and does **not** establish historical contract membership, injury, or inactivity. Team-at-game is shown when recorded. Game pages group by team, put known starters first, then known bench, then unknown roles, sorting each group by actual regulation minutes. Incomplete starter sets are flagged rather than inventing five starters.

## Verification

```powershell
python -m unittest discover -s analysis/rotations -v
```

The offline suite covers reconstruction endpoints, scoring rate/sign, personal fouls and quarter thresholds, missing values, interval inconsistencies, model joins, trades/roster gaps, chronological fixtures, starter ordering and HTML navigation. The viewer remains exploratory; source coverage limitations and unavailable model classifications remain visible.
