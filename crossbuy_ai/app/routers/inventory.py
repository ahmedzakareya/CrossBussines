"""Inventory analysis router (Phase 1 ML/analytics, no LLM / no API key)."""
from fastapi import APIRouter, Depends

from app.ml.inventory import analyze_inventory
from app.schemas.inventory import InventoryRequest
from app.security import require_secret

router = APIRouter(prefix="/inventory", tags=["inventory"], dependencies=[Depends(require_secret)])


@router.post("/analyze")
async def analyze(body: InventoryRequest) -> dict:
    return analyze_inventory(body.asOf, body.slowDays, [i.model_dump() for i in body.items])
