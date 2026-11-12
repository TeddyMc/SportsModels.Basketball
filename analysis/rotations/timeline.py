"""Regulation-only rotation read model over unified basketball diagnostics."""
from collections import Counter
import re

PERIODS = {name: i + 1 for i, name in enumerate(
    ('FirstQuarter', 'SecondQuarter', 'ThirdQuarter', 'FourthQuarter'))}
# Same personal-charge classification as FoulDiagnostics.CountsPersonal.
PERSONAL = {'Personal', 'Shooting', 'Offensive', 'OffensiveCharge', 'LooseBall',
            'FlagrantOne', 'FlagrantTwo', 'ClearPath', 'AwayFromPlay',
            'TransitionTake', 'PersonalTake', 'DoublePersonal'}


def elapsed(event):
    clock = event['Clock']
    period = PERIODS.get(clock['Period'])
    if period is None:
        return None
    h, m, s = map(float, clock['ClockRemaining'].split(':'))
    return period * 720 - (h * 3600 + m * 60 + s)


def intervals(validation, team, player):
    """Merge primary established intervals; never substitute alternate reconstructions."""
    spans = sorted(((s['Period'] * 720 - s['StartRemaining'],
                     s['Period'] * 720 - s['EndRemaining'])
                    for s in validation.get('Stints', [])
                    if s['TeamId'] == team and s['PlayerId'] == player and 1 <= s['Period'] <= 4))
    merged = []
    for start, end in spans:
        if not 0 <= start <= end <= 2880:
            raise ValueError('Invalid regulation stint')
        if merged and start <= merged[-1][1]:
            merged[-1][1] = max(end, merged[-1][1])
        else:
            merged.append([start, end])
    return merged


def reconstruct(game, validation):
    """Sample after all events <= endpoint; membership is [start,end).

    At quarter-end use that quarter's closing membership, before next-period
    opening changes. Cumulative counters include events at the endpoint.
    """
    teams = [game['HomeTeam'], game['AwayTeam']]
    players = {(t['TeamId'], p['PlayerId']): p for t in teams for p in t['Players']}
    spans = {key: intervals(validation, *key) for key in players}
    minutes = {(m['TeamId'], m['PlayerId']): m for m in validation.get('PlayerMinutes', [])
               if m['Scope'] == 'Regulation'}
    checks = validation.get('Checks', [])
    score_anchors = {}
    for check in checks:
        if check['Code'] == 'SCORE.OBSERVATION':
            match = re.match(r'After-row observation home=(\d+), away=(\d+);', check['Evidence'])
            if match and check['Status'] != 'Fail':
                score_anchors[check['EventId']] = tuple(map(int, match.groups()))
    events = [e for e in game['Events'] if elapsed(e) is not None]
    chronological = all(elapsed(a) <= elapsed(b) for a, b in zip(events, events[1:]))
    events.sort(key=lambda e: (elapsed(e), e['SequenceNumber']))
    fouls, points = Counter(), Counter()
    home, away, score_known, foul_known = 0, 0, True, True
    unknown_points = set()
    all_points_known = True
    result = {key: [] for key in players}
    cursor = 0
    warnings = [] if chronological else ['Nonchronological events: cumulative values unavailable']
    for end in range(30, 2881, 30):
        quarter = (end - 1) // 720 + 1
        while cursor < len(events) and elapsed(events[cursor]) <= end:
            e = events[cursor]
            # A quarter boundary belongs to the closing quarter for this cell.
            if PERIODS[e['Clock']['Period']] > quarter:
                break
            cursor += 1
            kind = e['$event']
            if kind in ('FieldGoalEvent', 'FreeThrowEvent'):
                value = 1 if kind == 'FreeThrowEvent' else {'TwoPointer': 2, 'ThreePointer': 3}.get(e['FieldGoalType'])
                if e['Outcome'] not in ('Made', 'Missed') or (e['Outcome'] == 'Made' and value is None):
                    score_known = False
                    if e.get('ShooterPlayerId') is not None:
                        unknown_points.add((e['TeamId'], e['ShooterPlayerId']))
                    elif e.get('AttemptCredit') != 'Team':
                        all_points_known = False
                elif e['Outcome'] == 'Made':
                    player = e.get('ShooterPlayerId')
                    if player is not None:
                        points[(e['TeamId'], player)] += value
                    if e['TeamId'] == teams[0]['TeamId']:
                        home += value
                    elif e['TeamId'] == teams[1]['TeamId']:
                        away += value
                    else:
                        score_known = False
            if kind == 'FoulEvent':
                if e['FoulType'] in ('Other', 'NotSet'):
                    foul_known = False
                if e['FoulType'] in PERSONAL:
                    for actor in e['CommittingParticipants']:
                        if actor.get('PlayerId') is None:
                            foul_known = False
                        else:
                            fouls[(actor['TeamId'], actor['PlayerId'])] += 1
            if e['EventId'] in score_anchors:
                home, away = score_anchors[e['EventId']]
                score_known = True
        for key, player in players.items():
            team, _ = key
            minute = minutes.get(key, {})
            known = minute.get('Seconds') is not None and minute.get('Availability') not in ('Contradictory', 'Unestablished', 'Inactive')
            seconds = sum(max(0, min(end, b) - a) for a, b in spans[key] if a < end)
            on = any(a <= end < b for a, b in spans[key])
            if end % 720 == 0:
                on = any(a < end <= b for a, b in spans[key])
            pf = fouls[key] if foul_known and chronological else None
            pts = points[key] if all_points_known and key not in unknown_points and chronological else None
            result[key].append({'end': end, 'on': on if known or on else None,
                'minutes': seconds / 60 if known else None, 'fouls': pf,
                'trouble': sum(fouls[k] >= quarter + 1 for k in players if k[0] == team) if foul_known and chronological else None,
                'points': pts, 'ppm': pts / (seconds / 60) if pts is not None and known and seconds > 0 else None,
                'margin': (away - home if team == teams[0]['TeamId'] else home - away) if score_known and chronological else None})
    if not score_known or not foul_known or not all_points_known or unknown_points:
        warnings.append('Some cumulative statistics have incomplete event evidence')
    for key, minute in minutes.items():
        if key in spans and minute.get('Seconds') is not None:
            total = sum(b - a for a, b in spans[key])
            if abs(total - minute['Seconds']) > .01:
                warnings.append(f'Player {key[1]}: established intervals disagree with regulation total')
    return result, warnings
