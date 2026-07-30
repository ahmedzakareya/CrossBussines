"""Shared-secret guard for the internal AI service (Phase 0).

This service is internal: only the .NET backend may call it. Every route
except /health must carry the X-AI-Secret header matching AI_SHARED_SECRET.
Identity/permissions of the *end user* are handled in .NET, never here.
"""
from fastapi import Header, HTTPException, status

from app.config import get_settings


async def require_secret(x_ai_secret: str | None = Header(default=None)) -> None:
    settings = get_settings()
    expected = settings.ai_shared_secret
    # Fail closed: if no secret is configured, reject everything.
    if not expected or x_ai_secret != expected:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Invalid or missing X-AI-Secret header.",
        )
