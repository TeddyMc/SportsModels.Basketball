# Historical regulation-minute models

Python fitting, evaluation and local HTML generation support [issue #20](https://github.com/TeddyMc/SportsModels.Basketball/issues/20). The issue owns modelling decisions and the wider factor exploration. This prototype does not complete that exploration.

## Run

Create an environment and install the pinned numerical dependencies (tested with Python 3.14). Generated data remains outside Git.

```powershell
python -m venv C:/Data/Basketball/tools/minutes-venv
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe -m pip install -r analysis/minutes/requirements.txt
$env:OPENBLAS_NUM_THREADS = '1'
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --catalog C:/Data/Basketball/converted/catalog.json --population played --loss mse --models five_plus_five --benchmark --version benchmark-played-v1 --output C:/Data/Basketball/analysis/minutes-five-plus-five-played
```

Use a new output directory for each fit. Required inputs are the five selected completed version-8-compatible runs from the catalog described in [NBA_CONVERSION.md](NBA_CONVERSION.md), canonical `games/`, `games.csv`, `player-minutes.csv` and matching manifests. Raw provider JSON is not read. Missing/malformed input, duplicate identities, invalid durations or absence of converged fits fail explicitly. Selected manifest/minute-CSV hashes and per-season population/quality counts are retained.

Open `index.html` directly in a browser. It contains the score table, a tab per selected model, starter/bench population comparisons, selectable-player actual/prediction and residual timelines, history IDs and error/coverage metrics. Charts/cards can display training, validation or both; the score table always reports both. Player selection never changes population scores or comparison plots. All charts use lines without scatter markers. Population lines show mean actual minutes within one-minute prediction bands, using every scored game; a dashed diagonal marks perfect prediction. Individual timelines use equal game spacing and dashed season boundaries.

`results.json` retains observations, history IDs, predictions, fitted parameters, optimizer diagnostics and split metrics. `comparison.csv` is the score table. `population-audit.json` records excluded eligibility and explicit absence observations, including canonical reasons. Refresh HTML without refitting or changing saved data, and run the offline tests:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --output C:/Data/Basketball/analysis/minutes-model-comparison --render-only
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe -m unittest discover -s analysis/minutes -v
```

## Models, fitting and validation

Parameters are fitted separately for starters and bench using **2021-22 through 2024-25 only**. **2025-26 is validation** and never enters fitting or parameter selection. Training MAE is an in-sample fitted score; validation uses frozen parameters.

History uses earlier NBA game dates in the same role, newest first. Same-day outcomes cannot influence another prediction. Histories cross seasons with no reset or role blending. Earlier completed validation games may supply history for later validation predictions; they never update parameters. This is rolling one-game-ahead validation, not a whole-season forecast at its start.

For weighted-history models, the default fitting objective is **training MAE in minutes**. `--loss mse` instead minimises **mean squared error in minutes squared**. MSE mode reports MSE as the objective plus RMSE (its square root, in minutes) and MAE as diagnostics. These point models do not fit a likelihood or variance. They remain weighted historical-mean forms; MAE fitting does not establish a general conditional-mean estimator. Normalised nonnegative weights keep predictions within the historical input range. There is no intercept or player-specific parameter. The Bayesian models below instead fit training predictive likelihood and retain player-specific posterior states.

To fit only individual 5 + mean next 5 with squared error and generate a single-model report:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --loss mse --models five_plus_five --output C:/Data/Basketball/analysis/minutes-five-plus-five-mse
```

Starting status is given for each target game. A starter uses previous starts; a bench player uses previous bench appearances. Fitted parameters are also separate by starting role. The consumer does not predict the target starting status or combine opposite-role history.

The default `--population played` targets **regulation minutes conditional on a positive regulation appearance** (`minutes-given-positive-regulation-v1`). All zero-minute and confirmed non-playing rows, including coach decisions, are excluded before fitting, scoring and player history/state updates. A missed game does not reset the last observation date; calendar uncertainty still grows since the last usable same-role appearance. Missing complete totals remain null and never become zeros. The Gaussian observation family remains an approximation fitted on positive outcomes, not a truncated distribution. This iteration does not fit participation probabilities or return-from-absence effects.

Every fit writes `participation-ledger.json`: every represented regular-season roster row, chronologically ordered, with registry/player/team/game/date/season identities, eligibility, Played/Started, complete and reported regulation seconds, absence reason/description, quality/source references and admission/exclusion. This includes inactive players and active+injured/unused players. Null complete totals stay null even when Played=false; the status records non-participation without inventing a numeric minute label. The summary records the ledger path/hash, counts and status/reason breakdown. The Bayesian model and comparison report retain that same ledger reference for future return-from-injury or other absence effects. The ledger is not yet used as a model feature.

The historical `--population available` (`available-roster-v2`) population requires canonical `Eligibility=Active` and excludes explicit canonical injury, rest, personal, suspension, protocol, reconditioning, trade, ineligible and not-with-team reasons before building either targets or histories. `NWT_*` and `INACTIVE_*` reasons establish absence; supported `DNP_*`/`DND_*` absence causes are enumerated in `absence_reason`. Coach decisions (`DNP_COACH`/`DND_COACH`), missing reasons and unrecognised reasons remain admitted: zero alone does not establish unavailability. Available-unused zeros still affect fitting and future predictions. This uses canonical fields, never a separate provider parser. Archived statuses are retrospective scenario inputs, not proof of availability at a live prediction cutoff.

Recorded participation (`Played=true` or a positive established regulation total) takes precedence over a conflicting absence reason. Some feeds retain injury reasons even for players with corroborated appearances; these games remain admitted, including short injury exits. Population changes require a newly fitted 5+5 benchmark and a separate registry directory (`--registry <new-directory>/registry.json` for both fits). Historical registries remain immutable; scores from different populations must not appear as same-cohort improvements.

| Model | Parameters per role | Components |
|---|---|---|
| Exponential | Decay rho in [0,1] | Last 10 same-role games with weights proportional to rho^(lag-1); zero uses newest, one gives equal weights. |
| Exponential + newest | Decay rho, newest mass q | Newest receives q; remaining 1-q distributed exponentially over lags 2-10. |
| Individual 5 + mean next 5 | Six component weights, sum one | Individual lags 1-5, then one weight on the arithmetic mean of lags 6-10. |
| Individual 3 + mean next 3 | Four component weights, sum one | Individual lags 1-3, then one weight on the arithmetic mean of lags 4-6. |

Short histories use available components with renormalised weights. A partial tail block averages its available games. No history or unknown role gives no prediction. Missing actuals do not prevent predictions when history exists. Zeros enter history only in the historical `available` mode; the default conditional mode skips them. Missing totals never become zeros. A numerical floor of 1e-8 on free component/newest weights prevents degenerate empty effective histories. Exponential lag weights may be exactly zero at decay zero.

Scalar decay fitting uses a deterministic grid plus bounded local refinements. Other fits use deterministic multistart SLSQP with explicit constraints and diagnostics. Only converged finite fits are eligible; the lowest training objective is retained. MAE is nonsmooth, and multistart convergence does not certify global optimality. No optimizer start/window is selected on validation.

## Population, targets and limitations

History and targets are NBA regular season only (official game-ID prefix 002). The consumer uses canonical game-specific eligibility/role and matching CSV complete regulation seconds. Positive numeric PlausibleUncorroborated, WithComparisonWarnings and Corroborated observations are retained in conditional mode. ConfirmedUnused zeros remain in the participation ledger. Box-score agreement is not an admission gate. EstablishedSeconds is never substituted for an unknown complete total. Regulation portions of overtime games remain, overtime minutes are excluded. No foul, blowout, player-tier, performance or early-injury-exit corrections are applied; explicit non-playing absences and zero-minute games are outside the conditional target.

Explicit inactive and unknown-eligibility observations are excluded; unknown starting roles are never assigned to bench. Version-9 conversion outputs refresh eligibility and role labels across all five seasons, using source and reconstruction-derived evidence with provenance. The report exposes per-season eligibility, remaining unknown active roles, quality and scored counts. Missing complete totals or earlier history can still prevent scoring; use the actual run's counts rather than assuming every roster observation is assessable. The earlier version-8 comparison had severe missing-label coverage limitations and is superseded by the version-9 rerun.

Archived active/starter metadata supplies a retrospective pre-game scenario; original pre-tip publication timing is not established. There is no availability forecast for absent players. Conversion diagnostics and cross-feed comparisons provide corroboration rather than independent ground truth.

MAE averages absolute errors over rows with actual and prediction. Signed error is **actual minus predicted**, so positive means underprediction. Population, observed, predicted and evaluated counts are separate; unknowns never enter averages as zeros. Counts accompany the comparison to distinguish coverage from accuracy.

## Model versions and fixed benchmark

The current conditional benchmark is `benchmark-played-v1`. Its registry is `C:/Data/Basketball/analysis/model-tracking-played-v1/registry.json`; older zero-inclusive registries remain intact. Its `index.html` displays training/validation errors overall and separately for starters and bench. Choose RMSE, MSE or MAE in the table. `model-versions.csv` retains all metrics, counts, fit objectives and data hashes. Numerical outputs remain local, outside Git.

New fits default to that conditional registry. Use a different `--registry` for a different population or historical mode, and designate its single-model reference with `--benchmark`. Use `--version candidate-v2` for an explicit immutable version ID; otherwise the output directory name is used. Version IDs cannot replace different predictions/parameters, and the benchmark cannot be silently replaced. Historical fit objectives stay recorded; they do not alter the definitions of displayed error metrics. Use a new registry/explicitly re-evaluated benchmark for changed data: population or target mismatches are rejected rather than presented as a model improvement.

To initialise a registry from an existing benchmark result, or add an existing experiment:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/registry.py --registry C:/Data/Basketball/analysis/model-tracking-played-v1/registry.json --output C:/Data/Basketball/analysis/model-tracking-played-v1 --results C:/Data/Basketball/analysis/minutes-five-plus-five-played/results.json --model five_plus_five --version benchmark-played-v1 --benchmark
```

Omit `--benchmark` for candidates and supply their results path, model key and new version. A new fit can designate its single selected model with `--benchmark`; a multi-model fit cannot. Before any benchmark is registered, fits are recorded but matched visual comparisons are unavailable. Run one registry writer at a time.

The version table compares each candidate on player-games where **both candidate and fixed benchmark predict**. Full-available-population scores are retained separately in the CSV/registry, which matters for future prior-only/cold-start predictions. Alignments verify registry/player/game identities, dates, roles, actuals and split; saved experiment fingerprints are checked on regeneration.

Selecting a version overlays the benchmark as grey dashed lines on individual prediction/residual timelines and population calibration curves. Calibration overlays use matched player-games. Player cards/timelines show all available observations, so future models' extra prior-only coverage remains inspectable without contaminating paired error comparisons. The benchmark tab does not duplicate its own curve.

## Bayesian player-role states

`bayesian.py` fits separate role-level priors and observation variances, then maintains independent expected-minute means and variances for each player/role. A known role with no history receives its role prior; an unknown role remains unavailable. The shared filter predicts every game on a date before updating from that date. In the current conditional mode, zeros are excluded before filtering and never update the state; unknown actuals do not update it either. Historical zero-inclusive results remain unchanged. States use all earlier same-role observations through a recursive update, rather than a fixed ten-game window. Inspectable history counts reflect all those observations; the game-ID list shows only the latest ten.

The first implementation is `calendar_drift_fitted_cap`: before the next observation, latent variance grows by fitted daily variance times elapsed calendar days, capped at a separately fitted role-specific value. The cap is parameterised as initial variance plus a positive fitted extra. Posterior means stay unchanged while waiting; observations update means/variances. Noise variance remains separate, so predictive variance is latent variance plus observation noise and does not collapse as a mean becomes well established. A selected-player uncertainty plot shows the two pre-game standard deviations in minutes; saved results also retain posterior means/variances.

Parameters are fitted by Gaussian sequential predictive likelihood on training data only, with deterministic multistart L-BFGS-B and analytic gradients. Prior means are bounded to 0–48; positive variances are log-parameterised. Broad numerical log bounds are [-12,12] for prior/noise/extra-cap variance and [-18,8] for daily drift. Boundary hits and convergence are reported. Validation parameters stay frozen; only posterior player states update after observations. The Gaussian predictive distribution is an approximation with unbounded support, and uncertainty calibration is not guaranteed.

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/bayesian.py --reference-results C:/Data/Basketball/analysis/minutes-five-plus-five-played/results.json --dynamics calendar_drift_fitted_cap --version bayes-drift-cap-played-v1 --output C:/Data/Basketball/analysis/bayesian-cap-played
```

An explicit `--reference-results` is required to choose the frozen 5+5 benchmark, ensuring identical targets, labels, population policy and split. A new Bayesian fit saves `setup.json` with effect selection, reference-result hash and implementation hashes, alongside results and its report. Version IDs are immutable; use a new ID/output for a new fit.

## Interchangeable effects and batch studies

An effect study holds the dataset, prior/observation family, filtering/evaluation policy and optimizer procedure fixed while selecting a named implementation for an effect slot. The current slot is `uncertainty_dynamics`. `effects.py` defines its contract: named raw parameters and bounds, decoding to physical parameters, variance propagation, propagation derivatives, and deterministic starting values. New implementations register in `DYNAMICS`; the common filter, likelihood, fitter and study runner do not require implementation-specific branches. Give changed mathematical behaviour a new implementation/version identity. Tests should check propagation rules, gradients and no-leakage invariants.

`studies/uncertainty.json` is a historical zero-inclusive executable example with the one approved capped implementation. For conditional studies, copy its structure with the conditional reference result, new versions and matching conditional `--registry`. Add a separate experiment/version for each agreed setup; its `effects` mapping selects the implementation. The optional `reuse_results` references an already completed fit. The runner validates all IDs/effect selections and the benchmark population before fitting, reuses only matching completed version/setup results, fits missing setups sequentially, records per-setup success/failure, and preserves successful fits if another setup fails. It does not invent or automatically add unrequested variants.

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/study.py analysis/minutes/studies/uncertainty.json --output C:/Data/Basketball/analysis/uncertainty-study
```

The study saves its configuration and completion statuses and generates a report containing the selected setups plus the benchmark. The persistent registry/report is refreshed too. Mean and total predictive LL are selectable alongside errors, with training/validation overall, starter and bench scores and observation counts. Higher LL is better. Log scores are in nats using minutes as the observation unit; same-date observations of one player/role receive their joint Gaussian predictive score. Full coverage includes prior-only predictions and agrees with the training likelihood objective; matched coverage makes comparison with the fixed point benchmark fair. Point-only models have blank LL unless an explicit predictive distribution is supplied—no arbitrary residual variance is assigned to the benchmark. Saved experiment predictions/parameters are not changed when additional derived LL metrics are computed.
