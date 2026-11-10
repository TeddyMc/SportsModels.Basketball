"""Persistent model-version scores and matched benchmark comparison reports."""

import argparse
from datetime import datetime, timezone
from hashlib import sha256
import json
from pathlib import Path

import numpy as np

from train import metrics, write_report
from likelihoods import gaussian_log_scores


def identity(row):
    o = row["Observation"]
    return o["RegistryId"], o["PlayerId"], o["GameId"]


def cohort_hash(rows):
    values = sorted((identity(row), row["Observation"]["Date"], row["Observation"]["Started"],
                     row["Observation"]["RegulationSeconds"], row["Observation"]["Split"]) for row in rows)
    return sha256(json.dumps(values, separators=(",", ":")).encode()).hexdigest()


def score_groups(rows, predictions, predictive_variances=None, latent_variances=None):
    scores = {}
    for split in ("Training", "Validation"):
        scores[split] = {}
        for group, role in (("Overall", None), ("Starter", True), ("Bench", False)):
            indices = [i for i, row in enumerate(rows) if group == "Overall" or row["Observation"]["Started"] is role]
            scores[split][group] = metrics([rows[i] for i in indices], np.asarray([predictions[i] for i in indices], dtype=float), split)
            scores[split][group].update(gaussian_log_scores([rows[i] for i in indices], [predictions[i] for i in indices],
                [predictive_variances[i] for i in indices], [latent_variances[i] for i in indices] if latent_variances is not None else None, split)
                if predictive_variances is not None else {"LogLikelihoodSum": None, "MeanLogLikelihood": None, "LogLikelihoodN": 0})
    return scores


def align_to(reference_rows, candidate_rows, predictions):
    lookup = {identity(row): (row, value) for row, value in zip(candidate_rows, predictions)}
    if len(lookup) != len(candidate_rows) or len(predictions) != len(candidate_rows):
        raise ValueError("Duplicate identities or prediction length mismatch")
    aligned = []
    for row in reference_rows:
        if identity(row) not in lookup:
            raise ValueError("Candidate is missing a benchmark population row")
        other, value = lookup[identity(row)]
        for field in ("Date", "Started", "RegulationSeconds", "Split"):
            if other["Observation"][field] != row["Observation"][field]:
                raise ValueError(f"Changed cohort/target field: {field}; rerun the benchmark on the new dataset")
        aligned.append(value)
    if len(candidate_rows) != len(reference_rows):
        raise ValueError("Population changed; use a separately evaluated benchmark for this dataset")
    return aligned


def snapshot(payload, key, version, results_path, label=None):
    model = next(m for m in payload["Models"] if m["Key"] == key)
    values = model["PredictionsMinutes"]
    fingerprint = sha256(json.dumps({"Model": model, "Cohort": cohort_hash(payload["Rows"])}, sort_keys=True).encode()).hexdigest()
    return {"Version": version, "Label": label or model["Name"], "ModelKey": key,
            "Loss": model.get("Loss", payload["Summary"].get("Loss", "mae")),
            "ResultsPath": str(Path(results_path).resolve()), "Fingerprint": fingerprint,
            "CohortHash": cohort_hash(payload["Rows"]), "CatalogSha256": payload["Summary"]["CatalogSha256"],
            "RegisteredAtUtc": datetime.now(timezone.utc).isoformat(),
            "AllAvailable": score_groups(payload["Rows"], values, model.get("PredictiveVarianceMinutesSquared"), model.get("LatentVarianceMinutesSquared")),
            "Parameters": model["Roles"], "Effects": model.get("Effects", {"uncertainty_dynamics": "calendar_drift_fitted_cap"} if model["Key"] == "bayesian_drift_cap" else {})}


def register(registry, entry, benchmark=False):
    if benchmark and registry.get("Benchmark") not in (None, entry["Version"]):
        raise ValueError("Benchmark is frozen; do not silently replace it")
    existing = next((e for e in registry["Versions"] if e["Version"] == entry["Version"]), None)
    if existing and existing["Fingerprint"] != entry["Fingerprint"]:
        raise ValueError("Version IDs are immutable; register a new version instead of replacing scores")
    if not existing:
        registry["Versions"].append(entry)
    if benchmark:
        registry["Benchmark"] = entry["Version"]


