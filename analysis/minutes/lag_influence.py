"""Replay exact Bayesian mean coefficients to visualise memory by same-role lag."""

import argparse
from collections import defaultdict
import csv
from hashlib import sha256
import io
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import PercentFormatter
import numpy as np


def extract(payload, max_lag=200):
    model = payload["Models"][0]
    if not payload["Summary"].get("ConditionalOnPlaying"):
        raise ValueError("This diagnostic expects the conditional-on-playing model")
    states, days, records = {}, defaultdict(list), []
    for i, row in enumerate(payload["Rows"]):
        days[row["Observation"]["Date"]].append(i)
    max_error = 0.
    for _, indices in sorted(days.items()):
        pending = defaultdict(list)
        for i in indices:
            o = payload["Rows"][i]["Observation"]
            if o["Started"] is None:
                continue
            label = "Starter" if o["Started"] else "Bench"
            key = (o["RegistryId"], o["PlayerId"], o["Started"])
            prior, weights, outcomes = states.get(key, (1., np.empty(0), np.empty(0)))
            reconstructed = prior*model["Roles"][label]["PriorMeanMinutes"] + float(weights @ outcomes)
            max_error = max(max_error, abs(reconstructed-model["PredictionsMinutes"][i]))
            if not np.isclose(prior+weights.sum(), 1., atol=1e-10):
                raise AssertionError("Mean coefficients do not sum to one")
            if o["Split"] == "Validation" and o["RegulationSeconds"] is not None and payload["Benchmark"]["PredictionsMinutes"][i] is not None:
                recent = np.zeros(max_lag)
                recent[:min(max_lag, len(weights))] = weights[::-1][:max_lag]
                records.append({"Role": label, "Player": o["Name"], "GameId": o["GameId"],
                                "Date": o["Date"], "HistoryCount": len(weights), "PriorWeight": prior,
                                "OlderWeight": float(weights[:-max_lag].sum()), "Weights": recent,
                                "EqualWeightGames": float(weights.sum()**2/(weights @ weights))})
            pending[key].append(i)
        for key, members in pending.items():
            observed = [payload["Rows"][j]["Observation"]["RegulationSeconds"]/60 for j in members
                        if payload["Rows"][j]["Observation"]["RegulationSeconds"] is not None]
            if not observed:
                continue
            if min(observed) <= 0:
                raise AssertionError("A zero-minute observation reached the conditional filter")
            label = "Starter" if key[2] else "Bench"
            prior, weights, outcomes = states.get(key, (1., np.empty(0), np.empty(0)))
            variance = model["LatentVarianceMinutesSquared"][members[0]]
            gain = variance/(variance+model["Roles"][label]["ObservationVarianceMinutesSquared"]/len(observed))
            states[key] = (prior*(1-gain), np.r_[weights*(1-gain), np.full(len(observed), gain/len(observed))],
                           np.r_[outcomes, observed])
    if max_error > 1e-8:
        raise AssertionError(f"Saved predictions were not reconstructed: {max_error}")
    return records, max_error


def aggregate(records, min_history):
    result = {}
    for role in ("Starter", "Bench"):
        selected = [r for r in records if r["Role"] == role and r["HistoryCount"] >= min_history]
        if not selected:
            raise ValueError(f"No {role} observations with {min_history} games")
        weights = np.array([r["Weights"] for r in selected])
        cumulative = weights.cumsum(axis=1)
        mean = cumulative.mean(axis=0)
        result[role] = {"N": len(selected), "MeanWeights": weights.mean(axis=0).tolist(),
                        "CumulativeMean": mean.tolist(),
                        "CumulativeP10": np.quantile(cumulative, .1, axis=0).tolist(),
                        "CumulativeP90": np.quantile(cumulative, .9, axis=0).tolist(),
                        "PriorWeight": float(np.mean([r["PriorWeight"] for r in selected])),
                        "OlderWeight": float(np.mean([r["OlderWeight"] for r in selected])),
                        "MeanEqualWeightGames": float(np.mean([r["EqualWeightGames"] for r in selected])),
                        "LagsForShare": {str(percent): int(np.flatnonzero(mean >= percent/100)[0]+1)
                                         if np.any(mean >= percent/100) else None for percent in (50, 80, 90, 95)},
                        "LastGamesWeight": {str(n): float(mean[n-1]) for n in (1, 5, 10, 20, 50, 100, 200)}}
    return result


