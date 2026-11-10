import unittest
from typical import competitive_gate, apply_target, prepare_game


def event(kind, q=4, remaining=240, **fields):
    return {'$event':kind,'EventId':fields.pop('EventId',1),
            'Clock':{'Period':['FirstQuarter','SecondQuarter','ThirdQuarter','FourthQuarter'][q-1],
                     'ClockRemaining':f'00:{int(remaining)//60:02}:{remaining%60:05.2f}'},**fields}


def game(events):
    return {'HomeTeam':{'TeamId':1,'Players':[{'PlayerId':1}]},
            'AwayTeam':{'TeamId':2,'Players':[]},'Events':events}

CHECK = {'Code':'SCORE.OBSERVATION','Status':'Pass'}


def score_checks(g, status='Pass'):
    home=away=0
    checks=[]
    for i,e in enumerate(g['Events']):
        e['EventId']=i+100
        if e['$event'] in ('FieldGoalEvent','FreeThrowEvent') and e['Outcome']=='Made':
            points=1 if e['$event']=='FreeThrowEvent' else {'TwoPointer':2,'ThreePointer':3}[e['FieldGoalType']]
            if e['TeamId']==1:home+=points
            else:away+=points
        checks.append({'Code':'SCORE.OBSERVATION','Status':status,'EventId':e['EventId'],
                       'Evidence':f'After-row observation home={home}, away={away}; effective stream home={home}, away={away}.'})
    return checks


