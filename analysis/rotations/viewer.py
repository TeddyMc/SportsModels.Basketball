"""Prepare and serve local historical rotation pages; Python standard library only."""
import argparse
import csv
from contextlib import closing
from datetime import date, datetime
from hashlib import sha256
from html import escape
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import re
import sqlite3
from urllib.parse import parse_qs, urlencode, urlsplit

from timeline import reconstruct

VERSION = 1


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def digest(path):
    with Path(path).open('rb') as stream:
        return sha256(stream.read()).hexdigest()


def model_metadata(results=None, ledger=None):
    records = {}
    if ledger:
        for row in read_json(ledger):
            key = (str(row['GameId']), row['TeamId'], row['PlayerId'])
            records[key] = row
    if results:
        data = read_json(results)
        model = data['Models'][0]
        predictions = model['PredictionsMinutes']
        if len(predictions) != len(data['Rows']):
            raise ValueError('Model prediction/observation lengths differ')
        for wrapped, prediction in zip(data['Rows'], predictions):
            row = wrapped['Observation']
            key = (str(row['GameId']), row['TeamId'], row['PlayerId'])
            records[key] = {'IncludedInMinutesPopulation': True, **records.get(key, {}),
                            **row, 'PredictionMinutes': prediction}
    return records


def final_score(game, validation):
    """Use a recorded score only when no subsequent scoring row can change it."""
    observations = {c.get('EventId'): c for c in validation.get('Checks', [])
                    if c['Code'] == 'SCORE.OBSERVATION'}
    for event in reversed(game['Events']):
        observation = observations.get(event['EventId'])
        if observation is not None:
            match = re.match(r'After-row observation home=(\d+), away=(\d+);', observation['Evidence'])
            if match and observation['Status'] != 'Fail':
                return tuple(map(int, match.groups()))
        if event['$event'] in ('FieldGoalEvent', 'FreeThrowEvent') and event.get('Outcome') != 'Missed':
            return None
    return None


