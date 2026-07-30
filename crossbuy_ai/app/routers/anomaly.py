"""Anomaly-detection router (Phase 1 ML, no LLM / no API key)."""
from fastapi import APIRouter, Depends

from app.ml.anomaly import detect_journal_anomalies
from app.schemas.anomaly import AnomalyScanRequest
from app.security import require_secret

router = APIRouter(prefix="/anomaly", tags=["anomaly"], dependencies=[Depends(require_secret)])


@router.post("/journal")
async def journal(body: AnomalyScanRequest) -> dict:
    return detect_journal_anomalies([e.model_dump() for e in body.entries])