class TypicalTests(unittest.TestCase):
    def test_threshold_equality_and_before_after_sampling(self):
        shots = [event('FieldGoalEvent',1,700-i,Outcome='Made',FieldGoalType='ThreePointer',TeamId=1) for i in range(5)]
        g = game(shots+[event('PeriodBoundaryEvent',remaining=600),event('PeriodBoundaryEvent')])
        self.assertIsNone(competitive_gate(g,score_checks(g))[0])  # 15 at 44 is allowed.
        g['Events'].insert(-1,event('FreeThrowEvent',remaining=241,Outcome='Made',TeamId=1))
        self.assertEqual(competitive_gate(g,score_checks(g))[0],'Noncompetitive game')

    def test_missing_score_evidence(self):
        self.assertIn('score evidence',competitive_gate(game([]),[])[0])
        self.assertIn('score evidence',competitive_gate(game([event('PeriodBoundaryEvent')]),[])[0])

    def test_later_score_uncertainty_does_not_change_gate(self):
        g=game([event('PeriodBoundaryEvent',1,720),event('PeriodBoundaryEvent'),event('PeriodBoundaryEvent',remaining=120)])
        checks=score_checks(g)
        checks[-1]['Status']='Fail'
        self.assertIsNone(competitive_gate(g,checks)[0])

    def test_observed_scores_survive_unknown_earlier_heave(self):
        g=game([event('PeriodBoundaryEvent',1,720),
                event('FieldGoalEvent',2,0,Outcome='NotSet',FieldGoalType='NotSet',TeamId=1),
                event('PeriodBoundaryEvent',4,600),event('PeriodBoundaryEvent')])
        checks=score_checks(g,status='NotAssessable')
        self.assertIsNone(competitive_gate(g,checks)[0])
        checks[-1]['Status']='Fail'
        self.assertEqual(competitive_gate(g,checks)[0],'Contradictory score evidence')

    def test_missing_observation_at_unknown_scoring_event_is_not_guessed(self):
        g=game([event('PeriodBoundaryEvent',1,720),
                event('FieldGoalEvent',4,500,Outcome='NotSet',FieldGoalType='NotSet',TeamId=1),
                event('PeriodBoundaryEvent')])
        checks=score_checks(g)
        checks.pop(1)
        self.assertEqual(competitive_gate(g,checks)[0],'Missing score evidence')

    def test_margin_carried_to_end_of_interval(self):
        shots = [event('FieldGoalEvent',1,700-i,Outcome='Made',FieldGoalType='ThreePointer',TeamId=1) for i in range(6)]
        g=game(shots+[event('PeriodBoundaryEvent',remaining=600),event('PeriodBoundaryEvent',remaining=200)])
        reason,_ = competitive_gate(g,score_checks(g))
        self.assertEqual(reason,'Noncompetitive game')

    def test_extension_credits_only_off_court_time(self):
        context={'GameExclusion':None,'Stints':{1:[(0,1200),(2400,2700),(2820,2880)]},'UnknownFouls':set(),'FoulExits':{}}
        row={'PlayerId':1,'Started':True,'RegulationSeconds':1560}
        result,reason=apply_target(row,context)
        self.assertIsNone(reason)
        self.assertEqual(result['ActualRegulationSeconds'],1560)
        self.assertEqual(result['LateExtensionSeconds'],120)
        self.assertEqual(result['RegulationSeconds'],1680)
        context['Stints'][1]=[(0,1200),(2400,2880)]
        row['RegulationSeconds']=1680
        self.assertEqual(apply_target(row,context)[0]['LateExtensionSeconds'],0)

    def test_boundary_out_is_off_in_is_on(self):
        context={'GameExclusion':None,'Stints':{1:[(2400,2640)]},'UnknownFouls':set(),'FoulExits':{}}
        row={'PlayerId':1,'Started':False,'RegulationSeconds':240}
        self.assertFalse(apply_target(row,context)[0]['OnCourtAt44'])
        context['Stints'][1]=[(2640,2700)]
        row['RegulationSeconds']=60
        self.assertTrue(apply_target(row,context)[0]['OnCourtAt44'])

    def test_starter_proxy_and_missing_court_evidence(self):
        context={'GameExclusion':None,'Stints':{1:[(0,600)]},'UnknownFouls':set(),'FoulExits':{}}
        row={'PlayerId':1,'Started':True,'RegulationSeconds':600}
        self.assertIn('Starter without second-half minutes',apply_target(row,context)[0]['TypicalExclusions'])
        row['Started']=False
        self.assertIsNone(apply_target(row,context)[1])
        row['RegulationSeconds']=601
        self.assertIn('court evidence',apply_target(row,context)[1])

    def test_reaching_threshold_and_exit_order(self):
        foul=lambda i:event('FoulEvent',1,600,EventId=i,FoulType='Personal',CommittingParticipants=[{'PlayerId':1}])
        sub=event('SubstitutionEvent',1,600,EventId=3,PlayerOutId=1)
        report={'Validation':{'Checks':[CHECK],'Stints':[]}}
        for events,excluded in (([foul(1),foul(2),sub],True),([sub,foul(1),foul(2)],False)):
            context=prepare_game(game(events+[event('PeriodBoundaryEvent')]),report)
            self.assertEqual(1 in context['FoulExits'],excluded)
        sub['Clock']['ClockRemaining']='00:00:00'
        self.assertFalse(prepare_game(game([foul(1),foul(2),sub,event('PeriodBoundaryEvent')]),report)['FoulExits'])

    def test_all_quarter_thresholds_technical_fouls_and_carryover(self):
        report={'Validation':{'Checks':[CHECK],'Stints':[]}}
        for quarter in range(1,5):
            fouls=[event('FoulEvent',quarter,600,EventId=i,FoulType='Personal',
                         CommittingParticipants=[{'PlayerId':1}]) for i in range(quarter+1)]
            sub=event('SubstitutionEvent',quarter,590,PlayerOutId=1)
            self.assertIn(1,prepare_game(game(fouls+[sub,event('PeriodBoundaryEvent')]),report)['FoulExits'])
            fouls[-1]['FoulType']='Technical'
            self.assertNotIn(1,prepare_game(game(fouls+[sub,event('PeriodBoundaryEvent')]),report)['FoulExits'])

    def test_adjacent_stints_do_not_double_credit_extension(self):
        from typical import merge_intervals
        self.assertEqual(merge_intervals([(2640,2700),(2700,2880)]),[(2640,2880)])

    def test_ejection_after_threshold_is_an_exit(self):
        events=[event('FoulEvent',1,600,EventId=i,FoulType='Personal',
                      CommittingParticipants=[{'PlayerId':1}]) for i in (1,2)]
        events += [event('EjectionEvent',1,590,EventId=3,PlayerId=1),event('PeriodBoundaryEvent')]
        result=prepare_game(game(events),{'Validation':{'Checks':[CHECK],'Stints':[]}})
        self.assertEqual(result['FoulExits'][1]['ExitEventId'],3)

    def test_unknown_personal_counts_are_excluded(self):
        context={'GameExclusion':None,'Stints':{1:[(2400,2700)]},'UnknownFouls':{1},'FoulExits':{}}
        self.assertEqual(apply_target({'PlayerId':1,'Started':False,'RegulationSeconds':300},context)[1],
                         'Unknown foul evidence')

    def test_unknown_foul_without_identified_actor_does_not_silently_pass(self):
        events=[event('FoulEvent',1,600,FoulType='Other',CommittingParticipants=[]),
                event('PeriodBoundaryEvent')]
        result=prepare_game(game(events),{'Validation':{'Checks':[],'Stints':[]}})
        self.assertIn(1,result['UnknownFouls'])

    def test_adjusted_labels_drive_history_and_scoring(self):
        import numpy as np
        from train import build_history, metrics
        from test_train import row
        context={'GameExclusion':None,'Stints':{1:[(2400,2700)]},'UnknownFouls':set(),'FoulExits':{}}
        first,reason=apply_target(row(0,5,False),context)
        self.assertIsNone(reason)
        second,reason=apply_target(row(1,5,False),context)
        rows,history=build_history([first,second])
        self.assertEqual(history[1,0],8)
        self.assertEqual(metrics(rows,np.array([8,8]))['RmseMinutes'],0)
        self.assertEqual(first['ActualRegulationSeconds'],300)


if __name__=='__main__':
    unittest.main()
