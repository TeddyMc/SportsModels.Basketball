import unittest

import numpy as np
from scipy.optimize import approx_fprime

from bayesian import filter_rows, likelihood, prepare_training
from effects import UncertaintyDynamics, get_dynamics
from likelihoods import gaussian_log_scores
from study import validate
from test_train import row
from train import population_exclusion


def roles():
    return {name: {"PriorMeanMinutes": mean, "PriorVarianceMinutesSquared": 10.,
                   "DailyDriftVariance": 1., "ObservationVarianceMinutesSquared": 20.,
                   "VarianceCapMinutesSquared": 30.} for name, mean in [("Starter", 20.), ("Bench", 5.)]}


class BayesianTests(unittest.TestCase):
    def test_conditional_nonappearance_neither_updates_nor_resets_elapsed_gap(self):
        observations = [{**row(day, minutes), "Eligibility": "Active", "Played": minutes > 0}
                        for day, minutes in [(0, 10), (2, 0), (5, 30)]]
        rows = [{"Observation": o} for o in observations
                if population_exclusion(o, o["RegulationSeconds"], "played") is None]
        output = filter_rows(rows, roles())
        self.assertEqual(output["HistoryCounts"], [0, 1])
        self.assertAlmostEqual(output["PredictionsMinutes"][1], output["PosteriorMeansMinutes"][0])
        self.assertAlmostEqual(output["LatentVarianceMinutesSquared"][1],
                               output["PosteriorVariancesMinutesSquared"][0] + 5)
        prepared = prepare_training(rows, True)
        self.assertEqual(int(prepared[1].sum()), 2)

    def test_cold_start_uses_matching_prior_and_roles_do_not_borrow(self):
        rows = [{"Observation": row(0, 40, True)}, {"Observation": row(1, 10, False)},
                {"Observation": row(2, 10, None)}]
        output = filter_rows(rows, roles())
        self.assertEqual(output["PredictionsMinutes"][:2], [20., 5.])
        self.assertIsNone(output["PredictionsMinutes"][2])
        self.assertEqual(output["HistoryCounts"][:2], [0, 0])

    def test_update_and_long_gap_hit_fitted_cap_not_initial_variance(self):
        rows = [{"Observation": row(0, 10)}, {"Observation": row(200, None)}]
        output = filter_rows(rows, roles())
        self.assertAlmostEqual(output["PosteriorMeansMinutes"][0], 20-10/3)
        self.assertAlmostEqual(output["PredictionsMinutes"][1], 20-10/3)
        self.assertEqual(output["LatentVarianceMinutesSquared"][1], 30)
        self.assertEqual(output["PredictiveVarianceMinutesSquared"][1], 50)
        self.assertEqual(output["HistoryCounts"][1], 1)

    def test_same_day_predictions_precede_joint_update_and_zero_is_observed(self):
        rows = [{"Observation": row(0, 0, game="a")}, {"Observation": row(0, 40, game="b")},
                {"Observation": row(1, 10)}]
        output = filter_rows(rows, roles())
        self.assertEqual(output["PredictionsMinutes"][:2], [20., 20.])
        self.assertEqual(output["PredictionsMinutes"][2], 20.)
        self.assertEqual(output["HistoryCounts"][2], 2)

    def test_unknown_actual_does_not_update_and_future_cannot_change_past(self):
        rows = [{"Observation": row(0, None)}, {"Observation": row(1, 20)},
                {"Observation": row(2, 48, split="Validation")}]
        output = filter_rows(rows, roles())
        self.assertEqual(output["HistoryCounts"][:2], [0, 0])
        rows[2]["Observation"]["RegulationSeconds"] = 0
        changed = filter_rows(rows, roles())
        self.assertEqual(output["PredictionsMinutes"], changed["PredictionsMinutes"])

    def test_likelihood_gradient_matches_finite_differences_with_active_cap(self):
        rows = [{"Observation": row(0, 10)}, {"Observation": row(0, 20, game="other")},
                {"Observation": row(2, 30)}, {"Observation": row(200, 25)},
                {"Observation": row(201, 48, split="Validation")}]
        prepared = prepare_training(rows, True)
        self.assertEqual(prepared[1].sum(), 4)
        parameters = np.r_[20., np.log([8., 15., .5, 20.])]
        value, gradient = likelihood(parameters, prepared)
        numerical = approx_fprime(parameters, lambda p: likelihood(p, prepared)[0], 1e-6)
        self.assertTrue(np.isfinite(value))
        np.testing.assert_allclose(gradient, numerical, rtol=1e-4, atol=1e-5)

    def test_reported_joint_ll_matches_training_objective(self):
        rows = [{"Observation": row(0, 10)}, {"Observation": row(0, 20, game="b")},
                {"Observation": row(2, 30)}, {"Observation": row(200, 25)}]
        configured = roles()
        configured["Starter"].update(PriorVarianceMinutesSquared=8., DailyDriftVariance=.5,
                                      ObservationVarianceMinutesSquared=15., VarianceCapMinutesSquared=28.)
        output = filter_rows(rows, configured)
        score = gaussian_log_scores(rows, output["PredictionsMinutes"], output["PredictiveVarianceMinutesSquared"], output["LatentVarianceMinutesSquared"], "Training")
        raw = np.r_[20., np.log([8., 15., .5, 20.])]
        expected = likelihood(raw, prepare_training(rows, True))[0]
        self.assertAlmostEqual(score["MeanLogLikelihood"], -expected, places=10)
        self.assertEqual(score["LogLikelihoodN"], 4)

    def test_another_dynamics_can_use_same_filter_and_likelihood(self):
        class NoGrowthTestDouble(UncertaintyDynamics):
            key, raw_names, bounds = "test_only", (), ()
            def decode(self, raw, initial_variance): return {}
            def propagate(self, variance, elapsed_days, parameters): return variance
            def propagate_gradient(self, variance, derivative, days, initial, raw, offset): return variance, derivative
            def starting_raw(self, index): return []
        dynamics = NoGrowthTestDouble()
        rows = [{"Observation": row(0, 10)}, {"Observation": row(200, 20)}]
        result = filter_rows(rows, roles(), dynamics)
        self.assertLess(result["LatentVarianceMinutesSquared"][1], 10)
        raw = np.r_[20., np.log([8., 15.])]
        objective, gradient = likelihood(raw, prepare_training(rows, True), dynamics)
        numerical = approx_fprime(raw, lambda p: likelihood(p, prepare_training(rows, True), dynamics)[0], 1e-6)
        self.assertTrue(np.isfinite(objective))
        np.testing.assert_allclose(gradient, numerical, atol=1e-5)

    def test_study_rejects_unknown_effect_and_duplicate_versions_before_fitting(self):
        with self.assertRaises(ValueError): get_dynamics("unimplemented")
        experiment = {"version": "v1", "effects": {"uncertainty_dynamics": "calendar_drift_fitted_cap"}}
        config = {"schema_version": 1, "reference_results": "reference.json"}
        with self.assertRaises(ValueError): validate({**config, "experiments": [experiment, experiment]})
        with self.assertRaises(ValueError): validate({**config, "experiments": [{**experiment, "effects": {"uncertainty_dynamics": "unimplemented"}}]})
        validate({**config, "experiments": [experiment]})

    def test_predictive_noise_does_not_disappear_as_mean_becomes_known(self):
        rows = [{"Observation": row(i, 20)} for i in range(200)]
        output = filter_rows(rows, roles())
        self.assertGreaterEqual(output["PredictiveVarianceMinutesSquared"][-1], 20.)
        self.assertLess(output["PosteriorVariancesMinutesSquared"][-1], 10.)


if __name__ == "__main__":
    unittest.main()
