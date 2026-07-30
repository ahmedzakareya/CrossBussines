"""CrossBuy AI Service — FastAPI app (Phase 0: infrastructure only).

No AI capability yet. This step only proves the service runs, /health is
open, and protected routes reject calls without the shared secret.
"""
from fastapi import Depends, FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.routers import anomaly, diag, forecast, inventory
from app.security import require_secret

app = FastAPI(
    title="CrossBuy AI Service",
    version="0.1.0",
    description="Internal AI layer for CrossBuy. Called only by the .NET backend.",
)

# Internal service: do not allow any browser origin. Calls come from .NET only.
app.add_middleware(
    CORSMiddleware,
    allow_origins=[],
    allow_methods=[],
    allow_headers=[],
)


@app.get("/health", tags=["system"])
async def health() -> dict[str, str]:
    """Open, unauthenticated liveness probe."""
    return {"status": "ok", "service": "crossbuy-ai"}


@app.get("/ping", tags=["system"], dependencies=[Depends(require_secret)])
async def ping() -> dict[str, str]:
    """Protected sample route — proves the X-AI-Secret guard works (401 without it)."""
    return {"status": "authorized"}


app.include_router(diag.router)
app.include_router(anomaly.router)
app.include_router(forecast.router)
app.include_router(inventory.router)
