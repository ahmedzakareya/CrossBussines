"""Request schema for the reorder point engine (local, no LLM / no API key).

Note what this schema REFUSES to accept: a bare demand total. Every row must
carry `activeDays`, `spanDays`, `sumSqQty` and `maxDayQty` alongside
`totalQty`, because a total on its own cannot tell steady demand from a single
day's burst, and the two need very different reorder points. The .NET caller
aggregates those in SQL; see AiInsightsService.ComputeReorderPointsAsync.
"""
from typing import Any

from pydantic import BaseModel, Field


class ReorderRowIn(BaseModel):
    itemId: int
    warehouseId: int
    code: str | None = None
    name: str | None = None
    nameEn: str | None = None
    warehouseName: str | None = None

    onHand: float = 0.0

    # The demand DISTRIBUTION over the window, not merely its total.
    activeDays: int = 0          # distinct days carrying an outbound movement
    spanDays: int = 0            # first outbound day -> last outbound day
    totalQty: float = 0.0        # sum of daily quantity
    sumSqQty: float = 0.0        # sum of (daily quantity)^2 -> exact variance
    maxDayQty: float = 0.0       # largest single day -> burst detection
    firstOut: Any = None
    lastOut: Any = None

    # Supply side. Both may be absent; the engine says so in its reasons
    # rather than quietly assuming a punctual supplier.
    leadTimeDays: float | None = None
    leadTimeSdDays: float | None = 0.0

    # What the system holds today, so the response can show the delta.
    currentReorderPoint: float | None = None
    currentSafetyStock: float | None = None


class ReorderRequest(BaseModel):
    asOf: Any = None
    windowDays: int = 90
    serviceLevel: float = 0.95
    defaultLeadTimeDays: float = 7.0
    rows: list[ReorderRowIn] = Field(default_factory=list)
