import unittest
from calibration_review import calibration_points


def row(player,minutes):
    return {'Observation':{'RegistryId':'r','PlayerId':player,'RegulationSeconds':minutes*60}}


class CalibrationIntervalTests(unittest.TestCase):
    def test_repeated_games_from_one_player_do_not_create_an_interval(self):
        points=calibration_points([row(1,10),row(1,20)],[15.1,15.9],[0,1])
        self.assertEqual(points[0]['N'],2)
        self.assertEqual(points[0]['Players'],1)
        self.assertIsNone(points[0]['CI95Low'])
        self.assertEqual(points[0]['TargetMean'],15)

    def test_resampling_players_preserves_their_games_as_a_cluster(self):
        rows=[row(1,10)]*5+[row(2,20)]*5
        p=calibration_points(rows,[15.5]*10,list(range(10)))[0]
        self.assertEqual((p['CI95Low'],p['CI95High']),(10,20))
        self.assertEqual(p['N'],10)
        self.assertEqual(p['Players'],2)

    def test_points_remain_game_weighted_and_bootstrap_is_reproducible(self):
        rows=[row(1,10),row(1,10),row(2,40)]
        args=(rows,[10.1,10.2,10.3],[0,1,2])
        p=calibration_points(*args)[0]
        self.assertEqual(p['TargetMean'],20)
        self.assertAlmostEqual(p['PredictionMean'],10.2)
        self.assertEqual(calibration_points(*args),calibration_points(*args))
        self.assertLessEqual(0,p['CI95Low'])
        self.assertLessEqual(p['CI95High'],48)

    def test_only_requested_indices_enter_buckets(self):
        p=calibration_points([row(1,5),row(2,15),row(3,47)],[10.1,10.2,10.3],[0,1])[0]
        self.assertEqual(p['TargetMean'],10)
        self.assertEqual(p['N'],2)


if __name__=='__main__':unittest.main()
