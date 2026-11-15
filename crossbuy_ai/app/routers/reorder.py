"""Reorder point router (local analytics, no LLM / no API key).

Proposes ReorderPoint and SafetyStock per item x warehouse, or refuses and
says what history is missing. It never writes: the response is advice for the
buyer, and the .NET side is what persists anything the buyer accepts.
"""
from fastapi import APIRouter, Depends

from app.ml.reorder import compute_reorder_points
from app.schemas.reorder import ReorderRequest
from app.security import require_secret

router = APIRouter(prefix="/inventory", tags=["inventory"], dependencies=[Depends(require_secret)])


@router.post("/reorder-points")
async def reorder_points(body: ReorderRequest) -> dict:
    return compute_reorder_points(
        body.asOf,
        body.windowDays,
        body.serviceLevel,
        body.defaultLeadTimeDays,
        [r.model_dump() for r in body.rows],
    )
