"""Shared Gaussian player-role filtering/fitting with interchangeable dynamics.

Training uses sequential predictive likelihood, never validation errors.
"""

import argparse
from collections import defaultdict
from datetime import date
import json
from pathlib import Path
from hashlib import sha256

import numpy as np
from scipy.optimize import minimize

from train import metrics, write_report
from effects import get_dynamics
from likelihoods import gaussian_log_scores

BASE_BOUNDS = [(0., 48.), (-12., 12.), (-12., 12.)]


def unpack(parameters, dynamics=None):
    dynamics = dynamics or get_dynamics("calendar_drift_fitted_cap")
    initial, noise = np.exp(parameters[1:3])
    return float(parameters[0]), float(initial), float(noise), dynamics.decode(parameters[3:], initial)


def prepare_training(rows, role):
    sequences = defaultdict(lambda: defaultdict(list))
    for row in rows:
        o = row["Observation"]
        if o["Split"] == "Training" and o["Started"] is role and o["RegulationSeconds"] is not None:
            sequences[(o["RegistryId"], o["PlayerId"])][date.fromisoformat(o["Date"]).toordinal()].append(o["RegulationSeconds"]/60)
    if not sequences:
        raise ValueError("No observed training data for role")
    length = max(len(days) for days in sequences.values())
    means = np.zeros((length, len(sequences)))
    counts, deltas, within = np.zeros_like(means), np.zeros_like(means), np.zeros_like(means)
    for column, days in enumerate(sequences.values()):
        previous = None
        for step, (day, values) in enumerate(sorted(days.items())):
            values = np.asarray(values)
            means[step, column] = values.mean()
            counts[step, column] = len(values)
            within[step, column] = np.sum((values-values.mean())**2)
            deltas[step, column] = 0 if previous is None else day-previous
            previous = day
    return means, counts, deltas, within


def likelihood(parameters, prepared, dynamics=None):
    """Mean NLL and analytic gradient, batching same-day observations jointly."""
    dynamics = dynamics or get_dynamics("calendar_drift_fitted_cap")
    mu, initial, noise, _ = unpack(parameters, dynamics)
    ys, counts, deltas, within = prepared
    mean, variance = np.full(ys.shape[1], mu), np.full(ys.shape[1], initial)
    dimension = len(parameters)
    dmean, dvariance = np.zeros((ys.shape[1], dimension)), np.zeros((ys.shape[1], dimension))
    dmean[:, 0], dvariance[:, 1] = 1., initial
    dnoise = np.zeros(dimension)
    dnoise[2] = noise
    value, gradient = 0., np.zeros(dimension)
    for step in range(len(ys)):
        active = np.flatnonzero(counts[step] > 0)
        if not len(active):
            continue
        n, gap, target = counts[step, active], deltas[step, active], ys[step, active]
        prior, dv = dynamics.propagate_gradient(variance[active], dvariance[active], gap, initial, parameters[3:], 3)
        residual = target-mean[active]
        denominator = prior+noise/n
        dd = dv+dnoise/n[:, None]
        dm = dmean[active]
        value += .5*np.sum(np.log(denominator)+np.log(n)+(n-1)*np.log(noise)
                             + residual**2/denominator + within[step, active]/noise + n*np.log(2*np.pi))
        gradient += .5*np.sum((1/denominator-residual**2/denominator**2)[:, None]*dd
                              - (2*residual/denominator)[:, None]*dm
                              + ((n-1)/noise-within[step, active]/noise**2)[:, None]*dnoise, axis=0)
        gain = prior/denominator
        dg = (dv*denominator[:, None]-prior[:, None]*dd)/denominator[:, None]**2
        mean[active] += gain*residual
        dmean[active] = (1-gain)[:, None]*dm+residual[:, None]*dg
        variance[active] = prior*(1-gain)
        dvariance[active] = (1-gain)[:, None]*dv-prior[:, None]*dg
    total = counts.sum()
    return float(value/total), gradient/total


