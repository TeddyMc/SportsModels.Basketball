"""Retrospective typical-minute labels from canonical events and matching diagnostics.

No provider rows are parsed. All gates are label selection, never forecast features.
"""
from collections import Counter, defaultdict
from hashlib import sha256
from pathlib import Path
import json
import re

POLICY = 'competitive-undisturbed-observed-score-v2'
PERIODS = {'FirstQuarter': 1, 'SecondQuarter': 2, 'ThirdQuarter': 3, 'FourthQuarter': 4}
PERSONAL = {'Personal', 'Shooting', 'Offensive', 'OffensiveCharge', 'LooseBall',
            'FlagrantOne', 'FlagrantTwo', 'ClearPath', 'AwayFromPlay',
            'TransitionTake', 'PersonalTake', 'DoublePersonal'}


def clock(event):
    c = event['Clock']
    q = PERIODS.get(c['Period'])
    h, m, s = map(float, c['ClockRemaining'].split(':'))
    remaining = h * 3600 + m * 60 + s
    return q, remaining, None if q is None else q * 720 - remaining


def competitive_gate(game, checks):
    """Use recorded PBP score observations; replay status is not score missingness."""
    events = game['Events']
    by_id = {e['EventId']: i for i, e in enumerate(events)}
    observations = {}
    for c in checks:
        if c['Code'] != 'SCORE.OBSERVATION':
            continue
        index = by_id.get(c.get('EventId'))
        if index is None:
            continue
        t = clock(events[index])[2]
        if t is None or t > 2640:
            continue
        if c['Status'] == 'Fail':
            return 'Contradictory score evidence', []
        # This is the converter's diagnostic contract, not a raw-provider parser.
        match = re.match(r'After-row observation home=(\d+), away=(\d+); effective stream ', c['Evidence'])
        if not match:
            continue
        value = tuple(map(int, match.groups()))
        if index in observations and observations[index] != value:
            return 'Contradictory score evidence', []
        observations[index] = value
    anchors = [i for i in observations if clock(events[i])[2] < 2280]
    if not anchors or not any(clock(e)[2] is not None and clock(e)[2] >= 2640 for e in events):
        return 'Missing score evidence', []
    anchor = max(anchors)
    home, away = observations[anchor]
    evidence, breached = [], False
    previous_time = clock(events[anchor])[2]

    def sample(t):
        nonlocal breached
        if 2280 <= t <= 2640:
            margin = abs(home-away)
            limit = 27 - 2 * (t/60 - 38)
            evidence.append({'ElapsedSeconds': t, 'Margin': margin, 'Threshold': limit})
            breached |= margin > limit + 1e-9

    for i in range(anchor+1, len(events)):
        e = events[i]
        q, _, t = clock(e)
        if q is None:
            continue
        if t < previous_time:
            return 'Nonchronological score evidence', evidence
        if previous_time < 2280 <= t:
            sample(2280)
        sample(min(t,2640))
        if t > 2640:
            break
        if i in observations:
            next_home, next_away = observations[i]
            if next_home < home or next_away < away:
                return 'Contradictory score evidence', evidence
            home, away = next_home, next_away
        elif e['$event'] in ('FieldGoalEvent','FreeThrowEvent'):
            if e['Outcome'] not in ('Made','Missed'):
                return 'Missing score evidence', evidence
            if e['Outcome'] == 'Made':
                points = 1 if e['$event']=='FreeThrowEvent' else {'TwoPointer':2,'ThreePointer':3}.get(e['FieldGoalType'])
                if points is None:
                    return 'Missing score evidence', evidence
                if e['TeamId']==game['HomeTeam']['TeamId']:
                    home += points
                elif e['TeamId']==game['AwayTeam']['TeamId']:
                    away += points
                else:
                    return 'Missing score evidence', evidence
        sample(t)
        previous_time = t
    return ('Noncompetitive game' if breached else None), evidence


def merge_intervals(intervals):
    merged = []
    for start, end in sorted(intervals):
        if not 0 <= start <= end <= 2880:
            raise ValueError('Invalid canonical stint')
        if merged and start <= merged[-1][1]:
            merged[-1] = (merged[-1][0], max(end, merged[-1][1]))
        else:
            merged.append((start, end))
    return merged


