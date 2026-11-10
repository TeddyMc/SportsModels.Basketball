"""Hoverable calibration points and player-cluster bootstrap intervals; no fitting."""
import argparse
from collections import defaultdict
from hashlib import sha256
from html import escape
import json
from pathlib import Path

import numpy as np

REPLICATES = 2000
METHOD = ('95% pointwise percentile confidence interval for each bucket mean adjusted minutes; '
          'resample players with replacement within the bucket, retaining their game sums/counts. '
          'Fixed fitted models and bin membership; excludes model-fitting uncertainty. '
          'Repeated games from a player are clustered. No interval is estimated with fewer '
          'than two players; intervals with few players are only rough sampling diagnostics. '
          'These are confidence intervals for a mean, not individual-game prediction intervals.')


def calibration_points(rows, predictions, indices, seed=20261110):
    bins = defaultdict(list)
    for i in indices:
        bins[int(np.floor(predictions[i]))].append(i)
    points = []
    for bucket, members in sorted(bins.items()):
        clusters = defaultdict(lambda: [0., 0])
        for i in members:
            o = rows[i]['Observation']
            clusters[o['RegistryId'], o['PlayerId']][0] += o['RegulationSeconds']/60
            clusters[o['RegistryId'], o['PlayerId']][1] += 1
        values = np.array(list(clusters.values()))
        low = high = None
        if len(clusters) >= 2:
            rng = np.random.default_rng(seed+bucket)
            samples = rng.integers(0, len(clusters), size=(REPLICATES, len(clusters)))
            means = values[samples, 0].sum(axis=1)/values[samples, 1].sum(axis=1)
            low, high = map(float, np.quantile(means, [.025, .975]))
        points.append({'BucketStart': bucket, 'BucketEnd': bucket+1,
                       'PredictionMean': float(np.mean([predictions[i] for i in members])),
                       'TargetMean': float(values[:, 0].sum()/values[:, 1].sum()),
                       'N': len(members), 'Players': len(clusters), 'CI95Low': low, 'CI95High': high})
    return points


def svg_panel(title, models):
    left, right, top, bottom = 60, 575, 35, 445
    x = lambda v: left+v/48*(right-left)
    y = lambda v: bottom-v/48*(bottom-top)
    pieces = [f'<svg viewBox="0 0 600 500" style="width:100%;height:auto" aria-label="{escape(title)}">']
    for tick in range(0,49,6):
        pieces += [f'<path d="M {x(tick)} {top} V {bottom} M {left} {y(tick)} H {right}" stroke="#e7ebef" fill="none"/>',
                   f'<text x="{x(tick)}" y="468" text-anchor="middle" font-size="12">{tick}</text>',
                   f'<text x="50" y="{y(tick)+4}" text-anchor="end" font-size="12">{tick}</text>']
    pieces += [f'<path d="M {left} {bottom} L {right} {top}" stroke="#24384b" stroke-dasharray="7 5" stroke-width="2"/>',
               '<text x="310" y="495" text-anchor="middle" font-size="13">Mean predicted typical minutes (x)</text>',
               '<text transform="translate(15 240) rotate(-90)" text-anchor="middle" font-size="13">Mean adjusted target minutes (y)</text>']
    for model, color in (('Benchmark','#687b8c'),('Bayesian','#c25327')):
        points = models[model]
        coordinates = ' '.join(f'{x(p["PredictionMean"])},{y(p["TargetMean"])}' for p in points)
        pieces.append(f'<g class="calibration-{model}"><polyline points="{coordinates}" stroke="{color}" stroke-width="1.8" fill="none"/>')
        for p in points:
            px, py = x(p['PredictionMean']), y(p['TargetMean'])
            ci = 'not estimable (one player)' if p['CI95Low'] is None else f"[{p['CI95Low']:.3f}, {p['CI95High']:.3f}]"
            tooltip = (f"{model}; forecast bucket [{p['BucketStart']}, {p['BucketEnd']}) min; "
                       f"x={p['PredictionMean']:.3f} min; y={p['TargetMean']:.3f} min; n={p['N']}; "
                       f"players={p['Players']}; 95% CI for mean y: {ci}")
            pieces.append(f'<g class="calibration-point" tabindex="0" role="img" aria-label="{escape(tooltip)}"><title>{escape(tooltip)}</title>')
            if p['CI95Low'] is not None:
                lo, hi = y(p['CI95Low']), y(p['CI95High'])
                pieces.append(f'<path class="confidence-bar" d="M {px} {lo} V {hi} M {px-4} {lo} H {px+4} M {px-4} {hi} H {px+4}" stroke="{color}" stroke-width="1.4" opacity="0.65" fill="none"/>')
            pieces.append(f'<circle cx="{px}" cy="{py}" r="3.4" fill="{color}" stroke="white" stroke-width="0.7"/><circle cx="{px}" cy="{py}" r="8" fill="transparent"/></g>')
        pieces.append('</g>')
    pieces.append('</svg>')
    return ''.join(pieces)