def prepare(catalog, database, results=None, ledger=None):
    selection = next(s for s in read_json(catalog)['Seasons'] if s['Season'] == '2025-26')
    run = Path(selection['RunDirectory'])
    manifest = read_json(run / 'manifest.json')
    if not manifest.get('Complete'):
        raise ValueError('Selected conversion is incomplete')
    dates = {}
    schedule = {}
    for item in manifest['Inputs']:
        if item['Endpoint'] == 'playbyplayv3':
            match = re.search(r'_(\d{8})_(\d{10})\.json$', item['OriginalPath'])
            if match:
                day, game = match.groups()
                dates[game] = datetime.strptime(day, '%Y%m%d').date().isoformat()
                names = re.search(r'([A-Z0-9]+)@([A-Z0-9]+)_\d{8}_\d{10}\.json$', item['OriginalPath'])
                if names:
                    schedule[game] = names.groups()
    metadata = model_metadata(results, ledger)
    database = Path(database)
    database.parent.mkdir(parents=True, exist_ok=True)
    if database.exists():
        raise ValueError('Use a new database path; prepared indexes are immutable')
    db = sqlite3.connect(database)
    try:
        db.executescript('''
            CREATE TABLE info (value TEXT);
            CREATE TABLE teams (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE players (id INTEGER, team INTEGER, name TEXT, PRIMARY KEY(id,team));
            CREATE TABLE games (id TEXT PRIMARY KEY, day TEXT, home INTEGER, away INTEGER, data TEXT);
            CREATE INDEX fixtures_home ON games(home,day);
            CREATE INDEX fixtures_away ON games(away,day);
            CREATE TABLE rotations (game TEXT, team INTEGER, player INTEGER, name TEXT, started INTEGER,
                actual REAL, data TEXT, PRIMARY KEY(game,team,player));
            CREATE INDEX histories ON rotations(team,player,game);
        ''')
        info = {'version': VERSION, 'season': '2025-26', 'run': str(run),
                'registry': manifest['RegistryId'], 'catalog_sha256': digest(catalog),
                'manifest_sha256': digest(run / 'manifest.json'),
                'model': str(results) if results else None, 'model_sha256': digest(results) if results else None,
                'ledger': str(ledger) if ledger else None, 'ledger_sha256': digest(ledger) if ledger else None,
                'scope': 'NBA regular season; historical completed games only', 'sources': []}
        with (run / 'games.csv').open(encoding='utf-8-sig', newline='') as stream:
            selected = [r for r in csv.DictReader(stream) if r['competition'] == 'NBA regular season (provider prefix)']
        for index, row in enumerate(selected):
            gid = row['game']
            day = dates.get(gid)
            if day is None:
                raise ValueError(f'Missing scheduled date provenance: {gid}')
            if day >= date.today().isoformat():
                continue
            matches = list((run / 'games').glob(gid + '-*.json'))
            if not matches:
                continue
            if len(matches) != 1:
                raise ValueError(f'Ambiguous canonical game: {gid}')
            path = matches[0]
            report_path = run / 'reports' / path.name
            game, report = read_json(path), read_json(report_path)
            info['sources'].append({'game': gid, 'canonical': path.name,
                'canonical_sha256': digest(path), 'report_sha256': digest(report_path)})
            validation = report.get('Validation') or {}
            cells, warnings = reconstruct(game, validation)
            failed = sum(c['Status'] == 'Fail' for c in validation.get('Checks', []))
            if failed:
                warnings.append(f'{failed} failed diagnostic checks; inspect the matching conversion report')
            teams = [game['HomeTeam'], game['AwayTeam']]
            minute_rows = {(m['TeamId'], m['PlayerId']): m for m in validation.get('PlayerMinutes', []) if m['Scope'] == 'Regulation'}
            score = final_score(game, validation)
            # A completed conversion is not necessarily a completed game.
            finished = any(e['$event'] == 'PeriodBoundaryEvent' and e.get('Marker') == 'End'
                           and e['Clock']['Period'] in ('FourthQuarter', 'Overtime')
                           and e['Clock']['ClockRemaining'] == '00:00:00' for e in game['Events'])
            if not finished:
                warnings.append('Incomplete PBP: game-end marker missing')
            game_data = {'warnings': list(dict.fromkeys(warnings)), 'status': report['Status'],
                         'score': score, 'complete': finished}
            db.execute('INSERT INTO games VALUES (?,?,?,?,?)', (gid, day, teams[0]['TeamId'], teams[1]['TeamId'], json.dumps(game_data)))
            for team in teams:
                tid = team['TeamId']
                if sum(p.get('Started') is True for p in team['Players']) != 5:
                    game_data['warnings'].append(f'{team["Name"]}: complete five-player starter set unavailable')
                db.execute('INSERT OR REPLACE INTO teams VALUES (?,?)', (tid, team['Name']))
                for player in team['Players']:
                    pid = player['PlayerId']
                    db.execute('INSERT OR REPLACE INTO players VALUES (?,?,?)', (pid, tid, player['Name']))
                    model = metadata.get((gid, tid, pid), {})
                    if model and model.get('RegistryId') != manifest['RegistryId']:
                        raise ValueError('Model and canonical identity registries differ')
                    minute = minute_rows.get((tid, pid), {})
                    actual = minute.get('Seconds')
                    reasons = model.get('TypicalExclusions') or []
                    if model.get('Exclusion') and model['Exclusion'] not in reasons:
                        reasons = [*reasons, model['Exclusion']]
                    status = ('Inactive' if player.get('Eligibility') == 'Inactive' else
                              'DNP' if player.get('Played') is False else
                              'Zero minutes' if actual == 0 else
                              'Played' if player.get('Played') is True else 'Participation unknown')
                    data = {'cells': cells[(tid, pid)], 'status': status,
                        'reason': ' · '.join(filter(None, [player.get('NotPlayingReason'), player.get('NotPlayingDescription')])),
                        'quality': minute.get('Availability', 'Missing minute data'),
                        'minute_reasons': minute.get('Reasons', []),
                        'adjusted': model.get('TypicalRegulationSeconds'),
                        'predicted': model.get('PredictionMinutes'),
                        'included': model.get('IncludedInMinutesPopulation'), 'exclusions': reasons,
                        'actual': actual, 'started': player.get('Started')}
                    db.execute('INSERT INTO rotations VALUES (?,?,?,?,?,?,?)',
                        (gid, tid, pid, player['Name'], player.get('Started'), actual, json.dumps(data)))
            if (index + 1) % 100 == 0:
                print(f'Prepared {index + 1}/{len(selected)} games', flush=True)
                db.commit()
            db.execute('UPDATE games SET data=? WHERE id=?', (json.dumps(game_data), gid))
        # Retain manifest-discovered completed fixtures even when canonical PBP is absent.
        known = {r[0] for r in db.execute('SELECT id FROM games')}
        names = {r[1]: r[0] for r in db.execute('SELECT * FROM teams')}
        for gid, pair in schedule.items():
            if not gid.startswith('00225') or gid in known or dates[gid] >= date.today().isoformat():
                continue
            away, home = pair
            if home not in names or away not in names:
                raise ValueError(f'Cannot resolve teams for missing game {gid}')
            db.execute('INSERT INTO games VALUES (?,?,?,?,?)', (gid, dates[gid], names[home], names[away],
                json.dumps({'warnings': ['Missing canonical PBP'], 'status': 'Missing', 'score': None, 'complete': False})))
        db.execute('INSERT INTO info VALUES (?)', (json.dumps(info),))
        db.commit()
        print(f'Prepared {db.execute("SELECT COUNT(*) FROM games").fetchone()[0]} games: {database}', flush=True)
    except BaseException:
        db.close()
        database.unlink(missing_ok=True)
        raise
    finally:
        db.close()