def fit_role(prepared, label, dynamics=None):
    dynamics = dynamics or get_dynamics("calendar_drift_fitted_cap")
    ys, counts, _, _ = prepared
    observed_mean = float(np.sum(ys*counts)/counts.sum())
    cold_mean = float(np.mean(ys[0, counts[0] > 0]))
    starts = [(observed_mean, 25., 30.), (cold_mean, 50., 40.), (cold_mean, 10., 25.),
              (observed_mean, 80., 50.), (cold_mean, 40., 60.), (observed_mean, 20., 15.)]
    bounds = BASE_BOUNDS+list(dynamics.bounds)
    candidates = []
    for i, initial in enumerate(starts):
        raw = np.r_[initial[0], np.log(initial[1:]), dynamics.starting_raw(i)]
        result = minimize(likelihood, raw, args=(prepared, dynamics), jac=True, method="L-BFGS-B", bounds=bounds,
                          options={"maxiter": 250, "ftol": 1e-10, "gtol": 1e-6})
        print(f"{label}: start {i+1}/{len(starts)}, converged={result.success}, mean NLL={result.fun:.6f}", flush=True)
        if result.success and np.isfinite(result.fun):
            candidates.append(result)
    if not candidates:
        raise RuntimeError(f"No converged Bayesian fit: {label}")
    best = min(candidates, key=lambda item: item.fun)
    mean, initial, noise, effect_parameters = unpack(best.x, dynamics)
    parameters = {"PriorMeanMinutes": mean, "PriorVarianceMinutesSquared": initial,
                  "ObservationVarianceMinutesSquared": noise, **effect_parameters,
                  "DynamicsImplementation": dynamics.key}
    parameters["Fit"] = {"Success": True, "Objective": "Training Gaussian sequential predictive NLL",
                         "MeanNll": float(best.fun), "TrainingRows": int(counts.sum()),
                         "ConvergedStarts": len(candidates), "AttemptedStarts": len(starts),
                         "Iterations": int(best.nit), "RawParameters": best.x.tolist(),
                         "RawParameterNames": ["PriorMeanMinutes", "LogPriorVariance", "LogObservationVariance", *dynamics.raw_names],
                         "NumericalBoundaryIndices": [i for i, (x, (lo, hi)) in enumerate(zip(best.x, bounds)) if min(x-lo, hi-x) < 1e-4]}
    return parameters


def filter_rows(rows, roles, dynamics=None):
    """Predict an entire date before updating any player-role state from that date."""
    dynamics = dynamics or get_dynamics("calendar_drift_fitted_cap")
    size = len(rows)
    predictions, latent, predictive, posterior_means, posterior_variances = [np.full(size, np.nan) for _ in range(5)]
    history_counts, latest_dates = [0]*size, [None]*size
    states = {}
    days = defaultdict(list)
    for i, row in enumerate(rows):
        days[row["Observation"]["Date"]].append(i)
    for day_text, indices in sorted(days.items()):
        day = date.fromisoformat(day_text).toordinal()
        pending = defaultdict(list)
        for i in indices:
            o = rows[i]["Observation"]
            if o["Started"] is None:
                continue
            label = "Starter" if o["Started"] else "Bench"
            p = roles[label]
            key = (o["RegistryId"], o["PlayerId"], o["Started"])
            mu, variance, last_day, count = states.get(key, (p["PriorMeanMinutes"], p["PriorVarianceMinutesSquared"], None, 0))
            gap = 0 if last_day is None else day-last_day
            v = float(dynamics.propagate(variance, gap, p))
            predictions[i], latent[i], predictive[i] = mu, v, v+p["ObservationVarianceMinutesSquared"]
            history_counts[i] = count
            latest_dates[i] = None if last_day is None else date.fromordinal(last_day).isoformat()
            pending[key].append(i)
        for key, members in pending.items():
            label = "Starter" if key[2] else "Bench"
            p, i = roles[label], members[0]
            actual = [rows[j]["Observation"]["RegulationSeconds"]/60 for j in members if rows[j]["Observation"]["RegulationSeconds"] is not None]
            mu, v = predictions[i], latent[i]
            if actual:
                gain = v/(v+p["ObservationVarianceMinutesSquared"]/len(actual))
                mu += gain*(float(np.mean(actual))-mu)
                v *= 1-gain
                states[key] = (mu, v, day, history_counts[i]+len(actual))
            for j in members:
                posterior_means[j], posterior_variances[j] = mu, v
    serial = lambda values: [float(x) if np.isfinite(x) else None for x in values]
    return {"PredictionsMinutes": serial(predictions), "LatentVarianceMinutesSquared": serial(latent),
            "PredictiveVarianceMinutesSquared": serial(predictive), "PosteriorMeansMinutes": serial(posterior_means),
            "PosteriorVariancesMinutesSquared": serial(posterior_variances),
            "HistoryCounts": history_counts, "LatestHistoryDates": latest_dates}