def figure(groups, subtitle, output, name):
    plt.rcParams.update({"font.size": 10, "svg.fonttype": "none", "axes.spines.top": False, "axes.spines.right": False})
    fig, axes = plt.subplots(2, 2, figsize=(12.6, 8.1))
    top_limit = max(max(g["MeanWeights"][:60]) for g in groups.values())*1.2
    for column, (role, g) in enumerate(groups.items()):
        role_label = "Starters" if role == "Starter" else "Bench"
        color = "#176c98" if role == "Starter" else "#7358a5"
        ax = axes[0, column]
        ax.plot(np.arange(1, 61), g["MeanWeights"][:60], color=color, linewidth=2.3)
        ax.set(title=f"{role_label} - weight of each previous game", xlim=(1, 60), ylim=(0, top_limit),
               xlabel="Same-role game lag (1 = most recent appearance)", ylabel="Average weight in predicted mean")
        ax.text(.97, .93, f"Newest game: {g['MeanWeights'][0]:.1%}\nLast 10: {g['LastGamesWeight']['10']:.1%}\n"
                f"Role prior: {g['PriorWeight']:.1%}", transform=ax.transAxes, ha="right", va="top", color=color)
        ax.yaxis.set_major_formatter(PercentFormatter(1))
        ax.grid(alpha=.16)
        ax = axes[1, column]
        x = np.arange(101)
        ax.fill_between(x, np.r_[0, g["CumulativeP10"][:100]], np.r_[0, g["CumulativeP90"][:100]],
                        color=color, alpha=.12, label="10th-90th percentile across predictions")
        ax.plot(x, np.r_[0, g["CumulativeMean"][:100]], color=color, linewidth=2.3, label="Average cumulative weight")
        for share in (50, 90):
            ax.axhline(share/100, color="#9aa8b6", linestyle="--", linewidth=.8)
            lag = g["LagsForShare"][str(share)]
            if lag is not None and lag <= 100:
                ax.axvline(lag, color=color, linestyle=":" if share == 50 else "--", linewidth=1)
                ax.text(lag+1, share/100-.06, f"{share}%: {lag} games", fontsize=9, color=color)
        ax.set(title=f"{role_label} - total weight captured by recent games", xlim=(0, 100), ylim=(0, 1.03),
               xlabel="Number of most recent same-role appearances", ylabel="Cumulative share of predicted-mean weight")
        ax.yaxis.set_major_formatter(PercentFormatter(1))
        ax.grid(alpha=.16)
        ax.legend(loc="lower right", fontsize=8, frameon=False)
    fig.suptitle("How far back does the Bayesian minutes model remember?", fontsize=18, x=.075, ha="left", y=.99)
    fig.text(.075, .94, subtitle, fontsize=11)
    fig.text(.075, .015, "Exact linear weights in the saved predicted mean, holding fitted parameters fixed. Not information gain or a lag-ablation accuracy test.\n"
             "Only positive same-role appearances count; zero-minute games do not update. Shading shows prediction-to-prediction variation, not a confidence interval.", fontsize=9, color="#516377")
    fig.subplots_adjust(left=.075, right=.98, top=.84, bottom=.14, hspace=.5, wspace=.23)
    fig.savefig(output/f"{name}.png", dpi=170)
    svg = io.StringIO()
    fig.savefig(svg, format="svg")
    plt.close(fig)
    return svg.getvalue()[svg.getvalue().index("<svg"):]