def link(label, **query):
    return f'<a href="/?{escape(urlencode(query), quote=True)}">{escape(str(label))}</a>'


def number(value):
    return 'N/A' if value is None else f'{value:.2f}'


def heatmap(cells):
    if not cells:
        cells = [{'end': end, **dict.fromkeys(('on', 'minutes', 'fouls', 'trouble', 'points', 'ppm', 'margin'))}
                 for end in range(30, 2881, 30)]
    html = []
    for i, c in enumerate(cells):
        end = c['end']
        quarter = (end - 1) // 720 + 1
        remaining = quarter * 720 - end
        state = 'On court' if c['on'] else 'Off court' if c['on'] is False else 'Unknown'
        title = (f'Elapsed {end//60}:{end%60:02d} · Q{quarter} {remaining//60}:{remaining%60:02d} remaining\n'
                 f'{state}\nCumulative minutes: {number(c["minutes"])}\nPersonal fouls: {number(c["fouls"])}'
                 f'\nTeam players in foul trouble: {number(c["trouble"])}\nPoints: {number(c["points"])}'
                 f'\nPoints per minute played: {number(c["ppm"])}\nScore differential (+ trailing): {number(c["margin"])}')
        cls = 'on' if c['on'] else 'off' if c['on'] is False else 'unknown'
        html.append(f'<span tabindex="0" class="cell {cls} {"quarter" if i in (24,48,72) else ""}" title="{escape(title, quote=True)}" aria-label="{escape(title, quote=True)}"></span>')
    return '<div class="heatmap">' + ''.join(html) + '</div>'


def row_metadata(data):
    included = data.get('included')
    return (f'Actual {number(data.get("actual") / 60 if data.get("actual") is not None else None)} · '
            f'Adjusted {number(data.get("adjusted") / 60 if data.get("adjusted") is not None else None)} · '
            f'Expected {number(data.get("predicted"))} min · '
            f'{"Included" if included else "Excluded" if included is False else "Inclusion N/A"} · '
            + escape(' · '.join(data.get('exclusions', []))) + '<br>'
            + escape(' · '.join(filter(None, [data.get('status'), data.get('reason'), data.get('quality'),
                                             *data.get('minute_reasons', [])]))))


