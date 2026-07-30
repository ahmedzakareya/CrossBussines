"""Phase 0.2 verification (offline — no API key required).

Checks: app + client import cleanly, model routing maps tiers to the right IDs,
/diag/echo is secret-guarded (401), and a keyless call returns 503 (not a crash).
A real Arabic completion requires ANTHROPIC_API_KEY and is tested separately.
"""
from fastapi.testclient import TestClient

from app.clients.anthropic import _model_for
from app.config import get_settings
from app.main import app

client = TestClient(app)
secret = get_settings().ai_shared_secret
settings = get_settings()

assert _model_for("fast") == settings.fast_model, _model_for("fast")
assert _model_for("smart") == settings.default_model, _model_for("smart")
print("PASS model routing -> fast:", _model_for("fast"), "| smart:", _model_for("smart"))

r = client.post("/diag/echo", json={"message": "hi", "tier": "fast"})
assert r.status_code == 401, r.text
print("PASS /diag/echo (no secret) ->", r.status_code)

r = client.post("/diag/echo", headers={"X-AI-Secret": secret}, json={"tier": "fast"})
if not settings.anthropic_api_key:
    assert r.status_code == 503, r.text
    print("PASS /diag/echo (no API key) -> 503 graceful:", r.json()["detail"])
else:
    assert r.status_code == 200, r.text
    body = r.json()
    print("PASS /diag/echo (live) ->", body["model"], "| reply:", body["reply"][:60])

print("\nALL PHASE 0.2 OFFLINE CHECKS PASSED")
