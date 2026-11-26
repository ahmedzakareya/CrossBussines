"""CrossBuy AI Service — FastAPI app (Phase 0: infrastructure only).

No AI capability yet. This step only proves the service runs, /health is
open, and protected routes reject calls without the shared secret.
"""
from fastapi import Depends, FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.routers import anomaly, assist, calls, compose, diag, forecast, inventory, reorder, speech, understand
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


@app.on_event("startup")
async def warm_tail_engine() -> None:
    """Load the local model's weights in the background, before anyone asks.

    The first call loads 2.3 GB and takes the better part of a minute. Paying that inside a
    request puts a forty-second silence in a chat box, and a forty-second silence reads as broken
    — the same misreading that once made me call a nine-second answer a missing one. Warming it
    here moves the cost to startup, where nobody is waiting on it.

    In a thread, so the service answers /health and every cheap route while this runs; and
    swallowed, because a missing or unreadable model is a normal checkout, not a failed boot.
    """
    from app.config import get_settings

    if not get_settings().intent_llm_enabled:
        return

    import threading

    def _warm() -> None:
        try:
            from app.ml.intent_llm import engine
            eng = engine()
            if eng is not None:
                # warm() loads AND flips the readiness flag. complete() deliberately refuses to
                # load, so until this finishes every caller is told "not available" at once
                # instead of queueing behind 2.3 GB of weights.
                eng.warm()
        except Exception:
            pass

    threading.Thread(target=_warm, name="warm-intent-llm", daemon=True).start()


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
app.include_router(reorder.router)
# Which of the catalogue's questions is this? The only route that sees free user text, which is
# why it runs here on loopback rather than anywhere a question could leave the building.
app.include_router(understand.router)
# Turning a sentence into a query over a governed surface. Reached only for a surface-backed
# question, and constrained by a grammar to the columns that surface just declared.
app.include_router(compose.router)
app.include_router(speech.router)   # speech to text: a voice message becomes words (Whisper, on this machine)
app.include_router(calls.router)    # call summaries: a recorded call's words and summary, in the background
app.include_router(assist.router)   # the assistant in a conversation (summary, questions) and message translation


@app.on_event("startup")
async def resume_call_summaries() -> None:
    """A call summary that was queued or half-done when the service stopped starts again from its audio."""
    try:
        from app.ml.calls import resume_unfinished
        resume_unfinished()
    except Exception:
        pass
