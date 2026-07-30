"""Cash-flow forecast router (Phase 1 ML/analytics, no LLM / no API key)."""
from fastapi import APIRouter, Depends

from app.ml.forecast import project_cashflow
from app.schemas.forecast import CashflowRequest
from app.security import require_secret

router = APIRouter(prefix="/forecast", tags=["forecast"], dependencies=[Depends(require_secret)])


@router.post("/cashflow")
async def cashflow(body: CashflowRequest) -> dict:
    return project_cashflow(
        body.openingCash, body.asOf, body.horizonDays,
        [f.model_dump() for f in body.inflows],
        [f.model_dump() for f in body.outflows],
    )
