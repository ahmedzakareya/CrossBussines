from __future__ import annotations

import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.release_gate import candidate_is_acceptable  # noqa: E402


class ReleaseGateTests(unittest.TestCase):
    def setUp(self) -> None:
        self.real = {"correct": 53, "wrong": 0, "leaked": 0,
                     "refusedWrongly": 0, "precision": 1.0}
        self.generated = {"total": 4500, "wrong": 13, "leaked": 6}

    def test_accepts_candidate_inside_all_quality_bounds(self) -> None:
        self.assertTrue(candidate_is_acceptable(self.real, self.generated))

    def test_rejects_any_wrong_or_leaked_human_benchmark_answer(self) -> None:
        for field in ("wrong", "leaked"):
            candidate = dict(self.real, **{field: 1})
            self.assertFalse(candidate_is_acceptable(candidate, self.generated))

    def test_rejects_generated_leak_rate_above_limit(self) -> None:
        generated = dict(self.generated, leaked=10)
        self.assertFalse(candidate_is_acceptable(self.real, generated))

    def test_rejects_generated_wrong_rate_above_limit(self) -> None:
        generated = dict(self.generated, wrong=23)
        self.assertFalse(candidate_is_acceptable(self.real, generated))


if __name__ == "__main__":
    unittest.main()
