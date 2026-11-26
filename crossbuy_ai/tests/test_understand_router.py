from __future__ import annotations

import asyncio
import json
import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)

from app.routers.understand import understand  # noqa: E402
from app.schemas.understand import IntentIn, UnderstandRequest  # noqa: E402
from pydantic import ValidationError  # noqa: E402


class UnderstandRouterFallbackTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        with open(os.path.join(ROOT, "tests", "catalogue.json"), encoding="utf-8") as stream:
            catalogue = json.load(stream)
        cls.intents = [IntentIn(
            id=intent,
            phrasings=body["keywords"] + body["example"] + body["desc"],
        ) for intent, body in catalogue.items()]
        # An untrained intent forces the request-local matcher, as happens immediately after
        # adding a capability before a retrain has produced a model that covers it.
        cls.intents.append(IntentIn(id="test.untrained-capability", phrasings=["unseen test capability"]))

    def ask(self, question: str):
        request = UnderstandRequest(question=question, intents=self.intents)
        return asyncio.run(understand(request))

    def test_dynamic_fallback_answers_only_registered_questions(self) -> None:
        answer = self.ask("كام رصيد مياه معدنية")
        self.assertEqual("inventory.item-stock", answer.intent)

    def test_dynamic_fallback_refuses_out_of_scope_even_if_nearest_surface_is_close(self) -> None:
        for question in ("ما هو الطقس اليوم", "مين أحسن مورد عندنا؟"):
            with self.subTest(question=question):
                answer = self.ask(question)
                self.assertIsNone(answer.intent)
                self.assertNotIn("none", [row.intent for row in answer.ranked])

    def test_catalogue_request_is_bounded_and_ids_are_unique(self) -> None:
        with self.assertRaises(ValidationError):
            UnderstandRequest(question="test", intents=[
                IntentIn(id="inventory.item-stock", phrasings=["stock"]),
                IntentIn(id="inventory.item-stock", phrasings=["items"]),
            ])
        with self.assertRaises(ValidationError):
            IntentIn(id="INVALID ID", phrasings=["question"])
        with self.assertRaises(ValidationError):
            IntentIn(id="test.intent", phrasings=["x" * 501])


if __name__ == "__main__":
    unittest.main()
