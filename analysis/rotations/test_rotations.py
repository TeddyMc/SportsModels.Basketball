import json
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import unittest
from urllib.parse import parse_qs

from timeline import reconstruct
from viewer import Viewer, heatmap, model_metadata, prepare, final_score


def event(kind, seconds, sequence, quarter=1, **fields):
    remaining = quarter * 720 - seconds
    return {'$event': kind, 'EventId': sequence, 'SequenceNumber': sequence,
            'Clock': {'Period': ['FirstQuarter', 'SecondQuarter', 'ThirdQuarter', 'FourthQuarter'][quarter-1],
                      'ClockRemaining': f'00:{int(remaining)//60:02d}:{remaining%60:02.0f}'}, **fields}


def fixture():
    game = {'HomeTeam': {'TeamId': 1, 'Players': [{'PlayerId': 10}]},
            'AwayTeam': {'TeamId': 2, 'Players': [{'PlayerId': 20}]}, 'Events': []}
    validation = {'Checks': [], 'Stints': [{'Period': 1, 'TeamId': 1, 'PlayerId': 10,
                                          'StartRemaining': 720, 'EndRemaining': 660}],
                  'PlayerMinutes': [{'Scope': 'Regulation', 'TeamId': 1, 'PlayerId': 10,
                                     'Seconds': 60, 'Availability': 'PlausibleUncorroborated'}]}
    return game, validation


class TimelineTests(unittest.TestCase):
    def test_exact_boundaries_and_cumulative_minutes(self):
        game, validation = fixture()
        rows, _ = reconstruct(game, validation)
        cells = rows[(1, 10)]
        self.assertEqual(96, len(cells))
        self.assertTrue(cells[0]['on'])
        self.assertFalse(cells[1]['on'])
        self.assertEqual([.5, 1, 1], [c['minutes'] for c in cells[:3]])
        self.assertEqual(0, cells[0]['ppm'])

    def test_scoring_rate_and_opposing_margin_sign_after_exact_event(self):
        game, validation = fixture()
        game['Events'] = [event('FieldGoalEvent', 30, 1, TeamId=1, ShooterPlayerId=10,
                                FieldGoalType='ThreePointer', Outcome='Made')]
        rows, _ = reconstruct(game, validation)
        self.assertEqual(3, rows[(1, 10)][0]['points'])
        self.assertEqual(6, rows[(1, 10)][0]['ppm'])
        self.assertEqual(-3, rows[(1, 10)][0]['margin'])
        self.assertEqual(3, rows[(2, 20)][0]['margin'])

    def test_q_plus_one_and_technical_fouls(self):
        game, validation = fixture()
        game['Events'] = [event('FoulEvent', 10, 1, FoulType='Personal', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}]),
                          event('FoulEvent', 30, 2, FoulType='Shooting', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}]),
                          event('FoulEvent', 40, 3, FoulType='Technical', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}])]
        rows, _ = reconstruct(game, validation)
        self.assertEqual(2, rows[(1, 10)][1]['fouls'])
        self.assertEqual(1, rows[(1, 10)][0]['trouble'])
        self.assertEqual(0, rows[(1, 10)][24]['trouble'])

    def test_quarter_closing_membership_and_next_period_opening(self):
        game, validation = fixture()
        validation['Stints'][0]['EndRemaining'] = 0
        validation['PlayerMinutes'][0]['Seconds'] = 720
        game['Events'] = [event('FoulEvent', 720, 1, FoulType='Personal', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}]),
                          event('FoulEvent', 720, 2, quarter=2, FoulType='Personal', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}])]
        rows, _ = reconstruct(game, validation)
        self.assertTrue(rows[(1, 10)][23]['on'])
        self.assertEqual(1, rows[(1, 10)][23]['fouls'])
        self.assertFalse(rows[(1, 10)][24]['on'])
        self.assertEqual(2, rows[(1, 10)][24]['fouls'])

    def test_missing_minutes_not_zero_and_unknown_scoring(self):
        game, validation = fixture()
        game['Events'] = [event('FieldGoalEvent', 0, 1, TeamId=1, ShooterPlayerId=10,
                                FieldGoalType='NotSet', Outcome='NotSet')]
        rows, warnings = reconstruct(game, validation)
        self.assertIsNone(rows[(2, 20)][0]['minutes'])
        self.assertIsNone(rows[(2, 20)][0]['on'])
        self.assertIsNone(rows[(1, 10)][0]['points'])
        self.assertIsNone(rows[(1, 10)][0]['margin'])
        self.assertTrue(warnings)

    def test_stint_disagreement_is_flagged_and_overlaps_merged(self):
        game, validation = fixture()
        validation['Stints'].append(dict(validation['Stints'][0]))
        rows, warnings = reconstruct(game, validation)
        self.assertEqual(1, rows[(1, 10)][1]['minutes'])
        self.assertFalse(warnings)
        validation['PlayerMinutes'][0]['Seconds'] = 100
        self.assertTrue(reconstruct(game, validation)[1])

    def test_overtime_ignored_and_html_cells(self):
        game, validation = fixture()
        game['Events'] = [{'Clock': {'Period': 'Overtime'}}]
        rows, _ = reconstruct(game, validation)
        html = heatmap(rows[(1, 10)])
        self.assertEqual(96, html.count('tabindex="0"'))
        self.assertEqual(3, html.count(' quarter"'))
        missing = heatmap([])
        self.assertEqual(96, missing.count('tabindex="0"'))
        self.assertEqual(96, missing.count('cell unknown'))

    def test_unknown_player_credit_does_not_erase_other_players(self):
        game, validation = fixture()
        game['Events'] = [event('FieldGoalEvent', 30, 1, TeamId=1, ShooterPlayerId=10,
                                FieldGoalType='NotSet', Outcome='NotSet')]
        rows, _ = reconstruct(game, validation)
        self.assertIsNone(rows[(1, 10)][0]['points'])
        self.assertEqual(0, rows[(2, 20)][0]['points'])

    def test_final_score_does_not_use_stale_anchor(self):
        game, validation = fixture()
        game['Events'] = [event('PeriodBoundaryEvent', 0, 1, Marker='Start'),
                          event('FieldGoalEvent', 30, 2, TeamId=1, ShooterPlayerId=10,
                                FieldGoalType='ThreePointer', Outcome='Made')]
        validation['Checks'] = [{'Code': 'SCORE.OBSERVATION', 'Status': 'Pass', 'EventId': 1,
                                  'Evidence': 'After-row observation home=0, away=0; effective stream known'}]
        self.assertIsNone(final_score(game, validation))
        validation['Checks'].append({'Code': 'SCORE.OBSERVATION', 'Status': 'Pass', 'EventId': 2,
                                    'Evidence': 'After-row observation home=3, away=0; effective stream known'})
        self.assertEqual((3, 0), final_score(game, validation))

    def test_all_quarter_foul_thresholds(self):
        for quarter in range(1, 5):
            game, validation = fixture()
            game['Events'] = [event('FoulEvent', (quarter-1)*720+10, i, quarter=quarter,
                                   FoulType='Personal', CommittingParticipants=[{'TeamId': 1, 'PlayerId': 10}])
                              for i in range(quarter+1)]
            rows, _ = reconstruct(game, validation)
            self.assertEqual(1, rows[(1, 10)][(quarter-1)*24]['trouble'])


class ViewTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = Path(self.temp.name) / 'test.sqlite'
        with closing(sqlite3.connect(self.path)) as db, db:
            db.executescript('''CREATE TABLE teams(id,name); CREATE TABLE players(id,team,name);
                CREATE TABLE games(id,day,home,away,data);
                CREATE TABLE rotations(game,team,player,name,started,actual,data);''')
            db.executemany('INSERT INTO teams VALUES (?,?)', [(1, 'AAA'), (2, 'BBB')])
            db.executemany('INSERT INTO players VALUES (?,?,?)', [(10, 1, 'Traded <Player>'), (10, 2, 'Traded <Player>')])
            gd = json.dumps({'warnings': [], 'complete': True, 'score': [100, 90], 'status': 'Converted'})
            db.executemany('INSERT INTO games VALUES (?,?,?,?,?)', [('late', '2026-02-02', 1, 2, gd), ('early', '2026-01-01', 1, 2, gd)])
            data = json.dumps({'status': 'DNP', 'actual': 0, 'cells': [], 'exclusions': ['Foul trouble', 'Noncompetitive game']})
            db.executemany('INSERT INTO rotations VALUES (?,?,?,?,?,?,?)', [
                ('late', 2, 10, 'Traded <Player>', 0, 0, data),
                ('early', 1, 10, 'Traded <Player>', 1, 0, data),
                ('early', 1, 11, 'Bench scorer', 0, 2400, data)])
        self.viewer = Viewer(self.path)

    def tearDown(self):
        self.temp.cleanup()

    def test_navigation_trade_directory_and_roster_gaps(self):
        self.assertIn('team=1', self.viewer.render({}))
        for tid in (1, 2):
            self.assertIn('player=10', self.viewer.render(parse_qs(f'team={tid}')))
        history = self.viewer.render(parse_qs('team=1&player=10'))
        self.assertLess(history.index('2026-01-01'), history.index('2026-02-02'))
        self.assertIn('Not on recorded game roster', history)
        self.assertIn('Team at game: BBB', history)
        self.assertIn('DNP', history)
        self.assertIn('Foul trouble · Noncompetitive game', history)
        self.assertIn('Traded &lt;Player&gt;', history)
        self.assertIn('game=early', history)

    def test_starters_precede_higher_minute_bench_and_links_back(self):
        page = self.viewer.render(parse_qs('game=early'))
        self.assertLess(page.index('Traded &lt;Player&gt;'), page.index('Bench scorer'))
        self.assertIn('team=1&amp;player=10', page)
        self.assertIn('Expected N/A', page)

    def test_directory_orders_positive_appearance_average_and_rates(self):
        with closing(sqlite3.connect(self.path)) as db, db:
            db.execute('INSERT INTO players VALUES (11,1,?)', ('Bench scorer',))
            cells = [{'on': True} for _ in range(96)]
            db.execute('UPDATE rotations SET actual=600,data=? WHERE team=1 AND player=10', (json.dumps({'cells': cells}),))
            db.execute('INSERT INTO rotations VALUES (?,?,?,?,?,?,?)', ('late', 1, 10, 'Traded <Player>', 0, 0, json.dumps({'cells': []})))
            db.execute('UPDATE rotations SET data=? WHERE player=11', (json.dumps({'cells': [{'on': False} for _ in range(96)]}),))
        page = self.viewer.render(parse_qs('team=1'))
        self.assertLess(page.index('Bench scorer'), page.index('Traded &lt;Player&gt;'))
        self.assertIn('10.00 min/game played · 1 positive-minute games', page)
        self.assertIn('100.0% (1/1 known appearances)', page)
        self.assertIn('0.0% (0/1 known appearances)', page)
        self.assertEqual(192, page.count('tabindex="0"'))

    def test_model_adapter_joins_predictions_and_retains_multiple_reasons(self):
        ledger = Path(self.temp.name) / 'ledger.json'
        results = Path(self.temp.name) / 'results.json'
        row = {'GameId': 'g', 'TeamId': 1, 'PlayerId': 10, 'TypicalExclusions': ['A', 'B'], 'IncludedInMinutesPopulation': False}
        ledger.write_text(json.dumps([row]))
        results.write_text(json.dumps({'Rows': [{'Observation': row}], 'Models': [{'PredictionsMinutes': [30]}]}))
        value = model_metadata(results, ledger)[('g', 1, 10)]
        self.assertEqual(30, value['PredictionMinutes'])
        self.assertEqual(['A', 'B'], value['TypicalExclusions'])

    def test_prepare_retains_missing_fixture_and_omits_future(self):
        root = Path(self.temp.name) / 'run'
        (root / 'games').mkdir(parents=True)
        (root / 'reports').mkdir()
        game, validation = fixture()
        game['HomeTeam']['Name'], game['AwayTeam']['Name'] = 'AAA', 'BBB'
        game['HomeTeam']['Players'][0].update(Name='Player', Started=True, Played=True)
        game['AwayTeam']['Players'][0].update(Name='Other', Started=False, Played=False)
        game['Events'] = [event('PeriodBoundaryEvent', 2880, 1, quarter=4, Marker='End')]
        manifest = {'Complete': True, 'RegistryId': 'registry', 'Inputs': [
            {'Endpoint': 'playbyplayv3', 'OriginalPath': f'BBB@AAA_{day}_{gid}.json'}
            for day, gid in [('20251021', '0022500001'), ('20251022', '0022500002'), ('20991022', '0022500003')]]}
        (root / 'manifest.json').write_text(json.dumps(manifest))
        (root / 'games.csv').write_text('game,competition\n0022500001,NBA regular season (provider prefix)\n0022500002,NBA regular season (provider prefix)\n')
        (root / 'games' / '0022500001-hash.json').write_text(json.dumps(game))
        (root / 'reports' / '0022500001-hash.json').write_text(json.dumps({'Validation': validation, 'Status': 'Partial'}))
        catalog = root / 'catalog.json'
        catalog.write_text(json.dumps({'Seasons': [{'Season': '2025-26', 'RunDirectory': str(root)}]}))
        output = root / 'prepared.sqlite'
        prepare(catalog, output)
        view = Viewer(output)
        history = view.render(parse_qs('team=1&player=10'))
        self.assertIn('Missing PBP/roster data', history)
        self.assertIn('2025-10-22', history)
        self.assertNotIn('2099', history)
        self.assertIn('Missing game roster', view.render(parse_qs('game=0022500002')))
        with self.assertRaisesRegex(ValueError, 'immutable'):
            prepare(catalog, output)


if __name__ == '__main__':
    unittest.main()
