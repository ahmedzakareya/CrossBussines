from __future__ import annotations

import json
import os
import sys
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)
sys.path.insert(0, os.path.join(ROOT, "tools"))

from app.ml.intent_data import INTENT_IDS  # noqa: E402
from extract_catalogue import extract  # noqa: E402


class CatalogueParityTests(unittest.TestCase):
    def test_fixture_matches_current_dotnet_catalogue(self) -> None:
        with open(os.path.join(os.path.dirname(__file__), "catalogue.json"), encoding="utf-8") as stream:
            fixture = json.load(stream)
        runtime = extract()
        self.assertEqual(set(runtime), set(fixture), "Python fixture is stale against registered .NET handlers")
        self.assertEqual(runtime, fixture, "phrasing changes must be reflected in the Python test fixture")

    def test_trained_model_catalogue_matches_runtime_handlers(self) -> None:
        runtime = set(extract())
        self.assertEqual(runtime, set(INTENT_IDS), "intent training labels must equal registered .NET capabilities")


if __name__ == "__main__":
    unittest.main()