def prepare_game(game, report):
    validation = report['Validation']
    checks = validation['Checks']
    exclusion, score_evidence = competitive_gate(game, checks)
    stints = defaultdict(list)
    for s in validation['Stints']:
        q = s['Period']
        if 1 <= q <= 4:
            stints[s['PlayerId']].append((q * 720 - s['StartRemaining'], q * 720 - s['EndRemaining']))
    stints = {p: merge_intervals(v) for p, v in stints.items()}
    counts, crossed, foul_exits, unknown, last_foul = Counter(), {}, {}, set(), {}
    regulation_ids = {e['EventId'] for e in game['Events'] if clock(e)[0] is not None}
    for c in checks:
        if c.get('EventId') is not None and c['EventId'] not in regulation_ids:
            continue
        if c['Code'] == 'FOUL.PERSONAL_OBSERVATION' and c['Status'] in ('Fail', 'NotAssessable'):
            if c.get('EntityId') is not None:
                unknown.add(c['EntityId'])
    for e in game['Events']:
        q, remaining, t = clock(e)
        if q is None:
            continue
        if e['$event'] == 'FoulEvent':
            if e['FoulType'] in ('Other', 'NotSet'):
                actors = e['CommittingParticipants']
                if not actors or any(p.get('PlayerId') is None for p in actors):
                    unknown.update(p['PlayerId'] for side in ('HomeTeam','AwayTeam') for p in game[side]['Players'])
                else:
                    unknown.update(p['PlayerId'] for p in actors)
            if e['FoulType'] in PERSONAL:
                for actor in e['CommittingParticipants']:
                    player = actor.get('PlayerId')
                    if player is None:
                        unknown.update(p['PlayerId'] for side in ('HomeTeam','AwayTeam') for p in game[side]['Players'])
                        continue
                    counts[player] += 1
                    last_foul[player] = e['EventId']
                    if counts[player] >= q + 1:
                        crossed.setdefault((player, q), e['EventId'])
        if e['$event'] in ('SubstitutionEvent', 'EjectionEvent') and remaining > 0:
            player = e.get('PlayerOutId') if e['$event'] == 'SubstitutionEvent' else e.get('PlayerId')
            if player is None:
                continue
            if counts[player] >= q + 1:
                crossed.setdefault((player, q), last_foul[player])
            if (player, q) in crossed:
                foul_exits.setdefault(player, {'Quarter': q, 'ThresholdEventId': crossed[player, q],
                                              'ExitEventId': e['EventId'], 'ElapsedSeconds': t})
    # An absent independent count comparison does not make complete canonical events unknown.
    # Actual count gaps are reflected by per-player NotAssessable checks above.
    return {'GameExclusion': exclusion, 'ScoreEvidence': score_evidence, 'Stints': stints,
            'FoulExits': foul_exits, 'UnknownFouls': unknown}


def apply_target(row, context):
    actual = row['RegulationSeconds']
    intervals = context['Stints'].get(row['PlayerId'], [])
    seconds = sum(b-a for a,b in intervals)
    on44 = any(a <= 2640 < b for a,b in intervals)
    late = sum(max(0, min(b,2880)-max(a,2640)) for a,b in intervals)
    adjustment = 240-late if on44 else 0
    reasons = []
    if context['GameExclusion']:
        reasons.append(context['GameExclusion'])
    if actual is None:
        reasons.append('Unknown complete actual minutes')
    elif abs(seconds-actual) > 0.01:
        reasons.append('Incomplete or conflicting court evidence')
    if row['PlayerId'] in context['UnknownFouls']:
        reasons.append('Unknown foul evidence')
    if row['PlayerId'] in context['FoulExits']:
        reasons.append('Foul trouble exit')
    second_half = sum(max(0,b-max(a,1440)) for a,b in intervals)
    if row['Started'] is True and actual and not second_half:
        reasons.append('Starter without second-half minutes')
    if row['Started'] is None:
        reasons.append('Unknown starting role')
    adjusted = None if actual is None else actual+adjustment
    if adjusted is not None and not 0 <= adjusted <= 2880.01:
        reasons.append('Invalid adjusted minutes')
    return {**row, 'ActualRegulationSeconds': actual, 'TypicalRegulationSeconds': adjusted,
            'RegulationSeconds': adjusted, 'LateExtensionSeconds': adjustment,
            'OnCourtAt44': on44, 'TypicalExclusions': reasons,
            'FoulExitEvidence': context['FoulExits'].get(row['PlayerId']),
            'CourtIntervals': intervals}, reasons[0] if reasons else None


