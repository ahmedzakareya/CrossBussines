"""Request schema for inventory analysis (Phase 1 ML/analytics, no LLM)."""
from typing import Any

from pydantic import BaseModel, Field


class ItemIn(BaseModel):
    itemId: int
    code: str | None = None
    name: str | None = None
    nameEn: str | None = None
    onHand: float = 0.0
    value: float = 0.0
    out90: float = 0.0
    out30: float = 0.0
    reorderPoint: float | None = None
    maxQty: float | None = None
    lastMovement: Any = None


class InventoryRequest(BaseModel):
    asOf: Any = None
    slowDays: int = 90
    items: list[ItemIn] = Field(default_factory=list)