def generate(results_path, output):
    source = results_path.read_bytes()
    if output.exists():
        previous = json.loads((output/"summary.json").read_text(encoding="utf-8"))
        if previous["SourceSha256"] != sha256(source).hexdigest():
            raise ValueError("Use a new diagnostic output directory for changed model data")
    payload = json.loads(source)
    records, error = extract(payload)
    groups = {"all": aggregate(records, 1), "established": aggregate(records, 50)}
    output.mkdir(parents=True, exist_ok=True)
    summary = {"ModelVersion": payload["Models"][0]["Version"], "Source": str(results_path.resolve()),
               "SourceSha256": sha256(source).hexdigest(), "MaximumPredictionReconstructionError": error,
               "Population": "2025-26 validation, positive minutes, benchmark-matched", "MaxLag": 200,
               "Groups": groups}
    (output/"summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    with (output/"game-summary.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(["Role", "Player", "GameId", "Date", "HistoryCount", "PriorWeight", "Last1", "Last5", "Last10", "Last20", "Last50", "Last100", "Older200", "EquivalentEqualWeightGames"])
        for r in records:
            writer.writerow([r[k] for k in ("Role", "Player", "GameId", "Date", "HistoryCount", "PriorWeight")]
                            +[float(r["Weights"][:n].sum()) for n in (1, 5, 10, 20, 50, 100)]
                            +[r["OlderWeight"], r["EqualWeightGames"]])
    charts = {}
    for key, g in groups.items():
        charts[key] = figure(g, "2025-26 validation | " + ("all benchmark-matched predictions" if key == "all" else "predictions with at least 50 prior same-role appearances"), output, key)
    tables = {}
    for key, g in groups.items():
        rows = []
        for role, s in g.items():
            rows.append("<tr>"+"".join(f"<td>{value}</td>" for value in [role, f"{s['N']:,}",
                        f"{s['MeanWeights'][0]:.1%}", *[s["LagsForShare"][str(n)] or ">200" for n in (50, 80, 90, 95)],
                        f"{s['PriorWeight']:.1%}", f"{s['OlderWeight']:.2%}", f"{s['MeanEqualWeightGames']:.1f}"])+"</tr>")
        tables[key] = "".join(rows)
    html = ('<!doctype html><html lang="en"><meta charset="utf-8"><title>Bayesian memory by lag</title>'
            '<style>body{font:16px system-ui;max-width:1400px;margin:26px auto;padding:0 20px;color:#172638}p{line-height:1.5}'
            'svg{width:100%;height:auto}select{padding:9px;font:inherit}table{border-collapse:collapse;width:100%}'
            'td,th{padding:9px;text-align:right;border-bottom:1px solid #ddd}td:first-child,th:first-child{text-align:left}.scroll{overflow:auto}</style>'
            '<h1>Bayesian model memory, separately by starting role</h1><p>Each forecast is a weighted sum of the role prior and earlier same-role minute observations. '
            'We replay those weights exactly: after an observation, its Kalman gain becomes its weight and every older coefficient is multiplied by one minus that gain. '
            'Weights depend on gaps and uncertainty, so there is no single fixed lag-weight vector.</p>'
            '<label for="cohort">Prediction population: </label><select id="cohort" onchange="show(this.value)">'
            '<option value="all">All matched validation predictions</option><option value="established">At least 50 earlier same-role appearances</option></select>')
    for key in groups:
        html += (f'<section id="{key}"'+(' hidden' if key != "all" else '')+'>'+charts[key]
                 +'<div class="scroll"><table><thead><tr><th>Role</th><th>Predictions</th><th>Newest weight</th><th>Games for 50%</th><th>80%</th><th>90%</th><th>95%</th><th>Prior</th><th>Older than 200</th><th>Equal-weight games*</th></tr></thead><tbody>'
                 +tables[key]+'</tbody></table></div></section>')
    html += ('<p><strong>Reading the plots:</strong> the upper plots show each lag\'s average coefficient; the lower plots sum coefficients from the newest game backwards. '
             'The 50/80/90/95% counts refer to the average share of the entire predicted-mean weight, including the role-prior remainder, not percentages renormalised to history alone. '
             'Missing older lags have zero weight. Games with long histories do not receive extra averaging weight.</p>'
             '<p><strong>What this does and does not measure:</strong> this is exact influence on the predicted mean with fitted hyperparameters fixed. It is a rough picture of memory, '
             'not mutual information, incremental predictive value, a precision decomposition or a comparison of models refitted with different lookbacks. '
             '*Equal-weight games is the per-prediction concentration equivalent (sum of past-game weights) squared divided by sum of their squares, averaged across predictions; '
             'it is not a count of independent observations.</p>'
             '<p>A long gap before a prediction increases uncertainty but leaves the mean and its weights unchanged. '
             'The next observed appearance can receive a larger update. Excluded zero-minute games do not update either role; opposite-role appearances do not update this role.</p>'
             '<p>The prior share and older-than-200 share are retained explicitly. The shaded band is the 10th-90th percentile across forecasts, not statistical uncertainty about the average. '
             'Same-date repeated observations share one joint update and equal new weights. No refitting.</p>'
             '<p><a href="summary.json">Full curves, counts and provenance</a> | <a href="game-summary.csv">Per-game diagnostics</a></p>'
             '<script>function show(v){for(const k of ["all","established"])document.getElementById(k).hidden=k!==v}</script></html>')
    (output/"index.html").write_text(html, encoding="utf-8")
    print(json.dumps({"MaximumReconstructionError": error, "Groups": {k:{r:{n:v for n,v in s.items() if n not in {"MeanWeights", "CumulativeMean", "CumulativeP10", "CumulativeP90"}} for r,s in g.items()} for k,g in groups.items()}}, indent=2))
    print(output/"index.html")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    generate(args.results, args.output)