STYLE = '''
body{font:15px system-ui;background:#f5f6f8;color:#17233b;margin:28px}a{color:#225cc2}h1{font-size:27px}
nav{display:flex;gap:20px;margin-bottom:24px}.teams{display:grid;grid-template-columns:repeat(5,1fr);gap:16px}
.teams a,.panel{background:white;padding:18px;border-radius:8px}.row{display:grid;grid-template-columns:330px minmax(700px,1fr);gap:20px;border-bottom:1px solid #ddd;padding:12px 0}
.scroll{overflow-x:auto}.heatmap{display:grid;grid-template-columns:repeat(96,minmax(6px,1fr));height:36px;margin:6px 0}
.cell{position:relative}.on{background:#276bce}.off{background:#dce3ed}.unknown{background:repeating-linear-gradient(45deg,#aaa,#aaa 2px,#eee 2px,#eee 5px)}
.quarter{border-left:3px solid black}.cell:focus,.cell:hover{outline:2px solid #e07114;z-index:1}
.axis{display:flex;justify-content:space-between;margin-left:350px;min-width:700px;color:#45536a}.muted{font-size:12px;color:#526075;line-height:1.6}.warning{background:#fff0cc;padding:10px}
.empty{background:#eee;color:#555;padding:10px}.legend span{display:inline-block;width:14px;height:14px;margin:0 5px 0 15px}
'''


class Viewer:
    def __init__(self, database):
        self.database = str(database)

    def render(self, query):
        with closing(sqlite3.connect(f'file:{Path(self.database).as_posix()}?mode=ro', uri=True)) as db:
            db.row_factory = sqlite3.Row
            teams = {r['id']: r['name'] for r in db.execute('SELECT * FROM teams ORDER BY name')}
            team = int(query['team'][0]) if 'team' in query else None
            player = int(query['player'][0]) if 'player' in query else None
            gid = query.get('game', [None])[0]
            nav = link('All teams')
            if team is not None:
                if team not in teams:
                    raise ValueError('Unknown team')
                nav += link(teams[team] + ' directory', team=team)
            title, content = 'NBA rotations · 2025–26', ''
            if gid:
                game = db.execute('SELECT * FROM games WHERE id=?', (gid,)).fetchone()
                if game is None:
                    raise ValueError('Unknown game')
                gd = json.loads(game['data'])
                title = f'{game["day"]} · {teams[game["away"]]} @ {teams[game["home"]]}'
                content += ''.join('<p class="warning">' + escape(w) + '</p>' for w in gd['warnings'])
                for tid in (game['away'], game['home']):
                    content += '<h2>' + link(teams[tid], team=tid) + '</h2>' + axis()
                    roster = db.execute('SELECT * FROM rotations WHERE game=? AND team=? ORDER BY CASE started WHEN 1 THEN 0 WHEN 0 THEN 1 ELSE 2 END, actual DESC, name', (gid, tid)).fetchall()
                    if not roster:
                        content += '<p class="warning">Missing game roster and rotation data</p>'
                    for row in roster:
                        data = json.loads(row['data'])
                        role = 'Starter' if row['started'] == 1 else 'Bench' if row['started'] == 0 else 'Role unknown'
                        content += render_row(link(row['name'], team=tid, player=row['player']) + ' · ' + role, data)
            elif player is not None:
                if team is None:
                    raise ValueError('Player history requires a team context')
                name = db.execute('SELECT name FROM players WHERE id=? AND team=?', (player, team)).fetchone()
                if name is None:
                    raise ValueError('Unknown player/team association')
                title = f'{name["name"]} · {teams[team]} · 2025–26 regular season'
                content += axis()
                fixtures = db.execute('SELECT * FROM games WHERE home=? OR away=? ORDER BY day,id', (team, team)).fetchall()
                for game in fixtures:
                    row = db.execute('SELECT data FROM rotations WHERE game=? AND team=? AND player=?', (game['id'], team, player)).fetchone()
                    gd = json.loads(game['data'])
                    if row:
                        data = json.loads(row['data'])
                        association = teams[team]
                    else:
                        other = db.execute('SELECT team FROM rotations WHERE game=? AND player=?', (game['id'], player)).fetchone()
                        association = teams[other['team']] if other else 'N/A'
                        data = {'status': 'Missing PBP/roster data' if gd['status'] == 'Missing' else 'Not on recorded game roster (historical roster membership unknown)', 'cells': []}
                    opponent = game['away'] if game['home'] == team else game['home']
                    margin = None if gd['score'] is None else (gd['score'][1] - gd['score'][0]) * (1 if game['home'] == team else -1)
                    label = link(f'{game["day"]} {"vs" if game["home"] == team else "@"} {teams[opponent]}', game=game['id'], team=team)
                    label += f'<br><small>Regular season · Team at game: {escape(association)} · Final differential {number(margin)}</small>'
                    if not gd['complete']:
                        label += '<br><strong>Incomplete PBP</strong>'
                    if gd.get('warnings'):
                        label += '<br><small class="warning">' + escape(' · '.join(gd['warnings'])) + '</small>'
                    content += render_row(label, data)
            elif team is not None:
                title = f'{teams[team]} · player directory · 2025–26'
                content = '<p>All players on a recorded game roster, including traded players.</p><div class="teams">'
                content += ''.join(link(r['name'], team=team, player=r['id']) for r in db.execute('SELECT * FROM players WHERE team=? ORDER BY name', (team,))) + '</div>'
            else:
                content = '<p>Choose a team to explore player histories and game rotations.</p><div class="teams">'
                content += ''.join(link(name, team=tid) for tid, name in teams.items()) + '</div>'
            return ('<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">'
                    '<title>' + escape(title) + '</title><style>' + STYLE + '</style><body><nav>' + nav + '</nav><h1>' + escape(title) + '</h1>'
                    '<p class="legend"><span class="on"></span>On court <span class="off"></span>Off court <span class="unknown"></span>Unknown</p>'
                    '<p class="muted">Regulation only · 30-second cells · Hover or focus a cell for details. State is sampled after events at each interval end; quarter-end cells show closing-quarter membership. Positive differential means trailing. Model metadata is N/A unless supplied.</p>'
                    '<main class="scroll">' + content + '</main></body></html>')


