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

    # The SHAPE of the demand behind out90, not just its size. Without these a daily rate is
    # out90/slowDays, which reads a single-day burst of 2,470 units as a steady 27/day for ever.
    # Optional: absent means the rate is simply not estimated, and the item says so.
    activeDays: int | None = None
    spanDays: int | None = None
    maxDayQty: float | None = None


class InventoryRequest(BaseModel):
    asOf: Any = None
    slowDays: int = 90

    # Activity across the WHOLE window, which decides whether "no issues" can mean dormant at all.
    # A window holding 4 active days out of 90 says something about the period, not the items.
    windowActiveDays: int | None = None
    windowMovements: int | None = None

    items: list[ItemIn] = Field(default_factory=list)
