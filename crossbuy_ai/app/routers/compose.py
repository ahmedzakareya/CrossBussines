"""Compose a query over a governed surface.

The surface's declaration travels WITH the question, exactly as the intent catalogue does on
/understand: this service holds no copy of what CrossBuy exposes, so it cannot name a column that
.NET did not just send it, and cannot drift out of step with the surface definition.

What crosses the wire: the question, the surface code, its description, and its column names and
labels. No rows, no figures, no identity. The question is still free-text business content, so it
travels under the same egress purpose and the same Internal-only rule.
"""
from fastapi import APIRouter, Depends
from pydantic import BaseModel, Field

from app.config import get_settings
from app.ml.compose import compose as compose_query
from app.ml.intent_llm import engine as llm_engine
from app.security import require_secret

router = APIRouter(prefix="/compose", tags=["compose"], dependencies=[Depends(require_secret)])


class ColumnIn(BaseModel):
    name: str = Field(min_length=1, max_length=60)
    label: str = Field(default="", max_length=120)


class ComposeRequest(BaseModel):
    question: str = Field(min_length=1, max_length=400)
    surface: str = Field(min_length=1, max_length=120)
    description: str = Field(default="", max_length=400)
    dimensions: list[ColumnIn] = Field(default_factory=list)
    measures: list[ColumnIn] = Field(default_factory=list)


class ComposeResponse(BaseModel):
    # None whenever nothing usable came back. The caller must substitute a known-good query and
    # say so, never guess at what was meant.
    composed: bool
    groupBy: str | None = None
    measure: str | None = None
    descending: bool = True
    limit: int = 10
    engine: str = "none"
    raw: str = ""


@router.post("", response_model=ComposeResponse)
async def compose(body: ComposeRequest) -> ComposeResponse:
    if not get_settings().intent_llm_enabled:
        return ComposeResponse(composed=False, engine="disabled")

    eng = llm_engine()
    if eng is None:
        return ComposeResponse(composed=False, engine="no-model")

    import asyncio

    result = await asyncio.to_thread(
        compose_query,
        eng,
        body.question,
        body.surface,
        body.description,
        [(c.name, c.label or c.name) for c in body.dimensions],
        [(c.name, c.label or c.name) for c in body.measures],
    )

    if result is None:
        return ComposeResponse(composed=False, engine=eng.name)

    return ComposeResponse(
        composed=True,
        groupBy=result.group_by,
        measure=result.measure,
        descending=result.descending,
        limit=result.limit,
        engine=eng.name,
        raw=result.raw,
    )