def summarize(observations, audit, sources):
    all_rows = observations+audit
    groups = {}
    for split in ('Training','Validation'):
        for role, started in (('Starter',True),('Bench',False),('Unknown',None)):
            rows = [r for r in all_rows if r['Split']==split and r['Started'] is started]
            included = [r for r in observations if r['Split']==split and r['Started'] is started]
            extensions = [r['LateExtensionSeconds']/60 for r in included if r['LateExtensionSeconds'] > 1e-9]
            groups[f'{split}:{role}'] = {'Represented':len(rows),'Included':len(included),
                'Exclusions':dict(Counter(reason for r in rows for reason in r['TypicalExclusions'])),
                'Extensions':len(extensions),'ExtensionMinutesTotal':sum(extensions),
                'MeanExtensionMinutes':sum(extensions)/len(extensions) if extensions else 0}
    return {'TypicalMinutes':True,'PopulationPolicy':POLICY,'TypicalAudit':groups,
            'Policy':'Typical-minute labels: positive playing appearances in competitive games, excluding foul-threshold exits and starters without second-half minutes. Minute-44 players receive only subsequent off-court seconds through 48. Separate same-role earlier-date histories and states; training-only fitted parameters frozen in validation. Actuals, every represented roster row, exclusion reasons and adjustment evidence are preserved. These retrospective labels are never forecast-time inputs.',
            'TargetImplementationSha256':sha256(Path(__file__).read_bytes()).hexdigest(),
            'TargetDescription':'Expected typical regulation minutes conditional on playing, known role and competitive undisturbed deployment. Scores use filtered, minute-44-extended labels; actuals remain separate. Historical selection and adjustments are retrospective labels, never target-game features.'}

def load_cached_observations(reference_results):
    """Rebuild gates using hashed saved court intervals; reread current score/foul evidence.

    The cache is explicit, provenance-checked and does not reuse predictions or fits.
    """
    from train import digest, population_exclusion
    reference_results = Path(reference_results)
    reference_bytes = reference_results.read_bytes()
    reference = json.loads(reference_bytes)
    if not reference['Summary'].get('TypicalMinutes'):
        raise ValueError('A typical-minute construction cache is required')
    ledger_info = reference['Summary']['ParticipationLedger']
    ledger_path = Path(ledger_info['Path'])
    if digest(ledger_path) != ledger_info['Sha256']:
        raise ValueError('Changed participation ledger')
    ledger = json.loads(ledger_path.read_bytes())
    by_game = defaultdict(list)
    for row in ledger:
        by_game[row['GameId']].append(row)
    decoder = json.JSONDecoder()
    pattern = re.compile(r'\{\s*"Code"\s*:\s*"(?:SCORE\.OBSERVATION|FOUL\.PERSONAL_OBSERVATION)"')
    observations, audit, sources = [], [], []
    for old_source in reference['Summary']['Sources']:
        source = {k:v for k,v in old_source.items() if k not in ('Counts','TypicalGames')}
        counts, games = Counter(), []
        for j, old_game in enumerate(old_source['TypicalGames']):
            game_id = old_game['GameId']
            records = by_game[game_id]
            path = Path(records[0]['Source'])
            if digest(path) != old_game['GameSha256']:
                raise ValueError(f'Changed canonical game: {path}')
            game = json.loads(path.read_bytes())
            report_path = Path(old_game['Report'])
            report_bytes = report_path.read_bytes()
            if sha256(report_bytes).hexdigest() != old_game['ReportSha256']:
                raise ValueError(f'Changed diagnostic report: {report_path}')
            text = report_bytes.decode('utf-8-sig')
            checks = [decoder.raw_decode(text,m.start())[0] for m in pattern.finditer(text)]
            context = prepare_game(game, {'Validation':{'Checks':checks,'Stints':[]}})
            context['Stints'] = {r['PlayerId']:r['CourtIntervals'] for r in records}
            counts[f"TypicalGame:{context['GameExclusion'] or 'Eligible'}"] += 1
            games.append({**old_game,'Exclusion':context['GameExclusion'],'ScoreEvidence':context['ScoreEvidence']})
            for old in records:
                row = {k:v for k,v in old.items() if k not in ('IncludedInMinutesPopulation','Exclusion')}
                row['RegulationSeconds'] = row['ActualRegulationSeconds']
                row, target_exclusion = apply_target(row, context)
                exclusion = population_exclusion(row,row['ActualRegulationSeconds'],'played') or target_exclusion
                counts['RepresentedRows'] += 1
                counts[f"Eligibility:{row['Eligibility']}"] += 1
                counts[f"Availability:{row['Quality']}"] += 1
                if exclusion:
                    audit.append({**row,'Exclusion':exclusion})
                    counts[f'Excluded:{exclusion}'] += 1
                else:
                    observations.append(row)
            if (j+1)%250==0:
                print(f"{source['Season']}: refreshed {j+1}/{len(old_source['TypicalGames'])} hashed games",flush=True)
        source.update(Counts=dict(counts),TypicalGames=games,
                      CourtIntervalCache={'Results':str(reference_results.resolve()),
                                          'ResultsSha256':sha256(reference_bytes).hexdigest(),
                                          'LedgerSha256':ledger_info['Sha256']})
        sources.append(source)
        print(source['Season'],dict(counts),flush=True)
    catalog_bytes = (reference_results.parent/'catalog.json').read_bytes()
    if sha256(catalog_bytes).hexdigest()!=reference['Summary']['CatalogSha256']:
        raise ValueError('Changed cached catalog')
    return observations,audit,sources,catalog_bytes
