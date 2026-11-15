"""Request and response shapes for /understand.

THE CATALOGUE TRAVELS WITH THE QUESTION, and that is deliberate. This service holds no copy of
what CrossBuy can be asked: .NET owns the catalogue, sends the candidates it will accept, and
receives back one of the ids it sent. So this service cannot name a question that does not exist,
cannot retain one after .NET stops offering it, and cannot drift out of step with the handlers.

WHAT CROSSES THE BOUNDARY: the question text and the intent names. No rows, no figures, no
customer or employee data, no company id. The question itself is free-text business content, which
the egress matrix permits only to an Internal destination — which is why this runs on loopback and
why a hosted model is a governance decision rather than a configuration change.
"""
from __future__ import annotations

from pydantic import BaseModel, Field


class IntentIn(BaseModel):
    id: str = Field(min_length=1, max_length=120)
    # Everything a person might say to mean this question: the handler's keywords, its
    # description and its example, sent as one list because this layer does not care which is
    # which — it only measures how close the question came to any known way of asking.
    phrasings: list[str] = Field(default_factory=list)


class UnderstandRequest(BaseModel):
    question: str = Field(min_length=1, max_length=400)
    intents: list[IntentIn] = Field(default_factory=list)


class MatchOut(BaseModel):
    intent: str
    score: float


class UnderstandResponse(BaseModel):
    # None when nothing cleared the answer floor. The caller must treat this as "ask me to
    # choose", never as "pick the first one anyway".
    intent: str | None
    confidence: float
    # Always present, best first, so a refusal can still offer the near misses.
    ranked: list[MatchOut]
    engine: str
    answerFloor: float
    offerFloor: float
