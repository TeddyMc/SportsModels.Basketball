"""Fit role-conditioned historical-minute predictors; generate a local HTML report.

Inputs are selected canonical conversion outputs and their matching CSV diagnostics.
No provider JSON, future outcomes, validation fitting or basketball-context filters.
"""

from __future__ import annotations

import argparse
import csv
from collections import Counter, defaultdict, deque
from datetime import datetime
from hashlib import sha256
import json
from pathlib import Path
import re

import numpy as np
from scipy.optimize import minimize, minimize_scalar

TRAIN_SEASONS = {"2021-22", "2022-23", "2023-24", "2024-25"}
VALIDATION_SEASON = "2025-26"
MODELS = [
    ("exponential", "Exponential · last 10", 10),
    ("exponential_newest", "Exponential + newest weight", 10),
    ("five_plus_five", "Individual 5 + mean next 5", 10),
    ("three_plus_three", "Individual 3 + mean next 3", 6),
]
NUMERICAL_FLOOR = 1e-8  # Prevent an empty effective history in early short-history rows.


def read_csv(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def digest(path):
    return sha256(path.read_bytes()).hexdigest()


def load_observations(catalog_path):
    catalog_bytes = catalog_path.read_bytes()
    catalog = json.loads(catalog_bytes.decode("utf-8-sig"))
    observations, audit, sources = [], [], []
    selected = {s["Season"]: s for s in catalog["Seasons"]}
    expected = TRAIN_SEASONS | {VALIDATION_SEASON}
    if not expected <= selected.keys():
        raise ValueError(f"Missing required seasons: {expected - selected.keys()}")

    for season in sorted(expected):
        run = Path(selected[season]["RunDirectory"])
        manifest_path = run / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        if not manifest.get("Complete") or manifest.get("Failed"):
            raise ValueError(f"Incomplete/structurally failed selected run: {run}")
        dates = {}
        for item in manifest["Inputs"]:
            if item["Endpoint"] != "playbyplayv3":
                continue
            match = re.search(r"_(\d{8})_(\d{10})\.json$", item["OriginalPath"])
            if not match:
                raise ValueError(f"Missing game-date provenance: {item['OriginalPath']}")
            date, game = match.groups()
            date = datetime.strptime(date, "%Y%m%d").date().isoformat()
            if game in dates and dates[game] != date:
                raise ValueError(f"Conflicting game dates: {game}")
            dates[game] = date

        minutes_path = run / "player-minutes.csv"
        minute_rows = {}
        for row in read_csv(minutes_path):
            if row["scope"] != "Regulation" or not row["game"].startswith("002"):
                continue
            key = (row["game"], int(row["team"]), int(row["player"]))
            if key in minute_rows:
                raise ValueError(f"Duplicate regulation minute observation: {key}")
            minute_rows[key] = row

        games = [g for g in read_csv(run / "games.csv") if g["game"].startswith("002")]
        counts = Counter()
        for game_index, entry in enumerate(games):
            game_id = entry["game"]
            path = run / "games" / f"{entry['input']}.json"
            game = json.loads(path.read_text(encoding="utf-8-sig"))
            for side in ("HomeTeam", "AwayTeam"):
                team = game[side]
                for player in team["Players"]:
                    counts["RepresentedRows"] += 1
                    eligibility = player.get("Eligibility", "Unknown")
                    counts[f"Eligibility:{eligibility}"] += 1
                    minute = minute_rows.get((game_id, team["TeamId"], player["PlayerId"]))
                    quality = minute["availability"] if minute else "No regulation observation"
                    seconds = float(minute["seconds"]) if minute and minute["seconds"] else None
                    # Numeric totals with comparison warnings/unverified evidence are retained.
                    # Contradictory/unestablished records do not provide complete known totals.
                    if quality in {"Contradictory", "Unestablished", "Inactive"}:
                        seconds = None
                    if seconds is not None and (not np.isfinite(seconds) or not 0 <= seconds <= 2880):
                        raise ValueError(f"Invalid canonical regulation seconds: {game_id}, {player['PlayerId']}, {seconds}")
                    counts[f"Availability:{quality}"] += 1
                    row = {
                        "RegistryId": manifest["RegistryId"], "PlayerId": player["PlayerId"],
                        "Name": player["Name"], "GameId": game_id, "Date": dates[game_id],
                        "Season": season, "Split": "Training" if season in TRAIN_SEASONS else "Validation",
                        "Started": player.get("Started"), "Active": eligibility == "Active",
                        "RegulationSeconds": seconds,
                        "Quality": quality, "Source": str(path),
                        "Reasons": minute["reasons"] if minute else "Missing regulation observation",
                        "EvidenceKinds": minute["evidence_kinds"] if minute else "",
                    }
                    if eligibility == "Active":
                        observations.append(row)
                    else:
                        audit.append({**row, "Eligibility": eligibility})
            if (game_index + 1) % 500 == 0:
                print(f"{season}: read {game_index + 1}/{len(games)} regular-season games", flush=True)
        sources.append({"Season": season, "RunDirectory": str(run), "Games": len(games),
                        "ManifestSha256": digest(manifest_path), "MinutesCsvSha256": digest(minutes_path),
                        "Counts": dict(counts)})
        print(f"{season}: {len(games)} games; {dict(counts)}", flush=True)
    return observations, audit, sources, catalog_bytes


def build_history(observations):
    """Most recent matching-role games first. No same-day outcome may enter a prediction."""
    ordered = sorted(observations, key=lambda r: (r["Date"], r["GameId"], r["PlayerId"]))
    seen = set()
    history = defaultdict(lambda: deque(maxlen=10))
    rows, features = [], []
    by_date = defaultdict(list)
    for row in ordered:
        key = (row["RegistryId"], row["PlayerId"], row["GameId"])
        if key in seen:
            raise ValueError(f"Duplicate player-game: {key}")
        seen.add(key)
        by_date[row["Date"]].append(row)
    for day in by_date.values():
        for observation in day:
            role = observation["Started"]
            key = (observation["RegistryId"], observation["PlayerId"], role)
            prior = list(reversed(history[key])) if role is not None else []
            values = [p["RegulationSeconds"] / 60 for p in prior]
            features.append(values + [np.nan] * (10 - len(values)))
            rows.append({"Observation": observation, "HistoryCount": len(prior),
                         "LatestHistoryDate": prior[0]["Date"] if prior else None,
                         "HistoryGameIds": [p["GameId"] for p in prior],
                         "Status": "Unknown starting role" if role is None else
                                   "Available" if prior else "No earlier same-role history"})
        for observation in day:
            if observation["Started"] is not None and observation["RegulationSeconds"] is not None:
                key = (observation["RegistryId"], observation["PlayerId"], observation["Started"])
                history[key].append(observation)
    return rows, np.asarray(features, dtype=float)


def grouped_features(history, individual, block):
    tail = history[:, individual:individual + block]
    count = np.isfinite(tail).sum(axis=1)
    mean = np.divide(np.nansum(tail, axis=1), count, out=np.full(len(tail), np.nan), where=count > 0)
    return np.column_stack((history[:, :individual], mean))


def weights(kind, parameters):
    if kind == "exponential":
        raw = parameters[0] ** np.arange(10, dtype=float)
        return raw / raw.sum()
    if kind == "exponential_newest":
        decay, newest = parameters
        tail = decay ** np.arange(9, dtype=float)
        return np.r_[newest, (1 - newest) * tail / tail.sum()]
    return np.asarray(parameters)


def predict(features, coefficients):
    mask = np.isfinite(features)
    numerator = np.nan_to_num(features) @ coefficients
    denominator = mask @ coefficients
    return np.divide(numerator, denominator, out=np.full(len(features), np.nan), where=denominator > 0)


def fit(kind, features, actual, loss="mae"):
    if loss not in {"mae", "mse"}:
        raise ValueError(f"Unsupported loss: {loss}")

    def objective_value(predictions):
        errors = predictions - actual
        return float(np.mean(errors**2 if loss == "mse" else np.abs(errors)))

    def diagnostic_value(value):
        return {"Loss": loss, "TrainingObjective": value,
                "TrainingObjectiveMse" if loss == "mse" else "TrainingObjectiveMae": value}

    if not len(actual):
        raise ValueError(f"No eligible training rows for {kind}")
    if kind == "exponential":
        def decay_loss(decay):
            return objective_value(predict(features, weights(kind, [decay])))
        # A deterministic grid protects against relying on one nonsmooth local interval.
        grid = np.linspace(0, 1, 257)
        losses = [decay_loss(x) for x in grid]
        candidates = [(losses[i], float(x)) for i, x in enumerate(grid)]
        for i in np.argsort(losses)[:8]:
            lo, hi = grid[max(0, i-1)], grid[min(len(grid)-1, i+1)]
            result = minimize_scalar(decay_loss, bounds=(lo, hi), method="bounded", options={"xatol": 1e-10})
            candidates.append((float(result.fun), float(result.x)))
        value, parameter = min(candidates)
        return [parameter], {"Success": True, **diagnostic_value(value), "Method": "grid + bounded scalar search"}

    if kind == "exponential_newest":
        def objective(parameters):
            return objective_value(predict(features, weights(kind, parameters)))
        starts = [[d, q] for d in (.2, .6, 1.) for q in (.1, .4, .8)]
        bounds = [(0, 1), (NUMERICAL_FLOOR, 1)]
        constraints = ()
        jacobian = None
    else:
        clean, mask = np.nan_to_num(features), np.isfinite(features).astype(float)

        def objective(parameters):
            return objective_value(predict(features, parameters))

        def jacobian(parameters):
            numerator, denominator = clean @ parameters, mask @ parameters
            residual = numerator / denominator - actual
            gradient = (clean * denominator[:, None] - numerator[:, None] * mask) / denominator[:, None] ** 2
            multiplier = 2 * residual if loss == "mse" else np.sign(residual)
            return np.mean(multiplier[:, None] * gradient, axis=0)

        size = features.shape[1]
        rng = np.random.default_rng(20)
        starts = [np.full(size, 1/size), np.r_[.75, np.full(size-1, .25/(size-1))]]
        starts += list(rng.dirichlet(np.ones(size), size=4))
        bounds = [(NUMERICAL_FLOOR, 1)] * size
        constraints = ({"type": "eq", "fun": lambda p: p.sum()-1, "jac": lambda p: np.ones_like(p)},)

    candidates = []
    for initial in starts:
        result = minimize(objective, initial, jac=jacobian, method="SLSQP", bounds=bounds,
                          constraints=constraints, options={"maxiter": 500, "ftol": 1e-10})
        if result.success and np.isfinite(result.fun):
            candidates.append(result)
    if not candidates:
        raise RuntimeError(f"No converged training fit for {kind}")
    best = min(candidates, key=lambda r: r.fun)
    return best.x.tolist(), {"Success": True, **diagnostic_value(float(best.fun)),
                            "Method": "deterministic multistart SLSQP", "ConvergedStarts": len(candidates),
                            "AttemptedStarts": len(starts), "Iterations": int(best.nit)}


def metrics(rows, predictions, split=None):
    indices = [i for i, row in enumerate(rows) if split is None or row["Observation"]["Split"] == split]
    observed = [i for i in indices if rows[i]["Observation"]["RegulationSeconds"] is not None]
    evaluated = [i for i in observed if np.isfinite(predictions[i])]
    errors = np.asarray([rows[i]["Observation"]["RegulationSeconds"] / 60 - predictions[i] for i in evaluated])
    return {"Population": len(indices), "Observed": len(observed),
            "Predicted": sum(np.isfinite(predictions[i]) for i in indices).item() if indices else 0,
            "Evaluated": len(evaluated), "MaeMinutes": float(np.mean(np.abs(errors))) if len(errors) else None,
            "MseMinutesSquared": float(np.mean(errors**2)) if len(errors) else None,
            "RmseMinutes": float(np.sqrt(np.mean(errors**2))) if len(errors) else None,
            "MeanSignedErrorMinutes": float(errors.mean()) if len(errors) else None}


def train_models(rows, history, model_keys=None, loss="mae"):
    outcomes = np.array([np.nan if r["Observation"]["RegulationSeconds"] is None else r["Observation"]["RegulationSeconds"]/60 for r in rows])
    is_training = np.array([r["Observation"]["Split"] == "Training" for r in rows])
    results = []
    for kind, name, lookback in MODELS:
        if model_keys is not None and kind not in model_keys:
            continue
        features = grouped_features(history, 5, 5) if kind == "five_plus_five" else grouped_features(history, 3, 3) if kind == "three_plus_three" else history
        predictions = np.full(len(rows), np.nan)
        roles = {}
        for role, label in ((True, "Starter"), (False, "Bench")):
            role_mask = np.array([r["Observation"]["Started"] is role for r in rows])
            usable = role_mask & np.isfinite(features[:, 0])
            fit_mask = usable & is_training & np.isfinite(outcomes)
            print(f"Fitting {name} / {label}: {int(fit_mask.sum())} training rows", flush=True)
            parameters, diagnostics = fit(kind, features[fit_mask], outcomes[fit_mask], loss=loss)
            coefficients = weights(kind, parameters)
            predictions[usable] = predict(features[usable], coefficients)
            roles[label] = {"Parameters": parameters, "ComponentWeights": coefficients.tolist(),
                            "TrainingRows": int(fit_mask.sum()), "Fit": diagnostics}
            value = diagnostics.get("TrainingObjective", diagnostics.get("TrainingObjectiveMae"))
            print(f"  fitted {parameters}; training {loss.upper()} {value:.6f}", flush=True)
        results.append({"Key": kind, "Name": name, "Lookback": lookback, "Loss": loss, "Roles": roles,
                        "Training": metrics(rows, predictions, "Training"),
                        "Validation": metrics(rows, predictions, "Validation"),
                        "PredictionsMinutes": [float(p) if np.isfinite(p) else None for p in predictions]})
    if not results:
        raise ValueError("No supported model selected")
    cohorts = [np.array([p is not None for p in model["PredictionsMinutes"]]) for model in results]
    if any(not np.array_equal(cohorts[0], cohort) for cohort in cohorts[1:]):
        raise RuntimeError("Models must be compared on the same prediction-availability cohort")
    return results


def write_report(output, payload):
    template = Path(__file__).with_name("report_template.html").read_text(encoding="utf-8")
    data = json.dumps(payload, ensure_ascii=True, allow_nan=False, separators=(",", ":")).replace("<", "\\u003c").replace(">", "\\u003e").replace("&", "\\u0026")
    (output / "index.html").write_text(template.replace("__DATA__", data), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--catalog", type=Path, default=Path("C:/Data/Basketball/converted/catalog.json"))
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--render-only", action="store_true", help="Refresh HTML from this output's saved results.json")
    parser.add_argument("--loss", choices=("mae", "mse"), default="mae")
    parser.add_argument("--models", nargs="+", choices=[m[0] for m in MODELS], default=None)
    parser.add_argument("--version", help="Immutable tracking ID; defaults to output directory name")
    parser.add_argument("--registry", type=Path, default=Path("C:/Data/Basketball/analysis/model-tracking/registry.json"))
    parser.add_argument("--benchmark", action="store_true", help="Designate this single model as the fixed benchmark")
    args = parser.parse_args()
    if args.render_only:
        payload = json.loads((args.output / "results.json").read_text(encoding="utf-8"))
        write_report(args.output, payload)
        return
    if args.output.exists():
        raise FileExistsError("Use a new output directory for a new fit")
    observations, audit, sources, catalog_bytes = load_observations(args.catalog)
    rows, history = build_history(observations)
    models = train_models(rows, history, model_keys=args.models, loss=args.loss)
    summary = {"TrainingSeasons": sorted(TRAIN_SEASONS), "ValidationSeason": VALIDATION_SEASON,
               "Loss": args.loss,
               "Objective": "Training mean squared error in minutes squared" if args.loss == "mse" else "Training MAE in minutes; no likelihood/distribution fitted",
               "Games": sum(s["Games"] for s in sources), "Sources": sources,
               "Catalog": str(args.catalog.resolve()), "CatalogSha256": sha256(catalog_bytes).hexdigest(),
               "ExcludedRows": len(audit), "ExcludedEligibility": dict(Counter(r["Eligibility"] for r in audit)),
               "NumericWeightFloor": NUMERICAL_FLOOR,
               "Policy": "Regular-season confirmed-active observations. Same-role earlier-date history only; history crosses seasons. Parameters fitted separately per role on 2021–25 only, frozen throughout 2025–26. Earlier observed validation outcomes may enter later validation histories. Short histories use available components renormalized; partial tail blocks average their available observations. Comparison warnings and plausible uncorroborated numeric totals retained; genuinely unknown totals remain null. Archived active/starter status supplied as a retrospective pre-game scenario; publication timing is not established. Multistart convergence is reported, not a certificate of global optimality."}
    payload = {"Rows": rows, "Models": models, "Summary": summary}
    args.output.mkdir(parents=True)
    (args.output / "catalog.json").write_bytes(catalog_bytes)
    (args.output / "results.json").write_text(json.dumps(payload, allow_nan=False, separators=(",", ":")), encoding="utf-8")
    (args.output / "population-audit.json").write_text(json.dumps(audit, separators=(",", ":")), encoding="utf-8")
    with (args.output / "comparison.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.writer(stream)
        writer.writerow(["Model", "Loss", "Training N", "Training MSE min2", "Training RMSE minutes", "Training MAE minutes",
                         "Validation N", "Validation MSE min2", "Validation RMSE minutes", "Validation MAE minutes"])
        for model in models:
            writer.writerow([model["Name"], args.loss, model["Training"]["Evaluated"], model["Training"]["MseMinutesSquared"],
                             model["Training"]["RmseMinutes"], model["Training"]["MaeMinutes"],
                             model["Validation"]["Evaluated"], model["Validation"]["MseMinutesSquared"],
                             model["Validation"]["RmseMinutes"], model["Validation"]["MaeMinutes"]])
    write_report(args.output, payload)
    from registry import update_from_results
    update_from_results(args.registry, args.output / "results.json", args.version or args.output.name, args.benchmark)
    print((args.output / "comparison.csv").read_text(encoding="utf-8"), flush=True)
    print(f"Open {args.output / 'index.html'}", flush=True)


if __name__ == "__main__":
    main()
