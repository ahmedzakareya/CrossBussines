"""Claude (Anthropic) client wrapper with model routing (Phase 0.2).

Two tiers:
  - "fast"  -> FAST_MODEL    (Haiku — cheap, short classify/summarize)
  - "smart" -> DEFAULT_MODEL (Opus 4.8 — Copilot, extraction, multi-step)

The smart tier uses adaptive thinking (the only on-mode for Opus 4.8/4.7;
budget_tokens is rejected with a 400). The system prompt is sent as a cached
block so a stable prefix is reused across requests once it's large enough.
"""
from __future__ import annotations

from dataclasses import dataclass

import anthropic

from app.config import get_settings


class LLMConfigError(RuntimeError):
    """Raised when the service is asked to call Claude without an API key."""


@dataclass
class LLMResult:
    text: str
    model: str
    stop_reason: str | None
    input_tokens: int
    output_tokens: int


def _client() -> anthropic.Anthropic:
    settings = get_settings()
    if not settings.anthropic_api_key:
        raise LLMConfigError("ANTHROPIC_API_KEY is not configured.")
    # The SDK retries 429/5xx/connection errors with exponential backoff.
    return anthropic.Anthropic(api_key=settings.anthropic_api_key, max_retries=3)


def _model_for(tier: str) -> str:
    settings = get_settings()
    return settings.fast_model if tier == "fast" else settings.default_model


def complete(
    *,
    messages: list[dict],
    system: str,
    model_tier: str = "smart",
    tools: list[dict] | None = None,
    max_tokens: int = 4096,
) -> LLMResult:
    """One Claude turn. Returns the text + token counts.

    `system` is wrapped as a cache-eligible block. Adaptive thinking is enabled
    on the smart tier only (Haiku does not take adaptive thinking / effort).
    """
    client = _client()
    model = _model_for(model_tier)

    kwargs: dict = {
        "model": model,
        "max_tokens": max_tokens,
        "system": [
            {
                "type": "text",
                "text": system,
                "cache_control": {"type": "ephemeral"},
            }
        ],
        "messages": messages,
    }
    if tools:
        kwargs["tools"] = tools
    if model_tier == "smart":
        # Adaptive thinking: the recommended (and only on-) mode for Opus 4.8.
        kwargs["thinking"] = {"type": "adaptive"}

    resp = client.messages.create(**kwargs)

    text = "".join(
        block.text for block in resp.content if getattr(block, "type", None) == "text"
    )
    return LLMResult(
        text=text,
        model=resp.model,
        stop_reason=resp.stop_reason,
        input_tokens=resp.usage.input_tokens,
        output_tokens=resp.usage.output_tokens,
    )
