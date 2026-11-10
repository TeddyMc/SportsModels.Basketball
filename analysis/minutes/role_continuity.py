"""Add a prior-five-team-games, same-role calibration diagnostic; no refitting."""
from bisect import bisect_left
from collections import defaultdict
import argparse
import csv
from hashlib import sha256
import json
from pathlib import Path


def prior_five_same_role(rows, ledger):
    """Prior five regular-season games of the target team, strictly earlier dates.

    A prior appearance need not pass the typical-minute training gates. Missing
    roster records/participation/roles fail the continuity test rather than being skipped.
    """
    players = {}
    schedules = defaultdict(dict)
    for o in ledger:
        player_key = (o['RegistryId'], o['PlayerId'], o['TeamId'], o['GameId'])
        if player_key in players:
            raise ValueError('Duplicate participation-ledger identity')
        players[player_key] = o
        schedule = schedules[o['RegistryId'], o['TeamId']]
        if o['GameId'] in schedule and schedule[o['GameId']] != o['Date']:
            raise ValueError('Conflicting game dates')
        schedule[o['GameId']] = o['Date']
    ordered = {k: sorted((day, game) for game, day in games.items()) for k, games in schedules.items()}
    dates = {k: [day for day, _ in games] for k, games in ordered.items()}
    output = []
    for row in rows:
        o = row['Observation']
        team = (o['RegistryId'], o['TeamId'])
        position = bisect_left(dates.get(team, []), o['Date'])
        prior = ordered.get(team, [])[max(0, position-5):position]
        selected = len(prior) == 5 and o['Started'] is not None
        for day, game in prior:
            p = players.get((o['RegistryId'], o['PlayerId'], o['TeamId'], game))
            played = p is not None and (p.get('Played') is True or
                (p.get('ActualRegulationSeconds') is not None and p['ActualRegulationSeconds'] > 0))
            selected = selected and played and p['Started'] is o['Started']
        output.append({'Included': bool(selected), 'PreviousTeamGameIds': [g for _, g in prior]})
    return output


def run(reference_path, candidate_path, review_path):
    import math
    import numpy as np
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt

    reference = json.loads(reference_path.read_bytes())
    candidate = json.loads(candidate_path.read_bytes())
    if reference['Rows'] != candidate['Rows'] or not reference['Summary'].get('TypicalMinutes'):
        raise ValueError('Expected the same typical-minute cohort and labels')
    ledger_info = reference['Summary']['ParticipationLedger']
    ledger_bytes = Path(ledger_info['Path']).read_bytes()
    if sha256(ledger_bytes).hexdigest() != ledger_info['Sha256']:
        raise ValueError('Changed participation ledger')
    rows = reference['Rows']
    membership = prior_five_same_role(rows, json.loads(ledger_bytes))
    baseline, bayesian = reference['Models'][0], candidate['Models'][0]
    summary = {'Definition': 'Previous five regular-season games of the target team, on strictly earlier dates; confirmed participation in every game and starter/bench status equal to the target role. Cross-season history is allowed. Missing records/status fail; prior games need not pass typical-minute gates. Target evaluation retains the existing matched, filtered and adjusted validation cohort. No model is refitted.',
               'LedgerSha256': ledger_info['Sha256'], 'ReferenceSha256': sha256(reference_path.read_bytes()).hexdigest(),
               'CandidateSha256': sha256(candidate_path.read_bytes()).hexdigest(), 'Roles': {}}
    fig, axes = plt.subplots(1, 2, figsize=(12, 5), layout='constrained')
    for ax, role in zip(axes, (True, False)):
        matched = [i for i, r in enumerate(rows) if r['Observation']['Split'] == 'Validation'
                   and r['Observation']['Started'] is role and baseline['PredictionsMinutes'][i] is not None
                   and bayesian['PredictionsMinutes'][i] is not None]
        indices = [i for i in matched if membership[i]['Included']]
        role_summary = {'MatchedN': len(matched), 'ContinuousRoleN': len(indices), 'Models': {}}
        for name, model, color in (('5+5 benchmark', baseline, '#687b8c'), ('Bayesian', bayesian, '#c25327')):
            bins = defaultdict(list)
            for i in indices:
                bins[math.floor(model['PredictionsMinutes'][i])].append(i)
            points = [{'PredictionMean': float(np.mean([model['PredictionsMinutes'][i] for i in bins[k]])),
                       'TargetMean': float(np.mean([rows[i]['Observation']['RegulationSeconds']/60 for i in bins[k]])),
                       'N': len(bins[k])} for k in sorted(bins)]
            errors = np.array([rows[i]['Observation']['RegulationSeconds']/60-model['PredictionsMinutes'][i] for i in indices])
            role_summary['Models'][name] = {'Bins': points, 'RMSE': float(np.sqrt(np.mean(errors**2))) if len(errors) else None,
                'MAE': float(np.mean(np.abs(errors))) if len(errors) else None}
            ax.plot([p['PredictionMean'] for p in points], [p['TargetMean'] for p in points], label=name, color=color)
        ax.plot([0, 48], [0, 48], '--', color='#24384b', label='Perfect prediction')
        ax.set(xlim=(0, 48), ylim=(0, 48), xlabel='Predicted typical minutes', ylabel='Mean adjusted target minutes',
               title=f'{"Starters" if role else "Bench"}: same role in previous 5 team games\nMatched validation N={len(indices):,} / {len(matched):,}')
        ax.grid(alpha=.2)
        ax.legend()
        summary['Roles']['Starter' if role else 'Bench'] = role_summary
    fig.savefig(review_path/'calibration-last-five-same-role.png', dpi=150)
    plt.close(fig)
    (review_path/'last-five-same-role.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    with (review_path/'last-five-same-role-membership.csv').open('w', newline='', encoding='utf-8') as stream:
        writer = csv.writer(stream)
        writer.writerow(['RegistryId', 'PlayerId', 'TeamId', 'GameId', 'Date', 'Split', 'Started', 'Included', 'PreviousTeamGameIds'])
        for row, member in zip(rows, membership):
            o = row['Observation']
            writer.writerow([o[k] for k in ('RegistryId', 'PlayerId', 'TeamId', 'GameId', 'Date', 'Split', 'Started')] +
                            [member['Included'], '|'.join(member['PreviousTeamGameIds'])])
    path = review_path/'index.html'
    html = path.read_text(encoding='utf-8')
    start, end = '<!-- LAST-FIVE-SAME-ROLE -->', '<!-- END-LAST-FIVE-SAME-ROLE -->'
    section = start+'<h2>Same role in each of the previous five team games</h2><p>'+summary['Definition']+'</p><img src="calibration-last-five-same-role.png"><p>Same one-minute bins, axes and model predictions as above; counts are shown per role. This is an evaluation subset, not a new training filter.</p>'+end
    if start in html:
        a, b = html.index(start), html.index(end)+len(end)
        html = html[:a]+section+html[b:]
    else:
        marker = '<img src="calibration.png">'
        if marker not in html:
            raise ValueError('Original calibration plot not found')
        html = html.replace(marker, marker+section, 1)
    path.write_text(html, encoding='utf-8')
    print(json.dumps({role: {k:v for k,v in values.items() if k != 'Models'} for role, values in summary['Roles'].items()}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--reference-results', type=Path, required=True)
    parser.add_argument('--candidate-results', type=Path, required=True)
    parser.add_argument('--review', type=Path, required=True)
    args = parser.parse_args()
    run(args.reference_results, args.candidate_results, args.review)


if __name__ == '__main__':
    main()
