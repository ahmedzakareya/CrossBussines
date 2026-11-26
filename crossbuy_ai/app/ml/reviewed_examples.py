"""Load explicitly reviewed local intent examples for candidate training.

Live user messages are never training data. This file accepts only human-approved examples,
validates the closed intent catalogue, and rejects ambiguous duplicate phrasings.
"""
from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path

from app.ml.intent_data import INTENT_IDS, OUT_OF_SCOPE
from app.ml.text_normalization import normalise

MAX_EXAMPLE_LENGTH = 400
MAX_EXAMPLES = 20_000


@dataclass(frozen=True)
class ReviewedExample:
    text: str
    intent: str


def load_reviewed_examples(path: str | Path) -> list[ReviewedExample]:
    """Read JSONL rows shaped as ``{"approved": true, "text": ..., "intent": ...}``.

    An empty or missing file is a normal state. Invalid rows fail closed with a line number;
    training must never silently skip a malformed label or ingest unapproved conversation text.
    """
    source = Path(path)
    if not source.exists():
        return []

    known = set(INTENT_IDS) | {OUT_OF_SCOPE}
    seen: dict[str, str] = {}
    examples: list[ReviewedExample] = []
    with source.open("r", encoding="utf-8") as stream:
        for line_number, raw in enumerate(stream, 1):
            if not raw.strip() or raw.lstrip().startswith("#"):
                continue
            try:
                item = json.loads(raw)
            except json.JSONDecodeError as exc:
                raise ValueError(f"line {line_number}: invalid JSON") from exc
            if not isinstance(item, dict) or item.get("approved") is not True:
                raise ValueError(f"line {line_number}: example must be explicitly approved")
            text, intent = item.get("text"), item.get("intent")
            if not isinstance(text, str) or not text.strip() or len(text) > MAX_EXAMPLE_LENGTH:
                raise ValueError(f"line {line_number}: text must contain 1-{MAX_EXAMPLE_LENGTH} characters")
            if not isinstance(intent, str) or intent not in known:
                raise ValueError(f"line {line_number}: intent is not in the closed catalogue")
            key = normalise(text)
            if not key:
                raise ValueError(f"line {line_number}: text has no searchable characters")
            previous = seen.get(key)
            if previous is not None:
                if previous != intent:
                    raise ValueError(f"line {line_number}: normalized text has conflicting labels")
                raise ValueError(f"line {line_number}: duplicate normalized text")
            seen[key] = intent
            examples.append(ReviewedExample(text=text.strip(), intent=intent))
            if len(examples) > MAX_EXAMPLES:
                raise ValueError(f"dataset exceeds {MAX_EXAMPLES} reviewed examples")
    return examples
