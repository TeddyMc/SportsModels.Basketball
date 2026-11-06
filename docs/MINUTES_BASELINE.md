# Historical regulation-minute models

Python fitting, evaluation and local HTML generation support [issue #20](https://github.com/TeddyMc/SportsModels.Basketball/issues/20). The issue owns modelling decisions and the wider factor exploration. This prototype does not complete that exploration.

## Run

Create an environment and install the pinned numerical dependencies (tested with Python 3.14). Generated data remains outside Git.

```powershell
python -m venv C:/Data/Basketball/tools/minutes-venv
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe -m pip install -r analysis/minutes/requirements.txt
$env:OPENBLAS_NUM_THREADS = '1'
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --catalog C:/Data/Basketball/converted/catalog.json --output C:/Data/Basketball/analysis/minutes-model-comparison
```

Use a new output directory for each fit. Required inputs are the five selected completed version-8-compatible runs from the catalog described in [NBA_CONVERSION.md](NBA_CONVERSION.md), canonical `games/`, `games.csv`, `player-minutes.csv` and matching manifests. Raw provider JSON is not read. Missing/malformed input, duplicate identities, invalid durations or absence of converged fits fail explicitly. Selected manifest/minute-CSV hashes and per-season population/quality counts are retained.

Open `index.html` directly in a browser. It contains the score table, a tab per selected model, starter/bench population comparisons, selectable-player actual/prediction and residual timelines, history IDs and error/coverage metrics. Charts/cards can display training, validation or both; the score table always reports both. Player selection never changes population scores or comparison plots. All charts use lines without scatter markers. Population lines show mean actual minutes within one-minute prediction bands, using every scored game; a dashed diagonal marks perfect prediction. Individual timelines use equal game spacing and dashed season boundaries.

`results.json` retains observations, history IDs, predictions, fitted parameters, optimizer diagnostics and split metrics. `comparison.csv` is the score table. `population-audit.json` records excluded inactive/unknown-eligibility observations. Refresh HTML without refitting or changing saved data, and run the offline tests:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --output C:/Data/Basketball/analysis/minutes-model-comparison --render-only
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe -m unittest discover -s analysis/minutes -v
```

## Models, fitting and validation

Parameters are fitted separately for starters and bench using **2021-22 through 2024-25 only**. **2025-26 is validation** and never enters fitting or parameter selection. Training MAE is an in-sample fitted score; validation uses frozen parameters.

History uses earlier NBA game dates in the same role, newest first. Same-day outcomes cannot influence another prediction. Histories cross seasons with no reset or role blending. Earlier completed validation games may supply history for later validation predictions; they never update parameters. This is rolling one-game-ahead validation, not a whole-season forecast at its start.

The default fitting objective is **training MAE in minutes**. `--loss mse` instead minimises **mean squared error in minutes squared**. MSE mode reports MSE as the objective plus RMSE (its square root, in minutes) and MAE as diagnostics. No likelihood, error distribution or variance parameter is fitted. These remain weighted historical-mean model forms; MAE fitting does not establish a general conditional-mean estimator. Normalised nonnegative weights keep predictions within the historical input range. There is no intercept or player-specific parameter.

To fit only individual 5 + mean next 5 with squared error and generate a single-model report:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --loss mse --models five_plus_five --output C:/Data/Basketball/analysis/minutes-five-plus-five-mse
```

Starting status is given for each target game. A starter uses previous starts; a bench player uses previous bench appearances. Fitted parameters are also separate by starting role. The consumer does not predict the target starting status or combine opposite-role history.

| Model | Parameters per role | Components |
|---|---|---|
| Exponential | Decay rho in [0,1] | Last 10 same-role games with weights proportional to rho^(lag-1); zero uses newest, one gives equal weights. |
| Exponential + newest | Decay rho, newest mass q | Newest receives q; remaining 1-q distributed exponentially over lags 2-10. |
| Individual 5 + mean next 5 | Six component weights, sum one | Individual lags 1-5, then one weight on the arithmetic mean of lags 6-10. |
| Individual 3 + mean next 3 | Four component weights, sum one | Individual lags 1-3, then one weight on the arithmetic mean of lags 4-6. |

Short histories use available components with renormalised weights. A partial tail block averages its available games. No history or unknown role gives no prediction. Missing actuals do not prevent predictions when history exists. Confirmed zeros enter history; missing totals never become zeros. A numerical floor of 1e-8 on free component/newest weights prevents degenerate empty effective histories. Exponential lag weights may be exactly zero at decay zero.

Scalar decay fitting uses a deterministic grid plus bounded local refinements. Other fits use deterministic multistart SLSQP with explicit constraints and diagnostics. Only converged finite fits are eligible; the lowest training objective is retained. MAE is nonsmooth, and multistart convergence does not certify global optimality. No optimizer start/window is selected on validation.

## Population, targets and limitations

History and targets are NBA regular season only (official game-ID prefix 002). The consumer uses canonical game-specific eligibility/role and matching CSV complete regulation seconds. Numeric PlausibleUncorroborated, WithComparisonWarnings, Corroborated and ConfirmedUnused observations are retained. Box-score agreement is not an admission gate. EstablishedSeconds is never substituted for an unknown complete total. Regulation portions of overtime games remain, overtime minutes are excluded. No foul, blowout, injury, rest, player-tier or performance filters/corrections are applied.

Explicit inactive and unknown-eligibility observations are excluded; unknown starting roles are never assigned to bench. Version-9 conversion outputs refresh eligibility and role labels across all five seasons, using source and reconstruction-derived evidence with provenance. The report exposes per-season eligibility, remaining unknown active roles, quality and scored counts. Missing complete totals or earlier history can still prevent scoring; use the actual run's counts rather than assuming every roster observation is assessable. The earlier version-8 comparison had severe missing-label coverage limitations and is superseded by the version-9 rerun.

Archived active/starter metadata supplies a retrospective pre-game scenario; original pre-tip publication timing is not established. There is no availability forecast for absent players. Conversion diagnostics and cross-feed comparisons provide corroboration rather than independent ground truth.

MAE averages absolute errors over rows with actual and prediction. Signed error is **actual minus predicted**, so positive means underprediction. Population, observed, predicted and evaluated counts are separate; unknowns never enter averages as zeros. Counts accompany the comparison to distinguish coverage from accuracy.

## Model versions and fixed benchmark

The latest squared-error individual-5-plus-mean-next-5 experiment is frozen as `benchmark-v1`. The persistent version registry is `C:/Data/Basketball/analysis/model-tracking/registry.json`; its `index.html` displays training/validation errors overall and separately for starters and bench. Choose RMSE, MSE or MAE in the table. `model-versions.csv` retains all metrics, counts, fit objectives and data hashes. Numerical outputs remain local, outside Git.

Every new fit automatically registers its results there. Use `--version candidate-v2` for an explicit immutable version ID; otherwise the output directory name is used. Version IDs cannot replace different predictions/parameters, and the benchmark cannot be silently replaced. Historical fit objectives stay recorded; they do not alter the definitions of displayed error metrics. Use a new registry/explicitly re-evaluated benchmark for changed data: population or target mismatches are rejected rather than presented as a model improvement.

To initialise a registry from an existing benchmark result, or add an existing experiment:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/registry.py --registry C:/Data/Basketball/analysis/model-tracking/registry.json --output C:/Data/Basketball/analysis/model-tracking --results C:/Data/Basketball/analysis/minutes-five-plus-five-mse-20261105/results.json --model five_plus_five --version benchmark-v1 --benchmark
```

Omit `--benchmark` for candidates and supply their results path, model key and new version. A new fit can designate its single selected model with `--benchmark`; a multi-model fit cannot. Before any benchmark is registered, fits are recorded but matched visual comparisons are unavailable. Run one registry writer at a time.

The version table compares each candidate on player-games where **both candidate and fixed benchmark predict**. Full-available-population scores are retained separately in the CSV/registry, which matters for future prior-only/cold-start predictions. Alignments verify registry/player/game identities, dates, roles, actuals and split; saved experiment fingerprints are checked on regeneration.

Selecting a version overlays the benchmark as grey dashed lines on individual prediction/residual timelines and population calibration curves. Calibration overlays use matched player-games. Player cards/timelines show all available observations, so future models' extra prior-only coverage remains inspectable without contaminating paired error comparisons. The benchmark tab does not duplicate its own curve.