def axis():
    return '<div class="axis"><span>0</span><span>12</span><span>24</span><span>36</span><span>48</span></div>'


def render_row(label, data):
    return '<div class="row"><div>' + label + '<div class="muted">' + row_metadata(data) + '</div></div><div>' + heatmap(data.get('cells')) + '</div></div>'


def serve(database, port):
    with closing(sqlite3.connect(f'file:{Path(database).as_posix()}?mode=ro', uri=True)) as db:
        if db.execute('SELECT COUNT(*) FROM info').fetchone()[0] != 1:
            raise ValueError('Preparation has not completed; inspect its progress/error log')
    viewer = Viewer(database)
    class Handler(BaseHTTPRequestHandler):
        timeout = 10

        def do_GET(self):
            try:
                url = urlsplit(self.path)
                if url.path != '/':
                    self.send_error(404)
                    return
                body = viewer.render(parse_qs(url.query)).encode('utf-8')
                self.send_response(200)
                self.send_header('Content-Type', 'text/html; charset=utf-8')
                self.send_header('Content-Length', str(len(body)))
                self.end_headers()
                self.wfile.write(body)
            except (ValueError, KeyError):
                self.send_error(404, 'Unknown rotation selection')
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass  # A closed browser tab must not interrupt other requests.
    server = ThreadingHTTPServer(('127.0.0.1', port), Handler)
    print(f'Rotation viewer: http://127.0.0.1:{port}', flush=True)
    server.serve_forever()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    build = sub.add_parser('prepare')
    build.add_argument('--catalog', type=Path, default=Path('C:/Data/Basketball/converted/catalog.json'))
    build.add_argument('--database', type=Path, required=True)
    build.add_argument('--results', type=Path)
    build.add_argument('--ledger', type=Path)
    host = sub.add_parser('serve')
    host.add_argument('--database', type=Path, required=True)
    host.add_argument('--port', type=int, default=8727)
    args = parser.parse_args()
    if args.command == 'prepare':
        prepare(args.catalog, args.database, args.results, args.ledger)
    else:
        if not args.database.is_file():
            parser.error('Prepare the database first')
        serve(args.database, args.port)


if __name__ == '__main__':
    main()
