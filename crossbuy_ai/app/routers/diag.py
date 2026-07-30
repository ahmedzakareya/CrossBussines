"""Diagnostics router (Phase 0.2) — proves the Claude connection works.

POST /diag/echo sends a short Arabic message to Claude and returns the reply
plus token counts. TEMPORARY — remove once Phase 1 capabilities exist.
"""
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, status
from pydantic import BaseModel, Field

from app.clients.anthropic import LLMConfigError, complete
from app.security import require_secret

router = APIRouter(prefix="/diag", tags=["diag"], dependencies=[Depends(require_secret)])


class EchoRequest(BaseModel):
    message: str = Field(default="من فضلك رد بجملة عربية قصيرة تؤكد أن الاتصال يعمل.")
    tier: Literal["fast", "smart"] = "fast"


class EchoResponse(BaseModel):
    reply: str
    model: str
    input_tokens: int
    output_tokens: int


@router.post("/echo", response_model=EchoResponse)
async def echo(body: EchoRequest) -> EchoResponse:
    try:
        result = complete(
            messages=[{"role": "user", "content": body.message}],
            system="أنت مساعد CrossBuy. رد بإيجاز وبالعربية الفصحى.",
            model_tier=body.tier,
            max_tokens=256,
        )
    except LLMConfigError as exc:
        raise HTTPException(
            status_code=status.HTTP_503_SERVICE_UNAVAILABLE, detail=str(exc)
        ) from exc
    return EchoResponse(
        reply=result.text,
        model=result.model,
        input_tokens=result.input_tokens,
        output_tokens=result.output_tokens,
    )