def run_experiment(reference_results, output, version, dynamics_key="calendar_drift_fitted_cap", name=None, registry=None):
    dynamics = get_dynamics(dynamics_key)
    reference_results, output = Path(reference_results), Path(output)
    if output.exists():
        raise FileExistsError("Use a new Bayesian output directory")
    reference_bytes = reference_results.read_bytes()
    reference = json.loads(reference_bytes)
    setup = {"Version": version, "Effects": {"uncertainty_dynamics": dynamics.key},
             "ReferenceResultsSha256": sha256(reference_bytes).hexdigest(),
             "ImplementationSha256": {file: sha256(Path(__file__).with_name(file).read_bytes()).hexdigest()
                                      for file in ("bayesian.py", "effects.py", "likelihoods.py")},
             "FitObjective": "Training Gaussian sequential predictive NLL", "FitBy": "Starting role"}
    rows = reference["Rows"]
    roles = {}
    for role, label in ((True, "Starter"), (False, "Bench")):
        prepared = prepare_training(rows, role)
        print(f"Fitting {label}: {int(prepared[1].sum())} observed training player-games; dynamics={dynamics.key}", flush=True)
        roles[label] = fit_role(prepared, label, dynamics)
        print(label, roles[label], flush=True)
    outputs = filter_rows(rows, roles, dynamics)
    predictions = np.array([np.nan if p is None else p for p in outputs["PredictionsMinutes"]])
    scores = {split: {**metrics(rows, predictions, split), **gaussian_log_scores(rows, outputs["PredictionsMinutes"],
              outputs["PredictiveVarianceMinutesSquared"], outputs["LatentVarianceMinutesSquared"], split)} for split in ("Training", "Validation")}
    model = {"Key": "bayesian_dynamic", "Name": name or "Bayesian drift + fitted cap", "Version": version,
             "Effects": {"uncertainty_dynamics": dynamics.key}, "Setup": setup, "PredictiveDistribution": "gaussian_shared_latent",
             "Lookback": 0, "Loss": "gaussian_nll", "Roles": roles, **outputs, **scores}
    summary = {**reference["Summary"], "Loss": "mse", "FitObjective": "gaussian_nll",
               "Objective": "Training Gaussian predictive NLL; LL and MSE/RMSE/MAE evaluation",
               "ReferenceResults": str(reference_results.resolve()),
               "Effects": model["Effects"],
               "Policy": "Independent player-role Gaussian latent means. Prior, observation noise and uncertainty-dynamics parameters fitted by training likelihood separately per role. Entire date predicted before same-day updates. Unknown actuals do not update states; validation hyperparameters remain frozen. Posterior state updates use earlier observed validation outcomes. LL is joint for same-date observations of the same player/role, in nats using minutes; higher is better. Gaussian predictive distributions have unbounded support; uncertainty is not guaranteed calibrated."}
    payload = {"Rows": rows, "Models": [model], "Summary": summary,
               "Benchmark": {"Name": "Fixed 5 + 5 benchmark", "Version": reference["Models"][0].get("Version", "benchmark-v1"), "PredictionsMinutes": reference["Models"][0]["PredictionsMinutes"]}}
    output.mkdir(parents=True)
    (output / "setup.json").write_text(json.dumps(setup, indent=2), encoding="utf-8")
    (output / "results.json").write_text(json.dumps(payload, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    write_report(output, payload)
    if registry:
        from registry import update_from_results
        update_from_results(registry, output / "results.json", version)
    print(json.dumps({"TrainingAllAvailable": model["Training"], "ValidationAllAvailable": model["Validation"]}), flush=True)
    print(f"Open {output / 'index.html'}", flush=True)
    return payload


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference-results", type=Path, default=Path("C:/Data/Basketball/analysis/minutes-five-plus-five-mse-20261105/results.json"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", default="bayes-drift-cap-v1")
    parser.add_argument("--registry", type=Path, default=Path("C:/Data/Basketball/analysis/model-tracking/registry.json"))
    parser.add_argument("--dynamics", default="calendar_drift_fitted_cap")
    parser.add_argument("--name")
    args = parser.parse_args()
    run_experiment(args.reference_results, args.output, args.version, args.dynamics, args.name, args.registry)


if __name__ == "__main__":
    main()
