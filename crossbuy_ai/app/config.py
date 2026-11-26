"""Configuration loaded from environment / .env (Phase 0).

Centralizes every setting so no module reads os.environ directly.
"""
from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env", env_file_encoding="utf-8", extra="ignore"
    )

    # Anthropic / Claude
    anthropic_api_key: str = ""

    # Shared secret enforced on every route except /health
    ai_shared_secret: str = ""

    # .NET backend (the source of truth; tools call back into it)
    dotnet_base_url: str = "http://localhost:5000"

    # Model routing
    default_model: str = "claude-opus-4-8"
    fast_model: str = "claude-haiku-4-5-20251001"

    # Service
    ai_service_port: int = 8000

    # THE TAIL ENGINE. A local GGUF model, asked ONLY when the trained classifier and the
    # nearest-neighbour matcher have both declined — so its seconds are spent instead of a
    # failure, never instead of a fast answer. Off by default: it loads gigabytes of weights on
    # first use, and a component that heavy should start running because someone decided it
    # should. Needs no API key and no provider; the weights sit in crossbuy_ai/models/.
    intent_llm_enabled: bool = False

    # SPEECH TO TEXT (owner 2026-11-19). Which Whisper weights in models/whisper-<size>/ transcribe a voice
    # message: "small", "medium", or "auto" — medium for a short message (reads Arabic far better), small for a
    # longer one (twice as fast). On the processor only (int8); the route answers 503 when no weights are there.
    whisper_model: str = "auto"


@lru_cache
def get_settings() -> Settings:
    return Settings()