def generate(registry, output, versions=None, study_statuses=None):
    registry["ScoringVersion"] = "point-errors-and-joint-gaussian-ll-v1"
    if not registry.get("Benchmark"):
        raise ValueError("Register a benchmark before generating comparisons")
    benchmark_entry = next(e for e in registry["Versions"] if e["Version"] == registry["Benchmark"])
    benchmark_payload = json.loads(Path(benchmark_entry["ResultsPath"]).read_text(encoding="utf-8"))
    baseline = next(m for m in benchmark_payload["Models"] if m["Key"] == benchmark_entry["ModelKey"])
    reference = benchmark_payload["Rows"]
    models = []
    entries = [e for e in registry["Versions"] if versions is None or e["Version"] in versions or e["Version"] == registry["Benchmark"]]
    for entry in sorted(entries, key=lambda e: e["Version"] != registry["Benchmark"]):
        payload = json.loads(Path(entry["ResultsPath"]).read_text(encoding="utf-8"))
        current = snapshot(payload, entry["ModelKey"], entry["Version"], entry["ResultsPath"])
        if current["Fingerprint"] != entry["Fingerprint"]:
            raise ValueError(f"Saved experiment changed: {entry['Version']}")
        model = next(m for m in payload["Models"] if m["Key"] == entry["ModelKey"])
        aligned = align_to(reference, payload["Rows"], model["PredictionsMinutes"])
        row_arrays = {field: align_to(reference, payload["Rows"], model[field]) for field in
                      ("HistoryCounts", "LatestHistoryDates", "LatentVarianceMinutesSquared", "PredictiveVarianceMinutesSquared", "PosteriorMeansMinutes", "PosteriorVariancesMinutesSquared") if field in model}
        # Compare only where both models can predict; retain the full-coverage scores separately.
        common = [v is not None and b is not None for v, b in zip(aligned, baseline["PredictionsMinutes"])]
        paired = [v if available else None for v, available in zip(aligned, common)]
        paired_baseline = [v if available else None for v, available in zip(baseline["PredictionsMinutes"], common)]
        entry["AllAvailable"] = current["AllAvailable"]
        entry["Effects"] = current["Effects"]
        entry["Matched"] = score_groups(reference, paired, row_arrays.get("PredictiveVarianceMinutesSquared"), row_arrays.get("LatentVarianceMinutesSquared"))
        entry["MatchedBenchmark"] = score_groups(reference, paired_baseline)
        model = {**model, **row_arrays, "Effects": entry["Effects"], "Key": entry["Version"], "ModelKind": entry["ModelKey"],
                 "Version": entry["Version"], "Name": entry["Label"], "FitObjective": entry["Loss"],
                 "GroupedScores": entry["Matched"], "FullCoverageScores": entry["AllAvailable"],
                 "Training": entry["Matched"]["Training"]["Overall"],
                 "Validation": entry["Matched"]["Validation"]["Overall"],
                 "PredictionsMinutes": aligned}
        models.append(model)
    summary = {**benchmark_payload["Summary"], "VersionRegistry": True, "Loss": "mse",
               "ScoringVersion": registry["ScoringVersion"],
               "BenchmarkVersion": registry["Benchmark"],
               "ComparisonPolicy": "Table uses player-games where candidate and fixed benchmark both predict. Full-coverage scores are retained separately. Individual timelines show all available predictions. Fit objectives may differ; displayed errors have the same definitions."}
    if study_statuses is not None:
        summary["StudyStatuses"] = study_statuses
    output.mkdir(parents=True, exist_ok=True)
    write_report(output, {"Rows": reference, "Models": models, "Summary": summary,
                          "Benchmark": {"Name": benchmark_entry["Label"], "Version": registry["Benchmark"],
                                        "PredictionsMinutes": baseline["PredictionsMinutes"]}})
    import csv
    with (output / "model-versions.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["Version", "Model", "Fit objective", "Split", "Role", "Coverage", "Scored N", "MSE min2", "RMSE min", "MAE min", "LL sum nats", "Mean LL nats", "LL N", "Benchmark MSE min2", "Catalog hash"])
        for entry in entries:
            for coverage, scores in (("AllAvailable", entry["AllAvailable"]), ("Matched", entry["Matched"])):
                for split, groups in scores.items():
                    for role, score in groups.items():
                        writer.writerow([entry["Version"], entry["Label"], entry["Loss"], split, role, coverage,
                                         score["Evaluated"], score["MseMinutesSquared"], score["RmseMinutes"], score["MaeMinutes"],
                                         score["LogLikelihoodSum"], score["MeanLogLikelihood"], score["LogLikelihoodN"],
                                         entry["MatchedBenchmark"][split][role]["MseMinutesSquared"] if coverage == "Matched" else "", entry["CatalogSha256"]])


def update_from_results(registry_path, results_path, version_prefix, benchmark=False, output=None):
    registry_path, results_path = Path(registry_path), Path(results_path)
    registry = json.loads(registry_path.read_text(encoding="utf-8")) if registry_path.exists() else {"SchemaVersion": 1, "Benchmark": None, "Versions": []}
    payload = json.loads(results_path.read_text(encoding="utf-8"))
    if benchmark and len(payload["Models"]) != 1:
        raise ValueError("Select exactly one model to designate a benchmark")
    for model in payload["Models"]:
        version = version_prefix if len(payload["Models"]) == 1 else f"{version_prefix}:{model['Key']}"
        register(registry, snapshot(payload, model["Key"], version, results_path), benchmark)
    if registry.get("Benchmark"):
        generate(registry, Path(output) if output else registry_path.parent)
    registry_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = registry_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(registry, indent=2), encoding="utf-8")
    temporary.replace(registry_path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--registry", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--results", type=Path)
    parser.add_argument("--model")
    parser.add_argument("--version")
    parser.add_argument("--label")
    parser.add_argument("--benchmark", action="store_true")
    args = parser.parse_args()
    registry = json.loads(args.registry.read_text(encoding="utf-8")) if args.registry.exists() else {"SchemaVersion": 1, "Benchmark": None, "Versions": []}
    if args.results:
        if not args.model or not args.version:
            parser.error("--results requires --model and --version")
        payload = json.loads(args.results.read_text(encoding="utf-8"))
        register(registry, snapshot(payload, args.model, args.version, args.results, args.label), args.benchmark)
    generate(registry, args.output)
    args.registry.parent.mkdir(parents=True, exist_ok=True)
    temporary = args.registry.with_suffix(".tmp")
    temporary.write_text(json.dumps(registry, indent=2), encoding="utf-8")
    temporary.replace(args.registry)
    print(f"Updated {args.registry}; report {args.output / 'index.html'}")


if __name__ == "__main__":
    main()
