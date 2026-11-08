"""Deterministic report comparison tests: no UI, subprocesses or clock assertions."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("benchmark", Path(__file__).with_name("rendering-benchmark.py"))
benchmark = importlib.util.module_from_spec(spec)
spec.loader.exec_module(benchmark)


class ComparisonTests(unittest.TestCase):
    def report(self):
        return dict(schema=1, workload="v1", corpusSha256="corpus", harnessSha256="harness",
                    environment={"machine": "fixed"}, parameters={"samples": 3}, revision="baseline",
                    scenarios=[dict(id="fixed", samples=[dict(milliseconds=n, allocatedBytes=n * 10,
                                                            work={"visits": 16}) for n in (1, 2, 30)])])

    def test_reports_median_and_work_counts_without_a_timing_verdict(self):
        before = self.report()
        after = copy.deepcopy(before)
        after["revision"] = "candidate"
        for sample in after["scenarios"][0]["samples"]:
            sample["milliseconds"] *= 2
        result = benchmark.compare(before, after)
        self.assertIn("2.000 -> 4.000 (+100.0%)", result)
        self.assertIn("visits: [16] -> [16]", result)
        self.assertIn("candidate", result)

    def test_rejects_incompatible_environment_corpus_harness_and_parameters(self):
        for field in ("schema", "workload", "corpusSha256", "harnessSha256", "environment", "parameters"):
            with self.subTest(field=field):
                before = self.report()
                after = copy.deepcopy(before)
                after[field] = None
                with self.assertRaises(ValueError):
                    benchmark.compare(before, after)

    def test_rejects_incomplete_or_different_workloads(self):
        for change in (lambda r: r["scenarios"][0]["samples"].pop(),
                       lambda r: r["scenarios"].clear(),
                       lambda r: r["scenarios"][0]["samples"][0]["work"].clear()):
            before = self.report()
            after = copy.deepcopy(before)
            change(after)
            with self.assertRaises(ValueError):
                benchmark.compare(before, after)

    def test_zero_baseline_and_variable_work_counts_are_visible(self):
        before = self.report()
        after = copy.deepcopy(before)
        for sample in before["scenarios"][0]["samples"]:
            sample["allocatedBytes"] = 0
        after["scenarios"][0]["samples"][0]["work"]["visits"] = 32
        result = benchmark.compare(before, after)
        self.assertIn("n/a (zero baseline)", result)
        self.assertIn("visits: [16] -> [16, 32]", result)

    def test_missing_historical_counter_is_not_reported_as_zero(self):
        before = self.report()
        after = copy.deepcopy(before)
        for sample in before["scenarios"][0]["samples"]:
            sample["work"]["visits"] = None
        result = benchmark.compare(before, after)
        self.assertIn("visits: [] (unavailable in some/all samples) -> [16]", result)


if __name__ == "__main__":
    unittest.main()
