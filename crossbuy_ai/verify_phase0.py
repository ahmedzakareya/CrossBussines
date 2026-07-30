"""Phase 0 verification — exercises the real ASGI app via TestClient.

Asserts: /health open (200), /ping rejects without secret (401), accepts with it (200).
"""
from fastapi.testclient import TestClient

from app.config import get_settings
from app.main import app

client = TestClient(app)
secret = get_settings().ai_shared_secret

r = client.get("/health")
assert r.status_code == 200 and r.json() == {"status": "ok", "service": "crossbuy-ai"}, r.text
print("PASS /health ->", r.status_code, r.json())

r = client.get("/ping")
assert r.status_code == 401, r.text
print("PASS /ping (no secret) ->", r.status_code)

r = client.get("/ping", headers={"X-AI-Secret": "wrong"})
assert r.status_code == 401, r.text
print("PASS /ping (wrong secret) ->", r.status_code)

r = client.get("/ping", headers={"X-AI-Secret": secret})
assert r.status_code == 200 and r.json() == {"status": "authorized"}, r.text
print("PASS /ping (valid secret) ->", r.status_code, r.json())

print("\nALL PHASE 0 CHECKS PASSED")
