"""Run configured effect variants through the same fitter, evaluator and ledger."""

import argparse
import json
from pathlib import Path
import re

from bayesian import run_experiment
from effects import get_dynamics
from registry import align_to, generate, register, snapshot


def validate(config):
    if config.get("schema_version") != 1 or set(config)-{"schema_version", "reference_results", "experiments"}:
        raise ValueError("Unsupported study schema or unknown top-level configuration fields")
    seen = set()
    for experiment in config["experiments"]:
        if set(experiment)-{"version", "name", "effects", "reuse_results"}:
            raise ValueError("Unknown experiment configuration field")
        version = experiment["version"]
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]*", version) or version in seen:
            raise ValueError(f"Duplicate or unsafe experiment version: {version}")
        seen.add(version)
        effects = experiment["effects"]
        if set(effects) != {"uncertainty_dynamics"}:
            raise ValueError("This study runner currently supports the uncertainty_dynamics effect slot")
        get_dynamics(effects["uncertainty_dynamics"])
    if not seen:
        raise ValueError("No experiments configured")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("config", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--registry", type=Path, default=Path("C:/Data/Basketball/analysis/model-tracking/registry.json"))
    args = parser.parse_args()
    config = json.loads(args.config.read_text(encoding="utf-8"))
    validate(config)  # Reject unknown strategies/duplicate IDs before doing any fitting.
    reference_path = Path(config["reference_results"])
    reference = json.loads(reference_path.read_text(encoding="utf-8"))
    registry = json.loads(args.registry.read_text(encoding="utf-8"))
    benchmark_entry = next(e for e in registry["Versions"] if e["Version"] == registry["Benchmark"])
    benchmark_payload = json.loads(Path(benchmark_entry["ResultsPath"]).read_text(encoding="utf-8"))
    align_to(benchmark_payload["Rows"], reference["Rows"], reference["Models"][0]["PredictionsMinutes"])
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "study-config.json").write_text(json.dumps(config, indent=2), encoding="utf-8")
    statuses = []
    for experiment in config["experiments"]:
        version = experiment["version"]
        try:
            target = args.output / version
            results = Path(experiment["reuse_results"]) if experiment.get("reuse_results") else target / "results.json"
            if results.exists():
                payload = json.loads(results.read_text(encoding="utf-8"))
                if len(payload["Models"]) != 1:
                    raise ValueError("A study setup must identify exactly one Bayesian model")
                model = payload["Models"][0]
                declared = model.get("Effects")
                if declared is None and model["Key"] == "bayesian_drift_cap":
                    declared = {"uncertainty_dynamics": "calendar_drift_fitted_cap"}
                if declared != experiment["effects"] or model.get("Version") != version:
                    raise ValueError("Existing results do not match the declared version/effect setup")
                align_to(reference["Rows"], payload["Rows"], model["PredictionsMinutes"])
                print(f"Reusing verified completed fit: {version}", flush=True)
                state = "Reused"
            else:
                if experiment.get("reuse_results"):
                    raise FileNotFoundError(results)
                payload = run_experiment(reference_path, target, version, experiment["effects"]["uncertainty_dynamics"], experiment.get("name"))
                results = target / "results.json"
                model, state = payload["Models"][0], "Fitted"
            register(registry, snapshot(payload, model["Key"], version, results, experiment.get("name")))
            temporary = args.registry.with_suffix(".tmp")
            temporary.write_text(json.dumps(registry, indent=2), encoding="utf-8")
            temporary.replace(args.registry)
            statuses.append({"Version": version, "Status": state, "Results": str(results)})
        except (ValueError, RuntimeError, OSError) as error:
            statuses.append({"Version": version, "Status": "Failed", "Error": str(error)})
            print(f"Failed {version}: {error}", flush=True)
        (args.output / "study-status.json").write_text(json.dumps(statuses, indent=2), encoding="utf-8")
    completed = [s["Version"] for s in statuses if s["Status"] != "Failed"]
    if completed:
        generate(registry, args.output, versions=completed, study_statuses=statuses)
        # Refresh the persistent report as well, preserving all past versions.
        generate(registry, args.registry.parent)
        args.registry.write_text(json.dumps(registry, indent=2), encoding="utf-8")
    else:
        from html import escape
        (args.output / "index.html").write_text("<!doctype html><meta charset='utf-8'><h1>No completed setups</h1><pre>"+escape(json.dumps(statuses, indent=2))+"</pre>", encoding="utf-8")
    print(json.dumps(statuses, indent=2), flush=True)
    print(f"Study report: {args.output / 'index.html'}", flush=True)
    return int(any(s["Status"] == "Failed" for s in statuses))


if __name__ == "__main__":
    raise SystemExit(main())
