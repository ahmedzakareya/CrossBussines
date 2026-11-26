from __future__ import annotations

import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.ml.reviewed_examples import load_reviewed_examples  # noqa: E402


class ReviewedExamplesTests(unittest.TestCase):
    def read_rows(self, rows: list[object]) -> list:
        handle = tempfile.NamedTemporaryFile("w", encoding="utf-8", delete=False)
        try:
            with handle:
                for row in rows:
                    handle.write(json.dumps(row, ensure_ascii=False) + "\n")
            return load_reviewed_examples(handle.name)
        finally:
            os.unlink(handle.name)

    def test_accepts_only_explicitly_approved_closed_catalogue_example(self) -> None:
        rows = self.read_rows([{"approved": True, "text": "كام رصيد المياه", "intent": "inventory.item-stock"}])
        self.assertEqual(1, len(rows))
        self.assertEqual("inventory.item-stock", rows[0].intent)

    def test_rejects_unapproved_and_unknown_intent(self) -> None:
        with self.assertRaisesRegex(ValueError, "explicitly approved"):
            self.read_rows([{"approved": False, "text": "question", "intent": "none"}])
        with self.assertRaisesRegex(ValueError, "closed catalogue"):
            self.read_rows([{"approved": True, "text": "question", "intent": "made.up"}])

    def test_rejects_normalized_duplicates_and_conflicting_labels(self) -> None:
        with self.assertRaisesRegex(ValueError, "duplicate normalized"):
            self.read_rows([
                {"approved": True, "text": "أحمد؟", "intent": "inventory.item-stock"},
                {"approved": True, "text": "احمد", "intent": "inventory.item-stock"},
            ])
        with self.assertRaisesRegex(ValueError, "conflicting labels"):
            self.read_rows([
                {"approved": True, "text": "مياه", "intent": "inventory.item-stock"},
                {"approved": True, "text": "مياه؟", "intent": "accounting.cash-balance"},
            ])

    def test_missing_file_is_empty(self) -> None:
        self.assertEqual([], load_reviewed_examples("missing-reviewed-questions.jsonl"))


if __name__ == "__main__":
    unittest.main()
