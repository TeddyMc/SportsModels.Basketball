import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import numpy as np

import train


def row(day, value, role=True, split="Training", registry="registry", game=None):
    from datetime import date, timedelta
    return {"RegistryId": registry, "PlayerId": 1, "Name": "Player", "GameId": game or f"g{day}",
            "Date": (date(2024, 1, 1) + timedelta(days=day)).isoformat(), "Started": role,
            "RegulationSeconds": None if value is None else value*60, "Split": split}


class HistoryTests(unittest.TestCase):
    def test_last_ten_matching_games_not_last_ten_overall(self):
        data = [row(i, i, i % 2 == 0) for i in range(31)]
        rows, features = train.build_history(data[::-1])
        np.testing.assert_array_equal(features[-1], np.arange(28, 8, -2))
        self.assertEqual(rows[-1]["HistoryCount"], 10)
        self.assertLess(rows[-1]["LatestHistoryDate"], rows[-1]["Observation"]["Date"])

    def test_zero_missing_unknown_role_and_registry_separation(self):
        data = [row(0, 0), row(1, None), row(2, 20, None), row(3, 30), row(4, 10, registry="other")]
        rows, features = train.build_history(data)
        self.assertEqual(features[3, 0], 0)
        self.assertEqual(rows[3]["HistoryCount"], 1)
        self.assertTrue(np.isnan(features[2]).all())
        self.assertTrue(np.isnan(features[4]).all())

    def test_same_day_leakage_and_walk_forward_validation(self):
        data = [row(0, 10), row(1, 20, split="Validation"),
                row(1, 40, split="Validation", game="other"), row(2, 30, split="Validation")]
        _, features = train.build_history(data)
        self.assertEqual(features[1, 0], 10)
        self.assertEqual(features[2, 0], 10)
        self.assertEqual(features[3, 0], 40)

    def test_duplicate_player_game_rejected(self):
        with self.assertRaises(ValueError):
            train.build_history([row(0, 10), row(0, 20)])


class ModelTests(unittest.TestCase):
    def test_collective_average_and_short_history_normalisation(self):
        history = np.array([[10, 20, 30, 40, 50, 0, 10, np.nan, np.nan, np.nan]], dtype=float)
        grouped = train.grouped_features(history, 5, 5)
        self.assertEqual(grouped[0, -1], 5)
        self.assertAlmostEqual(train.predict(grouped, np.array([.1]*5+[.5]))[0], 17.5)
        history[0, 1:] = np.nan
        self.assertEqual(train.predict(train.grouped_features(history, 3, 3), np.full(4, .25))[0], 10)

    def test_exponential_endpoints_and_free_newest_weight(self):
        np.testing.assert_array_equal(train.weights("exponential", [0]), [1]+[0]*9)
        np.testing.assert_allclose(train.weights("exponential", [1]), np.full(10, .1))
        weights = train.weights("exponential_newest", [.5, .7])
        self.assertAlmostEqual(weights[0], .7)
        self.assertAlmostEqual(weights[1:].sum(), .3)
        self.assertAlmostEqual(weights[2]/weights[1], .5)

    def test_fit_uses_mae_and_recovers_weighted_signal(self):
        rng = np.random.default_rng(20)
        features = rng.uniform(0, 48, size=(150, 4))
        actual = .8*features[:, 0]+.2*features[:, 1]
        weights, diagnostics = train.fit("three_plus_three", features, actual)
        self.assertTrue(diagnostics["Success"])
        self.assertLess(np.abs(train.predict(features, weights)-actual).mean(), 1e-4)
        self.assertAlmostEqual(sum(weights), 1, places=6)

    def test_validation_outcomes_never_reach_fitter(self):
        observations = [row(i, 10 if i < 2 else 45, role=i % 2 == 0,
                            split="Training" if i < 2 else "Validation", game=f"g{i}") for i in range(4)]
        rows = [{"Observation": o} for o in observations]
        features = np.full((4, 10), 20.0)
        seen = []

        def fake_fit(kind, x, y):
            seen.extend(y.tolist())
            parameters = [.5] if kind == "exponential" else [.5, .4] if kind == "exponential_newest" else [1/x.shape[1]]*x.shape[1]
            return parameters, {"TrainingObjectiveMae": 0}

        with patch.object(train, "fit", side_effect=fake_fit), contextlib.redirect_stdout(io.StringIO()):
            models = train.train_models(rows, features)
        self.assertTrue(seen)
        self.assertEqual(set(seen), {10.0})
        self.assertEqual({m["Validation"]["Evaluated"] for m in models}, {2})

    def test_metrics_keep_zero_and_exclude_missing(self):
        rows = [{"Observation": row(0, 0)}, {"Observation": row(1, 10)}, {"Observation": row(2, None)}]
        metrics = train.metrics(rows, np.array([5., 5., 5.]))
        self.assertEqual(metrics["Evaluated"], 2)
        self.assertEqual(metrics["MaeMinutes"], 5)
        self.assertEqual(metrics["MeanSignedErrorMinutes"], 0)

    def test_report_escapes_embedded_script_delimiters(self):
        with tempfile.TemporaryDirectory() as directory:
            train.write_report(Path(directory), {"Name": "</script><script>unsafe</script>"})
            html = (Path(directory)/"index.html").read_text(encoding="utf-8")
            self.assertEqual(html.count("<script>"), 1)
            self.assertNotIn("<script>unsafe", html)


if __name__ == "__main__":
    unittest.main()
