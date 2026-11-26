"""Request schema for journal anomaly detection (Phase 1 ML, no LLM)."""
from typing import Any

from pydantic import BaseModel, Field


class JournalEntryIn(BaseModel):
    id: int
    entryNo: str | None = None
    date: Any = None
    journalType: str | None = None
    sourceType: str | None = None
    # The DOCUMENT this entry posts. Duplication is judged on (sourceType, sourceId) because
    # descriptions in this product are mostly templated and cannot tell two documents apart.
    # Optional so an older caller still works — it then simply has nothing to deduplicate on,
    # and the response says how many entries went unchecked.
    sourceId: int | None = None
    # A short stable hash of the entry's LINES — (account, debit, credit) ordered. It is what
    # separates a double-posting from a REVERSAL: both have the same total and line count, but a
    # reversal has debit and credit swapped, so its signature differs. Absent means the posting
    # cannot be compared, and those entries are reported as unchecked rather than guessed at.
    lineSignature: str | None = None
    description: str | None = None
    amount: float = 0.0
    lineCount: int = 0


class AnomalyScanRequest(BaseModel):
    entries: list[JournalEntryIn] = Field(default_factory=list)
    # The share of the ledger the structural ranking may surface. A budget for human attention,
    # not a detection threshold — see app/ml/anomaly.py.
    reviewBudget: float = 0.01
