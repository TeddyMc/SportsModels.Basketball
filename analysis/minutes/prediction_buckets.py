"""Empirical typical-minute PDFs for fixed Bayesian forecast buckets; no refitting."""
import argparse
import csv
from hashlib import sha256
import json
from pathlib import Path

import numpy as np


def summarize(observations, predictions, indices, edges):
    values = np.array([observations[i]['RegulationSeconds']/60 for i in indices])
    forecasts = np.array([predictions[i] for i in indices])
    if len(values) and not np.all((values > 0) & (values <= 48)):
        raise ValueError('Unexpected adjusted typical-minute support')
    counts, _ = np.histogram(values, bins=edges)
    density = counts/(len(values)*np.diff(edges)) if len(values) else np.zeros(len(edges)-1)
    if int(counts.sum()) != len(values) or (len(values) and not np.isclose(sum(density*np.diff(edges)),1)):
        raise ValueError('Density normalization failed')
    return {'N': len(values), 'Starters': sum(observations[i]['Started'] is True for i in indices),
            'Bench': sum(observations[i]['Started'] is False for i in indices),
            'Players': len({(observations[i]['RegistryId'],observations[i]['PlayerId']) for i in indices}),
            'MeanPrediction': float(forecasts.mean()) if len(values) else None,
            'MeanTarget': float(values.mean()) if len(values) else None,
            'MedianTarget': float(np.median(values)) if len(values) else None,
            'TargetQuantiles10_90': np.quantile(values,[.1,.9]).tolist() if len(values) else None,
            'Counts': counts.tolist(), 'Density': density.tolist(),
            'Integral': float(sum(density*np.diff(edges)))}


