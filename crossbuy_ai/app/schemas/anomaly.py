"""Request schema for journal anomaly detection (Phase 1 ML, no LLM)."""
from typing import Any

from pydantic import BaseModel, Field


class JournalEntryIn(BaseModel):
    id: int
    entryNo: str | None = None
    date: Any = None
    journalType: str | None = None
    sourceType: str | None = None
    description: str | None = None
    amount: float = 0.0
    lineCount: int = 0


class AnomalyScanRequest(BaseModel):
    entries: list[JournalEntryIn] = Field(default_factory=list)