def run(reference_path, candidate_path, review):
    reference_bytes, candidate_bytes = reference_path.read_bytes(), candidate_path.read_bytes()
    a, b = json.loads(reference_bytes), json.loads(candidate_bytes)
    if a['Rows'] != b['Rows'] or not a['Summary'].get('TypicalMinutes'):
        raise ValueError('Expected identical filtered typical-minute rows')
    from role_continuity import prior_five_same_role
    info = a['Summary']['ParticipationLedger']
    ledger_bytes = Path(info['Path']).read_bytes()
    if sha256(ledger_bytes).hexdigest() != info['Sha256']:
        raise ValueError('Changed ledger')
    rows = a['Rows']; baseline, model = a['Models'][0], b['Models'][0]
    stable = prior_five_same_role(rows, json.loads(ledger_bytes))
    panels = {}
    for subset in ('All matched validation','Same role in previous five team games'):
        panels[subset] = {}
        for role, label in ((True,'Starters'),(False,'Bench')):
            ids = [i for i,r in enumerate(rows) if r['Observation']['Split']=='Validation'
                   and r['Observation']['Started'] is role and baseline['PredictionsMinutes'][i] is not None
                   and model['PredictionsMinutes'][i] is not None
                   and (subset=='All matched validation' or stable[i]['Included'])]
            panels[subset][label] = {'N':len(ids), 'Models':{
                'Benchmark':calibration_points(rows,baseline['PredictionsMinutes'],ids),
                'Bayesian':calibration_points(rows,model['PredictionsMinutes'],ids)}}
    meta = {'Method':METHOD,'Replicates':REPLICATES,'Seed':20261110,
            'ReferenceSha256':sha256(reference_bytes).hexdigest(),'CandidateSha256':sha256(candidate_bytes).hexdigest(),
            'Panels':panels}
    (review/'calibration-ci.json').write_text(json.dumps(meta,indent=2),encoding='utf-8')
    def section(name):
        controls = '<p><label><input type="checkbox" checked onchange="for(const e of this.closest(\'div.calibration-section\').querySelectorAll(\'.calibration-Benchmark\'))e.style.display=this.checked?\'\':\'none\'"> Benchmark</label> &nbsp; <label><input type="checkbox" checked onchange="for(const e of this.closest(\'div.calibration-section\').querySelectorAll(\'.calibration-Bayesian\'))e.style.display=this.checked?\'\':\'none\'"> Bayesian</label></p>'
        return '<div class="calibration-section"><p>Hover a point or confidence bar for x, y, n, player count and interval. Hide either model if points overlap.</p>'+controls+'<div style="display:grid;grid-template-columns:repeat(auto-fit,minmax(350px,1fr));gap:15px">'+''.join('<section><h3>'+role+f" · N={p['N']:,}</h3>"+svg_panel(role,p['Models'])+'</section>' for role,p in panels[name].items())+'</div><p>'+METHOD+'</p></div>'
    path = review/'index.html';html = path.read_text(encoding='utf-8')
    for key, name, image in (('ALL','All matched validation','calibration.png'),('STABLE','Same role in previous five team games','calibration-last-five-same-role.png')):
        start, end = f'<!-- CI-{key} -->',f'<!-- END-CI-{key} -->'
        replacement = start+section(name)+end
        if start in html:
            i,j=html.index(start),html.index(end)+len(end);html=html[:i]+replacement+html[j:]
        else:
            marker=f'<img src="{image}">'
            if marker not in html:raise ValueError('Missing calibration panel '+image)
            html=html.replace(marker,replacement,1)
    path.write_text(html,encoding='utf-8')
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    for index,(name,groups) in enumerate(panels.items()):
        fig,axes=plt.subplots(1,2,figsize=(12,5),layout='constrained')
        for ax,(role,panel) in zip(axes,groups.items()):
            for model_name,color in (('Benchmark','#687b8c'),('Bayesian','#c25327')):
                points=panel['Models'][model_name]
                ax.plot([p['PredictionMean'] for p in points],[p['TargetMean'] for p in points],'.-',color=color,label=model_name,markersize=4)
                for p in points:
                    if p['CI95Low'] is not None:
                        ax.vlines(p['PredictionMean'],p['CI95Low'],p['CI95High'],color=color,alpha=.55,linewidth=1)
            ax.plot([0,48],[0,48],'--',color='#24384b');ax.set(xlim=(0,48),ylim=(0,48),xlabel='Mean predicted minutes',ylabel='Mean adjusted target minutes',title=role+f" N={panel['N']:,}");ax.grid(alpha=.2);ax.legend()
        fig.suptitle(name+' - 95% player-cluster bootstrap CI')
        fig.savefig(review/f'calibration-ci-{index}.png',dpi=150);plt.close(fig)
    print(json.dumps({name:{role:{'N':p['N'],'Points':sum(len(v) for v in p['Models'].values())} for role,p in groups.items()} for name,groups in panels.items()}))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference-results',type=Path,required=True)
    parser.add_argument('--candidate-results',type=Path,required=True)
    parser.add_argument('--review',type=Path,required=True)
    args=parser.parse_args();run(args.reference_results,args.candidate_results,args.review)
