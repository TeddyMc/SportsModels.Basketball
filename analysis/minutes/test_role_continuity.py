import unittest
from role_continuity import prior_five_same_role


def observation(day, player=1, played=True, role=True, team=1, included=True):
    return {'RegistryId':'r','PlayerId':player,'TeamId':team,'GameId':f'g{team}-{day}',
            'Date':f'2025-01-{day:02d}','Started':role,'Played':played,
            'ActualRegulationSeconds':600 if played else 0,'IncludedInMinutesPopulation':included}


class ContinuityTests(unittest.TestCase):
    def test_requires_five_actual_team_games_without_skipping_absences(self):
        ledger=[observation(i) for i in range(1,7)]
        target={'Observation':observation(7)}
        self.assertTrue(prior_five_same_role([target],ledger)[0]['Included'])
        ledger[3]=observation(4,played=False)
        result=prior_five_same_role([target],ledger)[0]
        self.assertFalse(result['Included'])
        self.assertEqual(result['PreviousTeamGameIds'],[f'g1-{i}' for i in range(2,7)])

    def test_missing_player_record_is_not_skipped(self):
        ledger=[observation(i) for i in range(1,7) if i!=4]+[observation(4,player=2)]
        self.assertFalse(prior_five_same_role([{'Observation':observation(7)}],ledger)[0]['Included'])

    def test_role_change_and_unknown_role_fail_for_starters_and_bench(self):
        for role in (True,False):
            ledger=[observation(i,role=role) for i in range(1,6)]
            target={'Observation':observation(6,role=role)}
            self.assertTrue(prior_five_same_role([target],ledger)[0]['Included'])
            for other in (not role,None):
                ledger[2]['Started']=other
                self.assertFalse(prior_five_same_role([target],ledger)[0]['Included'])

    def test_same_day_and_future_games_cannot_supply_prior_history(self):
        ledger=[observation(i) for i in range(1,7)]
        member=prior_five_same_role([{'Observation':observation(5)}],ledger)[0]
        self.assertFalse(member['Included'])
        self.assertEqual(member['PreviousTeamGameIds'],[f'g1-{i}' for i in range(1,5)])

    def test_prior_game_need_not_pass_typical_training_gate(self):
        ledger=[observation(i,included=False) for i in range(1,6)]
        self.assertTrue(prior_five_same_role([{'Observation':observation(6)}],ledger)[0]['Included'])

    def test_previous_team_history_does_not_fill_current_team_schedule(self):
        ledger=[observation(i,team=2) for i in range(1,6)]
        ledger += [observation(i,player=2,team=1) for i in range(1,6)]
        self.assertFalse(prior_five_same_role([{'Observation':observation(6)}],ledger)[0]['Included'])

    def test_unknown_participation_fails_but_confirmed_played_with_unknown_total_counts(self):
        ledger=[observation(i) for i in range(1,6)]
        ledger[2]['ActualRegulationSeconds']=None
        target={'Observation':observation(6)}
        self.assertTrue(prior_five_same_role([target],ledger)[0]['Included'])
        ledger[2]['Played']=None
        self.assertFalse(prior_five_same_role([target],ledger)[0]['Included'])


if __name__=='__main__':unittest.main()
