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


@lru_cache
def get_settings() -> Settings:
    return Settings()
