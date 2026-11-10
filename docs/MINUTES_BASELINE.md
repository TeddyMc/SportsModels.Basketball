# Historical regulation-minute models

Python fitting, evaluation and local HTML generation support [issue #20](https://github.com/TeddyMc/SportsModels.Basketball/issues/20). The issue owns modelling decisions and acceptance. The current typical-minute workflow is ready for calibration review after a successful fit; later disturbance refinements are separately scoped.

## Expected typical minutes

Use `--typical --population played` for the target in [#20](https://github.com/TeddyMc/SportsModels.Basketball/issues/20): expected regulation minutes under competitive, undisturbed deployment, conditional on playing and known starting role. This is an upstream rotation indicator. The old played-conditional actual-minute runs remain historical comparisons on different targets.

```powershell
$env:OPENBLAS_NUM_THREADS = '1'
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/train.py --typical --population played --loss mse --models five_plus_five --benchmark --version benchmark-typical-v2 --registry C:/Data/Basketball/analysis/model-tracking-typical-v2/registry.json --output C:/Data/Basketball/analysis/minutes-five-plus-five-typical-v2-20261109
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/bayesian.py --reference-results C:/Data/Basketball/analysis/minutes-five-plus-five-typical-v2-20261109/results.json --version bayes-drift-cap-typical-v2 --registry C:/Data/Basketball/analysis/model-tracking-typical-v2/registry.json --output C:/Data/Basketball/analysis/bayesian-drift-cap-typical-v2-20261109
```

For a repeated target-construction pass, `--typical-cache <prior-typical-results.json>` explicitly reuses its hashed chronological ledger's court intervals. Canonical game and matching diagnostic hashes are verified; current score observations and personal-foul checks are reread, all gates/admission and histories are rebuilt, and both models are refitted. Predictions, optimizer results and prior admission decisions are not reused. The source result/ledger hashes stay in the new provenance. Without this option, all court intervals are read from the selected canonical reports. The initial replay-strict `typical-v1` run is diagnostic history, not the delivered cohort.

Both fits use the same filtered and adjusted historical labels. The existing training/validation seasons, separate starter/bench states, earlier-date updates and Gaussian dynamics remain. Use a new output directory and version for a changed target or input selection. The CLI defaults to the separate typical registry for this target; the commands make it explicit. Do not add this target to a historical played-minute registry.

`typical.py` consumes canonical events and the matching report's `Validation.Checks` and primary `Stints`. The competitive gate uses explicitly recorded PBP home/away score observations through minute 44, and excludes missing, contradictory or nonchronological score evidence. The converter records these in `SCORE.OBSERVATION` diagnostic evidence; the adapter accepts the exact supported diagnostic format and does not parse provider rows. A `NotAssessable` cumulative replay does not erase a complete recorded score observation (for example after an unresolved earlier heave). A failed score comparison remains a contradiction. Known canonical scoring increments bridge observations; unknown scoring without a recorded score remains excluded. It checks both 38:00 and 44:00 and every intervening before/after-event state. Scores carry between events: a constant margin must still satisfy the falling threshold at the end of each interval. Equality passes. The line is 27 points at 38 and 15 at 44. Later score changes do not affect this gate.

Personal foul types follow the converter's personal-count classification; technical/bench charges do not increment personal counts. Q+1 triggers on reaching 2/3/4/5 cumulative fouls in Q1/Q2/Q3/Q4. A subsequent canonical substitution or ejection exit in event order before the quarter-end clock excludes the player's game; an earlier exit or a zero-clock exit alone does not. Missing or contradictory personal-count evidence remains an exclusion. This is a heuristic, not a causal diagnosis. Starters with no Q3/Q4 playing interval are excluded; bench players do not receive this proxy.

Court intervals are merged before calculating durations. Minute-44 membership is after substitutions at that clock (intervals include the start and exclude the end). For players on court at 44, add only their off-court seconds between 44 and 48. Original complete regulation totals must match the established court-interval total within 0.01 seconds; otherwise membership/extension evidence is treated as incomplete. Regulation portions of overtime games remain; overtime intervals are ignored.

Canonical source files are unchanged. Model observations preserve `ActualRegulationSeconds`, `TypicalRegulationSeconds`, `LateExtensionSeconds`, `OnCourtAt44`, `CourtIntervals`, `TypicalExclusions` and foul-exit event evidence. The existing model-facing `RegulationSeconds` field contains the adjusted target in this mode. All errors and likelihoods use that target. The full chronological participation ledger preserves every represented row and its admission status. Exclusion counts may overlap; primary admission counts are separate from all triggered typical-gate reasons.

`Summary.Sources[].TypicalGames` retains per-game gate outcomes, sampled margins/thresholds and canonical/report hashes. `Summary.TypicalAudit` reports training/validation and role coverage, overlapping exclusions, extension counts and sizes. The comparison HTML includes this audit and plots adjusted labels with a dashed original-actual line. Predictive uncertainty refers to the next eligible typical-minute label, not an actual-game disturbance forecast. Historical eligibility and extensions are retrospective labels and never target-game forecast features. No participation, injury, blowout or foul correction model is fitted.

### Consistent admission through fitting, scoring and plots

The admitted typical-minute observations are the single numerical population for weighted histories, Bayesian state updates, training/validation errors and current report points. Excluded player-games remain in the participation ledger and exclusion audit; they supply no minute observation to model states, residuals, calibration curves or player timelines. This includes qualifying foul exits, starters without second-half minutes, noncompetitive games and unknown/incomplete observations. The original-actual overlay uses original minutes from those same admitted rows, so it does not reintroduce excluded games.

The five-game role-continuity panel further subsets those eligible target rows. Its full-ledger lookback checks historical participation and starting-role metadata against actual team fixtures; it does not restore excluded historical minutes to the fitter or plotted outcomes. Matched/full-coverage controls only change prediction availability, never target admission or adjusted labels. Calendar variance can propagate across excluded dates without receiving an observation update.

### Rotation consumer handoff

The typical Bayesian `results.json` pairs `Rows[i].Observation` identities/dates/known role with `Models[0].PredictionsMinutes[i]`. This is the pre-game expected typical-minute mean. `LatentVarianceMinutesSquared[i]` measures uncertainty about that mean; `PredictiveVarianceMinutesSquared[i]` adds observation noise for the next eligible adjusted label. Take square roots for standard deviations in minutes. `PosteriorMeansMinutes` and `PosteriorVariancesMinutesSquared` are after-observation states and must not be used as that game's pre-game forecast. `LatestHistoryDates` and `HistoryCounts` identify the earlier-date state evidence.

The file contains eligible historical examples, not a live service or a complete-game participation forecast. Downstream rotation work must define its own prospective input and disturbance handling. Start from the frozen role parameters and dynamics in `setup.json`/`Models[0].Roles`, and use only earlier completed eligible labels. Do not supply the target game's eventual eligibility, adjustment, realised fouls or score path as inputs. #21/#10/#19 own their rotation/compatibility specifications.

## Historical actual-minute runs

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

`lag_influence.py --results <saved-conditional-Bayesian-results.json> --output <diagnostic-directory>` replays exact linear coefficients of past outcomes and the role prior, checking that they reconstruct every saved predicted mean. No fitting is performed. The HTML shows separate starter/bench per-lag and cumulative weights on benchmark-matched validation predictions, with an optional at-least-50-prior-appearances cohort. Cumulative 10th-90th percentile bands describe variation across predictions, not confidence intervals. Prior/older-than-200 shares and per-game summaries remain explicit. This is influence on the mean with fitted parameters fixed, not information gain or incremental accuracy from adding lags. Install `requirements-plots.txt` for plotting. Reports and figures remain outside Git.

For descriptive distributions of actual minutes in the low/high prediction tails, install `analysis/minutes/requirements-plots.txt` and run `tail_distributions.py --results <saved-Bayesian-results.json> --output <diagnostic-directory>`. It uses benchmark-matched, positive-minute validation observations, selecting bottom/top 10% of predictions separately by role (`--tail-fraction` changes this). Ties at the quantile cutoff are included. Four line-only empirical densities use two-minute bins on shared axes, with group mean prediction/actual references. HTML, a four-panel PDF, selected-game CSV and counts/densities/source-hash JSON are saved outside Git. Each density integrates to one. No model is refitted; only identical input/selection settings can regenerate an existing diagnostic directory.

An effect study holds the dataset, prior/observation family, filtering/evaluation policy and optimizer procedure fixed while selecting a named implementation for an effect slot. The current slot is `uncertainty_dynamics`. `effects.py` defines its contract: named raw parameters and bounds, decoding to physical parameters, variance propagation, propagation derivatives, and deterministic starting values. New implementations register in `DYNAMICS`; the common filter, likelihood, fitter and study runner do not require implementation-specific branches. Give changed mathematical behaviour a new implementation/version identity. Tests should check propagation rules, gradients and no-leakage invariants.

`studies/uncertainty.json` is a historical zero-inclusive executable example with the one approved capped implementation. For conditional studies, copy its structure with the conditional reference result, new versions and matching conditional `--registry`. Add a separate experiment/version for each agreed setup; its `effects` mapping selects the implementation. The optional `reuse_results` references an already completed fit. The runner validates all IDs/effect selections and the benchmark population before fitting, reuses only matching completed version/setup results, fits missing setups sequentially, records per-setup success/failure, and preserves successful fits if another setup fails. It does not invent or automatically add unrequested variants.

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/study.py analysis/minutes/studies/uncertainty.json --output C:/Data/Basketball/analysis/uncertainty-study
```

The study saves its configuration and completion statuses and generates a report containing the selected setups plus the benchmark. The persistent registry/report is refreshed too. Mean and total predictive LL are selectable alongside errors, with training/validation overall, starter and bench scores and observation counts. Higher LL is better. Log scores are in nats using minutes as the observation unit; same-date observations of one player/role receive their joint Gaussian predictive score. Full coverage includes prior-only predictions and agrees with the training likelihood objective; matched coverage makes comparison with the fixed point benchmark fair. Point-only models have blank LL unless an explicit predictive distribution is supplied—no arbitrary residual variance is assigned to the benchmark. Saved experiment predictions/parameters are not changed when additional derived LL metrics are computed.

## Same-role continuity calibration subset

Add the previous-five-team-games calibration panel directly below the original plot in an existing static review:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/role_continuity.py --reference-results C:/Data/Basketball/analysis/minutes-five-plus-five-typical-v2-20261109/results.json --candidate-results C:/Data/Basketball/analysis/bayesian-drift-cap-typical-v2-20261109/results.json --review C:/Data/Basketball/analysis/typical-review-v2-20261109
```

This keeps the existing matched validation target and frozen predictions, then requires confirmed participation in every one of the current team's previous five regular-season games and the same starter/bench status as the target game. Team schedules come from the full participation ledger, so missed/absent roster rows and unknown roles are not skipped. Only strictly earlier dates count; histories can cross seasons. Prior appearances need not themselves pass the competitive/foul/typical-minute gates: this diagnoses actual role continuity, independently of model-history admission. Confirmed Played or positive original complete regulation minutes establish participation; unknown participation fails.

Both models use the identical subset and original one-minute calibration bins/axes. No refitting or extra target adjustment occurs. `last-five-same-role.json` retains counts, per-model bins and errors, input hashes and the definition; `last-five-same-role-membership.csv` retains every forecast's selection flag and five preceding team-game IDs. Rerunning replaces the same marked HTML panel without duplicating it.

## Fixed expected-minute bucket PDFs

`prediction_buckets.py` creates a standalone HTML with empirical PDFs of adjusted typical-minute labels for strict Bayesian forecast buckets **greater than 35** and **less than 26** minutes. Starter and bench player-games are pooled before normalisation. Two-minute histogram densities each integrate to one; no kernel smoothing or fitting is applied. All existing target exclusions remain in force.

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/prediction_buckets.py --results C:/Data/Basketball/analysis/bayesian-drift-cap-typical-v2-20261109/results.json --continuity-csv C:/Data/Basketball/analysis/typical-review-v2-20261109/last-five-same-role-membership.csv --output C:/Data/Basketball/analysis/prediction-bucket-pdfs-typical-v2-20261110
```

The default is full Bayesian validation coverage, including prior-only predictions. Controls select training/validation/combined data and full, benchmark-matched or matched five-game role-continuity populations. Training-containing views are labelled as including fitted training data. Both panels share axes within a view. `summary.json` retains every view's bin counts, density integrals, role composition, location statistics and source hashes. `samples.csv` retains player-game identities, predictions, original and adjusted minutes, split and diagnostic membership. PDF here means probability density function; the requested output is HTML.

## Calibration hover points and confidence intervals

Add or refresh hoverable data points and vertical confidence bars in both original and previous-five-same-role calibration panels of the existing review:

```powershell
C:/Data/Basketball/tools/minutes-venv/Scripts/python.exe analysis/minutes/calibration_review.py --reference-results C:/Data/Basketball/analysis/minutes-five-plus-five-typical-v2-20261109/results.json --candidate-results C:/Data/Basketball/analysis/bayesian-drift-cap-typical-v2-20261109/results.json --review C:/Data/Basketball/analysis/typical-review-v2-20261109
```

Each point retains its original one-minute prediction bin and game-weighted coordinates: x is mean prediction, y is mean adjusted label, n is player-games. Hover the point or its bar for these values, distinct player count and CI bounds. Series toggles let overlapping benchmark/Bayesian points be inspected separately.

Bars are **95% pointwise player-cluster bootstrap percentile confidence intervals for mean y**, using 2,000 resamples and a fixed seed. Players are sampled within a bucket with replacement, retaining each sampled player's summed adjusted minutes and game count. Thus repeated games are not treated as independent draws. Predictions, bin membership and fitted parameters remain fixed: these intervals do not include fitting uncertainty and are not individual-game prediction intervals. With only one represented player, the point remains visible and the tooltip states the CI is not estimable; few-player intervals require caution. `calibration-ci.json` retains all coordinates, counts, intervals, method and source hashes. The existing model results and training filters are unchanged; the previous-five condition remains a diagnostic subset only.

Run this command after regenerating the base/continuity review panels. The local full-review renderer also calls it last. Calibration acceptance and the next effect's specification are tracked in #20, not inferred from the diagnostic subset.
