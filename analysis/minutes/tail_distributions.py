"""Actual-minute empirical densities in low/high prediction tails, without refitting."""

import argparse
import csv
from hashlib import sha256
import io
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from reportlab.lib.pagesizes import landscape, A4
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas


def generate(results_path, output, tail_fraction=.1):
    if not 0 < tail_fraction < .5:
        raise ValueError("Tail fraction must be between zero and one half")
    source = results_path.read_bytes()
    if output.exists():
        previous = json.loads((output/"summary.json").read_text(encoding="utf-8"))
        if previous["SourceSha256"] != sha256(source).hexdigest() or previous["TailFraction"] != tail_fraction:
            raise ValueError("Use a new directory for different diagnostic inputs or tail definitions")
    payload = json.loads(source)
    model = payload["Models"][0]
    benchmark = payload["Benchmark"]["PredictionsMinutes"]
    edges = np.arange(0, 50, 2)
    groups, samples = [], []
    for role, label in ((True, "Starters"), (False, "Bench")):
        eligible = [(i, r["Observation"]) for i, r in enumerate(payload["Rows"])
                    if r["Observation"]["Split"] == "Validation"
                    and r["Observation"]["Started"] is role
                    and r["Observation"]["RegulationSeconds"] is not None
                    and r["Observation"]["RegulationSeconds"] > 0
                    and model["PredictionsMinutes"][i] is not None and benchmark[i] is not None]
        predictions = np.array([model["PredictionsMinutes"][i] for i, _ in eligible])
        if not len(predictions):
            raise ValueError(f"No matched validation observations for {label}")
        low, high = np.quantile(predictions, [tail_fraction, 1-tail_fraction])
        for tail, cutoff in (("Low", low), ("High", high)):
            selected = [(i, o) for i, o in eligible if
                        (model["PredictionsMinutes"][i] <= cutoff if tail == "Low"
                         else model["PredictionsMinutes"][i] >= cutoff)]
            actual = np.array([o["RegulationSeconds"]/60 for _, o in selected])
            predicted = np.array([model["PredictionsMinutes"][i] for i, _ in selected])
            if not np.all((actual > 0) & (actual <= 48)):
                raise ValueError("Unexpected actual-minute support")
            counts, _ = np.histogram(actual, bins=edges)
            density = counts / (len(actual)*np.diff(edges))
            assert int(counts.sum()) == len(actual)
            assert np.isclose(np.sum(density*np.diff(edges)), 1)
            group = {"Role": label, "Tail": tail, "Cutoff": float(cutoff),
                     "N": len(actual), "RolePopulation": len(eligible),
                     "Players": len({(o["RegistryId"], o["PlayerId"]) for _, o in selected}),
                     "MeanPredicted": float(predicted.mean()), "MeanActual": float(actual.mean()),
                     "MedianActual": float(np.median(actual)), "ActualQuantiles10_90": np.quantile(actual, [.1, .9]).tolist(),
                     "ActualUnder10Fraction": float(np.mean(actual < 10)), "ActualOver30Fraction": float(np.mean(actual >= 30)),
                     "BiasActualMinusPredicted": float(np.mean(actual-predicted)),
                     "Density": density.tolist(), "BinCounts": counts.tolist()}
            groups.append(group)
            samples.extend({"TailRole": label, "Tail": tail, "PredictionMinutes": model["PredictionsMinutes"][i],
                            "ActualMinutes": o["RegulationSeconds"]/60, **o} for i, o in selected)

    output.mkdir(parents=True, exist_ok=True)
    meta = {"ModelVersion": model["Version"], "Source": str(results_path.resolve()),
            "SourceSha256": sha256(source).hexdigest(), "Split": "Validation", "Season": "2025-26",
            "TailFraction": tail_fraction, "Cohort": "Matched with benchmark; positive regulation minutes",
            "BinEdgesMinutes": edges.tolist(), "Groups": groups}
    (output/"summary.json").write_text(json.dumps(meta, indent=2), encoding="utf-8")
    with (output/"samples.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(samples[0]))
        writer.writeheader()
        writer.writerows(samples)

    plt.rcParams.update({"font.size": 10, "axes.spines.top": False, "axes.spines.right": False,
                         "svg.fonttype": "none"})
    fig, axes = plt.subplots(2, 2, figsize=(12.8, 8.2), sharex=True, sharey=True)
    ymax = max(max(g["Density"]) for g in groups)*1.2
    for ax, g in zip(axes.flat, groups):
        color = "#176c98" if g["Role"] == "Starters" else "#7358a5"
        ax.stairs(g["Density"], edges, color=color, linewidth=2, label="Actual-minute density")
        ax.axvline(g["MeanPredicted"], color="#c25327", linestyle="--", linewidth=1.7,
                   label=f"Mean prediction {g['MeanPredicted']:.1f}")
        ax.axvline(g["MeanActual"], color="#16826c", linestyle=":", linewidth=1.8,
                   label=f"Mean actual {g['MeanActual']:.1f}")
        operator = "<=" if g["Tail"] == "Low" else ">="
        ax.set_title(f"{g['Role']} - {g['Tail'].lower()} prediction tail\n"
                     f"Prediction {operator} {g['Cutoff']:.2f} min | n={g['N']:,} | {g['Players']} players", loc="left")
        ax.set(xlim=(0, 48), ylim=(0, ymax), xlabel="Actual regulation minutes played",
               ylabel="Probability density (per minute)", xticks=np.arange(0, 49, 6))
        ax.grid(axis="y", alpha=.18)
        ax.tick_params(labelbottom=True)
        ax.legend(fontsize=9, loc="upper right" if g["Role"] == "Bench" and g["Tail"] == "Low"
                  else "upper left", frameon=False)
    fig.suptitle("Actual minutes played in the four prediction tails", fontsize=18, x=.065, ha="left", y=.99)
    fig.text(.065, .935, f"2025-26 validation | Bayesian capped drift | bottom/top {100*tail_fraction:g}% by prediction within role", fontsize=11)
    fig.text(.065, .015, "Matched report cohort; positive regulation minutes only. Two-minute bins, each density integrates to 1.\n"
             "Groups are selected by predictions, not actual outcomes. Ties at each cutoff are included. No refitting or exclusions beyond the report cohort.", fontsize=9, color="#516377")
    fig.subplots_adjust(top=.83, bottom=.13, left=.065, right=.98, hspace=.48, wspace=.18)
    fig.savefig(output/"distributions.png", dpi=180)
    svg = io.StringIO()
    fig.savefig(svg, format="svg")
    plt.close(fig)
    pdf = canvas.Canvas(str(output/"distributions.pdf"), pagesize=landscape(A4))
    width, height = landscape(A4)
    pdf.setTitle("Actual minutes played in the four prediction tails")
    pdf.drawImage(ImageReader(str(output/"distributions.png")), 0, (height-width*8.2/12.8)/2,
                  width=width, height=width*8.2/12.8)
    pdf.showPage()
    pdf.save()
    table = "".join("<tr>"+"".join(f"<td>{v}</td>" for v in [g["Role"]+" / "+g["Tail"], g["N"],
                    f"{g['MeanPredicted']:.2f}", f"{g['MeanActual']:.2f}", f"{g['MedianActual']:.2f}",
                    f"{g['BiasActualMinusPredicted']:+.2f}", f"{g['ActualUnder10Fraction']:.1%}"])+"</tr>" for g in groups)
    chart = svg.getvalue()[svg.getvalue().index("<svg"):]
    html = ('<!doctype html><html lang="en"><meta charset="utf-8"><title>Actual-minute tail distributions</title>'
            '<style>body{font:16px system-ui;max-width:1400px;margin:24px auto;padding:0 20px;color:#172638}'
            'svg{width:100%;height:auto}table{border-collapse:collapse;width:100%}td,th{padding:10px;text-align:right;border-bottom:1px solid #ddd}'
            'td:first-child,th:first-child{text-align:left}p{line-height:1.5}</style>'
            +chart+'<p>PDF means <strong>probability density function</strong> here. These are empirical density lines, without smoothing. '
            'The four groups use the same positive-minute, benchmark-matched validation population as the model report. '
            'Both density axes use the same scale. Density height is not probability; the area across an interval gives its proportion.</p>'
            '<table><thead><tr><th>Tail</th><th>Games</th><th>Mean predicted</th><th>Mean actual</th><th>Median actual</th><th>Actual - predicted</th><th>Actual &lt;10 min</th></tr></thead><tbody>'
            +table+'</tbody></table><p>Minutes throughout. This is descriptive analysis of held-out predictions, not a model refit. '
            'Small positive appearances, including early exits, remain included. Zero-minute games remain in the separate participation ledger.</p>'
            '<p><a href="distributions.pdf">Download PDF</a> | <a href="samples.csv">Selected player-games (CSV)</a> | <a href="summary.json">Counts, densities and provenance</a></p></html>')
    (output/"index.html").write_text(html, encoding="utf-8")
    print(json.dumps([{k:v for k,v in g.items() if k not in {"Density", "BinCounts"}} for g in groups], indent=2))
    print(output/"index.html")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--tail-fraction", type=float, default=.1)
    args = parser.parse_args()
    generate(args.results, args.output, args.tail_fraction)
