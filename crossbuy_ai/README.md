# CrossBuy AI Service

Internal AI layer for CrossBuy. **Called only by the .NET backend** (never directly by
browsers/mobile). The .NET app applies user identity + permissions, then proxies to this
service. The AI here only proposes/classifies/extracts/forecasts — it is **never** the
source of truth.

- Framework: FastAPI (port `8000`)
- LLM: Claude API (cloud)
- See design: `../docs/CrossBuy_AI_Platform_Analysis.md` and
  `../docs/CrossBuy_AI_Implementation_Prompts.md`

## Phase 0 (current)

Infrastructure only — no AI capability yet:
- `GET /health` → open liveness probe.
- `GET /ping` → protected; requires header `X-AI-Secret` (returns 401 without it).

## Run (Windows / PowerShell)

```powershell
# from crossbuy_ai/
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt

# configure secrets
Copy-Item .env.example .env
#   then edit .env: set AI_SHARED_SECRET (and later ANTHROPIC_API_KEY)

# run
uvicorn app.main:app --host 0.0.0.0 --port 8000
```

## Verify

```powershell
# health is open -> 200
curl http://localhost:8000/health

# protected route without the secret -> 401
curl -i http://localhost:8000/ping

# with the secret -> 200
curl -i -H "X-AI-Secret: <your AI_SHARED_SECRET>" http://localhost:8000/ping
```
