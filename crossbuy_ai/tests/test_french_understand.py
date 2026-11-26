"""French intent routing and refusal boundary.

Run from crossbuy_ai with: python -I tests/test_french_understand.py
"""
from __future__ import annotations

import asyncio
import os
import sys

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from app.routers.understand import understand  # noqa: E402
from app.schemas.understand import UnderstandRequest  # noqa: E402


def route(question: str, intents=None):
    body = UnderstandRequest(
        question=question,
        intents=intents or [{"id": "inventory.item-stock", "phrasings": ["inventory stock"]}],
    )
    return asyncio.run(understand(body))


supported = route("Combien de riz avons-nous en stock ?")
assert supported.intent == "inventory.item-stock", supported
assert supported.engine == "intent-knn-fr-v1", supported

for question in ("Quelle est la meteo ?", "Quel est le meilleur fournisseur ?"):
    refused = route(question)
    assert refused.intent is None, (question, refused)
    assert all(match.intent != "__crossbuy.out-of-scope.fr" for match in refused.ranked), refused

# A French request may only resolve to an intent that the caller included in the catalogue.
restricted = route("Combien d'employés avons-nous ?")
assert restricted.intent in (None, "inventory.item-stock"), restricted

task_catalogue = [
    {"id": "tasks.query", "phrasings": ["tasks", "overdue tasks"]},
    {"id": "inventory.item-stock", "phrasings": ["inventory stock"]},
]
task_question = route("Quelles tâches sont en retard ?", task_catalogue)
assert task_question.intent == "tasks.query", task_question
assert task_question.engine == "intent-knn-fr-v1", task_question

hidden_task_question = route(
    "Quelles tâches sont en retard ?",
    [{"id": "inventory.item-stock", "phrasings": ["inventory stock"]}],
)
assert hidden_task_question.intent is None, hidden_task_question
assert all(match.intent != "tasks.query" for match in hidden_task_question.ranked), hidden_task_question

write_task_question = route("Crée une tâche pour demain", task_catalogue)
assert write_task_question.intent is None, write_task_question

print("PASS French supported question routes to offered intent")
print("PASS out-of-scope French questions are refused")
print("PASS refusal sentinel is never exposed as an intent")
print("PASS filtered catalogues cannot produce a new intent")
print("PASS French task questions reach the permission-filtered task intent")
print("PASS French task intent stays hidden when it is not offered")
print("PASS French write requests are refused by the read-only task intent")
