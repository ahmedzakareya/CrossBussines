"""Request schema for cash-flow projection (Phase 1 ML/analytics, no LLM)."""
from typing import Any

from pydantic import BaseModel, Field


class Flow(BaseModel):
    date: Any = None
    amount: float = 0.0


class CashflowRequest(BaseModel):
    openingCash: float = 0.0
    asOf: Any = None
    horizonDays: int = 90
    inflows: list[Flow] = Field(default_factory=list)
    outflows: list[Flow] = Field(default_factory=list)
