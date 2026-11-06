import unittest
import json
from pathlib import Path
import tempfile

from registry import align_to, register, score_groups, snapshot, generate
from test_train import row


class RegistryTests(unittest.TestCase):
    def test_errors_are_split_by_role_and_dataset(self):
        rows = [{"Observation": row(0, 10, True)}, {"Observation": row(1, 20, False)},
                {"Observation": row(2, 30, True, "Validation")}, {"Observation": row(3, 0, False, "Validation")}]
        scores = score_groups(rows, [8, 16, 24, 3])
        self.assertEqual(scores["Training"]["Starter"]["MseMinutesSquared"], 4)
        self.assertEqual(scores["Training"]["Bench"]["MseMinutesSquared"], 16)
        self.assertEqual(scores["Validation"]["Overall"]["MseMinutesSquared"], 22.5)
        self.assertEqual(scores["Validation"]["Overall"]["Evaluated"], 2)

    def test_alignment_uses_identity_and_rejects_changed_targets(self):
        rows = [{"Observation": row(0, 10)}, {"Observation": row(1, 20)}]
        self.assertEqual(align_to(rows, rows[::-1], [2, 1]), [1, 2])
        changed = [{"Observation": row(0, 11)}, rows[1]]
        with self.assertRaises(ValueError):
            align_to(rows, changed, [1, 2])

    def test_model_versions_and_benchmark_cannot_be_silently_replaced(self):
        registry = {"Versions": [], "Benchmark": None}
        entry = {"Version": "benchmark-v1", "Fingerprint": "a"}
        register(registry, entry, benchmark=True)
        register(registry, entry, benchmark=True)
        self.assertEqual(len(registry["Versions"]), 1)
        with self.assertRaises(ValueError):
            register(registry, {**entry, "Fingerprint": "b"})
        with self.assertRaises(ValueError):
            register(registry, {"Version": "other", "Fingerprint": "b"}, benchmark=True)
        self.assertEqual(len(registry["Versions"]), 1)

    def test_prior_only_predictions_do_not_change_matched_benchmark_cohort(self):
        rows = [{"Observation": row(i, 10+i)} for i in range(3)]
        def payload(predictions):
            return {"Rows": rows, "Models": [{"Key": "model", "Name": "Model", "Lookback": 10,
                    "Roles": {}, "Loss": "mse", "PredictionsMinutes": predictions}],
                    "Summary": {"CatalogSha256": "catalog", "Games": 3}}
        registry = {"Versions": [], "Benchmark": None}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for version, predictions, benchmark in [("benchmark", [None, 10, 10], True), ("prior", [10, 11, 12], False)]:
                path = root / f"{version}.json"
                data = payload(predictions)
                path.write_text(json.dumps(data))
                register(registry, snapshot(data, "model", version, path), benchmark)
            generate(registry, root / "report")
        candidate = registry["Versions"][1]
        self.assertEqual(candidate["AllAvailable"]["Training"]["Overall"]["Evaluated"], 3)
        self.assertEqual(candidate["Matched"]["Training"]["Overall"]["Evaluated"], 2)
        self.assertEqual(candidate["MatchedBenchmark"]["Training"]["Overall"]["Evaluated"], 2)


if __name__ == "__main__":
    unittest.main()