def generate(results_path, output, continuity_csv=None):
    source = results_path.read_bytes()
    payload = json.loads(source)
    if not payload['Summary'].get('TypicalMinutes'):
        raise ValueError('Use the filtered, adjusted typical-minute model')
    observations = [r['Observation'] for r in payload['Rows']]
    model = payload['Models'][0]
    predictions = model['PredictionsMinutes']
    benchmark = payload['Benchmark']['PredictionsMinutes']
    stable = [False]*len(observations)
    continuity_hash = None
    if continuity_csv:
        continuity_hash = sha256(continuity_csv.read_bytes()).hexdigest()
        with continuity_csv.open(encoding='utf-8-sig',newline='') as stream:
            members = list(csv.DictReader(stream))
        if len(members) != len(observations):
            raise ValueError('Continuity membership length mismatch')
        for i,(m,o) in enumerate(zip(members,observations)):
            if (m['RegistryId'],int(m['PlayerId']),m['GameId']) != (o['RegistryId'],o['PlayerId'],o['GameId']):
                raise ValueError('Continuity membership identity mismatch')
            stable[i] = m['Included']=='True'
    for o in observations:
        if o['TypicalExclusions'] or o['ActualRegulationSeconds'] is None or o['ActualRegulationSeconds']<=0:
            raise ValueError('Excluded observation in model rows')
        if o['FoulExitEvidence'] is not None:
            raise ValueError('Foul-trouble observation in model rows')
        if o['Started'] is True and not any(end>1440 for start,end in o['CourtIntervals']):
            raise ValueError('Starter without second-half minutes')
    edges = np.arange(0,50,2)
    views = {}
    cohorts = ['All eligible Bayesian forecasts','Matched with benchmark']
    if continuity_csv:cohorts.append('Same role in previous five team games (matched)')
    for split in ('Validation','Training','Both'):
        for cohort in cohorts:
            base = [i for i,o in enumerate(observations) if (split=='Both' or o['Split']==split)
                    and predictions[i] is not None and o['RegulationSeconds'] is not None
                    and (cohort==cohorts[0] or benchmark[i] is not None)
                    and (cohort!=cohorts[-1] or len(cohorts)<3 or stable[i])]
            groups = []
            for label,condition in (('Expected minutes > 35',lambda p:p>35),('Expected minutes < 26',lambda p:p<26)):
                indices = [i for i in base if condition(predictions[i])]
                groups.append({'Label':label,**summarize(observations,predictions,indices,edges)})
            views[f'{split}|{cohort}'] = {'BaseN':len(base),'Groups':groups}
    meta = {'ModelVersion':model['Version'],'Results':str(results_path.resolve()),'SourceSha256':sha256(source).hexdigest(),
            'ContinuitySha256':continuity_hash,'Target':'Filtered, late-extended typical regulation minutes, conditional on playing',
            'Selection':'Strict forecast >35 or <26; starters and bench pooled by player-game. 26 and 35 are excluded from the corresponding buckets.',
            'BinEdgesMinutes':edges.tolist(),'BinWidthMinutes':2,'Cohorts':cohorts,'Views':views}
    output.mkdir(parents=True,exist_ok=True)
    summary_path = output/'summary.json'
    if summary_path.exists() and json.loads(summary_path.read_bytes())['SourceSha256']!=meta['SourceSha256']:
        raise ValueError('Use a new output directory for changed fits')
    summary_path.write_text(json.dumps(meta,indent=2),encoding='utf-8')
    with (output/'samples.csv').open('w',newline='',encoding='utf-8') as stream:
        writer=csv.writer(stream)
        writer.writerow(['RegistryId','PlayerId','Name','TeamId','GameId','Date','Split','Started','PredictionMinutes','AdjustedTargetMinutes','OriginalActualMinutes','Bucket','BenchmarkMatched','SameRolePreviousFive'])
        for i,o in enumerate(observations):
            p=predictions[i]
            if p is None or not (p>35 or p<26):continue
            writer.writerow([o[k] for k in ('RegistryId','PlayerId','Name','TeamId','GameId','Date','Split','Started')]+
                [p,o['RegulationSeconds']/60,o['ActualRegulationSeconds']/60,'>35' if p>35 else '<26',benchmark[i] is not None,stable[i]])
    import matplotlib
    matplotlib.use("Agg")
    from matplotlib import pyplot as plt
    view=views['Validation|'+cohorts[0]]
    fig,axes=plt.subplots(1,2,figsize=(13,5),layout='constrained',sharex=True,sharey=True)
    ymax=max(max(g['Density']) for g in view['Groups'])*1.2
    for ax,g in zip(axes,view['Groups']):
        ax.stairs(g['Density'],edges,color='#176c98',linewidth=2,label='Adjusted-minute empirical PDF')
        ax.axvline(g['MeanPrediction'],color='#c25327',linestyle='--',label=f"Mean prediction {g['MeanPrediction']:.2f}")
        ax.axvline(g['MeanTarget'],color='#16826c',linestyle=':',label=f"Mean target {g['MeanTarget']:.2f}")
        ax.set(title=f"{g['Label']} | N={g['N']:,}",xlabel='Adjusted typical regulation minutes',ylabel='Probability density per minute',xlim=(0,48),ylim=(0,ymax))
        ax.legend(fontsize=9);ax.grid(axis='y',alpha=.2)
    fig.savefig(output/'distributions.png',dpi=150);plt.close(fig)
    data=json.dumps(meta,ensure_ascii=True,separators=(',',':')).replace('<','\\u003c')
    html='''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Typical-minute distributions by expected minutes</title>
<style>body{font:16px system-ui;max-width:1300px;margin:28px auto;padding:0 20px;color:#172638;background:#f5f7fa}p{line-height:1.5}label{margin-right:20px}select{padding:8px;font:inherit}.plots{display:grid;grid-template-columns:1fr 1fr;gap:20px}section{background:white;padding:15px;border:1px solid #d7dfe7;border-radius:8px}svg{width:100%;height:auto}table{border-collapse:collapse;width:100%;background:white;margin-top:20px}td,th{text-align:left;padding:9px;border-bottom:1px solid #ddd}@media(max-width:800px){.plots{grid-template-columns:1fr}}</style>
<h1>Typical-minute PDFs by expected-minute bucket</h1><p>Starters and bench are pooled together, with equal weight per player-game. Selection is by the Bayesian pre-game expectation: <strong>&gt;35</strong> and <strong>&lt;26</strong> minutes. The plotted outcome is the <strong>filtered, adjusted typical-minute label</strong>, including the agreed late extension. All existing competitiveness, foul, H2 and participation exclusions remain applied.</p>
<label>Dataset <select id="split"><option>Validation</option><option>Training</option><option value="Both">Training + validation</option></select></label><label>Forecast population <select id="cohort"></select></label><p id="scope"></p>
<div class="plots"><section><h2 id="title0"></h2><svg id="plot0" viewBox="0 0 600 400"></svg><p id="stats0"></p></section><section><h2 id="title1"></h2><svg id="plot1" viewBox="0 0 600 400"></svg><p id="stats1"></p></section></div>
<p>Empirical probability density functions, using two-minute histogram bins with no smoothing. Each nonempty PDF integrates to 1. Both panels share the same axes within the selected view. Dashed orange = mean prediction; dotted green = mean adjusted target. Hover density segments for bin counts. Forecasts equal to 35 or 26 do not enter the corresponding strict bucket.</p>
<p><a href="samples.csv">Selected player-games (CSV)</a> · <a href="summary.json">All bin counts, summary statistics and source hashes</a> · <a href="distributions.png">Default-view image</a></p><p id="model"></p>
<script>const data=__DATA__;const $=id=>document.getElementById(id),ns='http://www.w3.org/2000/svg';
function el(tag,attrs,text){const e=document.createElementNS(ns,tag);for(const [k,v] of Object.entries(attrs))e.setAttribute(k,v);if(text!=null)e.textContent=text;return e}
for(const name of data.Cohorts){const option=document.createElement('option');option.value=name;option.textContent=name;$('cohort').append(option)}
function render(){const split=$('split').value,cohort=$('cohort').value,view=data.Views[split+'|'+cohort];$('scope').textContent=`${split==='Validation'?'2025-26 held-out validation':split==='Training'?'2021-22 through 2024-25 fitted training data':'Training and validation combined (includes fitted training data)'} · ${view.BaseN.toLocaleString()} eligible forecasts before bucketing. No refitting.`;const ymax=Math.max(...view.Groups.flatMap(g=>g.Density))*1.2||1;
view.Groups.forEach((g,index)=>{const svg=$('plot'+index);svg.replaceChildren();$('title'+index).textContent=g.Label+' · N='+g.N.toLocaleString();const left=60,right=580,top=20,bottom=345,x=v=>left+v/48*(right-left),y=v=>bottom-v/ymax*(bottom-top);
for(let tick=0;tick<=48;tick+=6)svg.append(el('line',{x1:x(tick),x2:x(tick),y1:top,y2:bottom,stroke:'#edf0f3'}),el('text',{x:x(tick),y:370,'text-anchor':'middle','font-size':12},tick));
for(let j=0;j<=4;j++){let value=ymax*j/4;svg.append(el('line',{x1:left,x2:right,y1:y(value),y2:y(value),stroke:'#e4eaf0'}),el('text',{x:52,y:y(value)+4,'text-anchor':'end','font-size':12},value.toFixed(3)))}
let path='';g.Density.forEach((d,i)=>{const a=data.BinEdgesMinutes[i],b=data.BinEdgesMinutes[i+1];path+=(i?' L':'M')+x(a)+' '+y(d)+' L'+x(b)+' '+y(d);const hit=el('line',{x1:x(a),x2:x(b),y1:y(d),y2:y(d),stroke:'transparent','stroke-width':12});hit.append(el('title',{},`${a}-${b} minutes: ${g.Counts[i]} games; density ${d.toFixed(5)}`));svg.append(hit)});svg.append(el('path',{d:path,fill:'none',stroke:'#176c98','stroke-width':2.5}));
for(const [v,color,dash] of [[g.MeanPrediction,'#c25327','7 5'],[g.MeanTarget,'#16826c','2 4']])if(v!=null)svg.append(el('line',{x1:x(v),x2:x(v),y1:top,y2:bottom,stroke:color,'stroke-width':2,'stroke-dasharray':dash}));
svg.append(el('text',{x:320,y:395,'text-anchor':'middle','font-size':13},'Adjusted typical regulation minutes'),el('text',{transform:'translate(15 180) rotate(-90)','text-anchor':'middle','font-size':13},'Density per minute'));
$('stats'+index).textContent=g.N?`${g.Starters.toLocaleString()} starter + ${g.Bench.toLocaleString()} bench player-games; ${g.Players} players. Mean forecast ${g.MeanPrediction.toFixed(2)} min; mean target ${g.MeanTarget.toFixed(2)}; median ${g.MedianTarget.toFixed(2)}; 10th–90th percentiles ${g.TargetQuantiles10_90.map(v=>v.toFixed(2)).join('–')} min. PDF area ${g.Integral.toFixed(3)}.`:'No observations in this bucket.';});}
$('model').textContent='Model: '+data.ModelVersion+' · '+data.Target;$('split').addEventListener('change',render);$('cohort').addEventListener('change',render);render();</script></html>'''
    (output/'index.html').write_text(html.replace('__DATA__',data),encoding='utf-8')
    print(json.dumps({'Path':str((output/'index.html').resolve()),'DefaultValidationGroups':view['Groups']},indent=2))


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--continuity-csv',type=Path)
    args=parser.parse_args()
    generate(args.results,args.output,args.continuity_csv)
