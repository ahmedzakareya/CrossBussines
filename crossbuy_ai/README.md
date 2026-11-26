# CrossBuy AI Service

Internal FastAPI service called by the CrossBuy .NET backend. It does not authenticate end users
or own business permissions: .NET filters the question catalogue and checks company/module access
before data is read. The Python service classifies questions and computes local predictions; it is
not a business system of record.

## Current capabilities

- Local question intent classification (`/understand`) over the permission-filtered catalogue.
  Arabic and English use the trained local classifier when its catalogue matches. French uses the
  runtime matcher with French examples; unsupported or ambiguous questions are refused.
- Local analytics endpoints for anomaly review, cash-flow projection, inventory analysis and
  reorder advice. These analyze request data and do not learn persistently from user activity.
- Local speech transcription when Whisper weights are installed under `models/whisper-small` or
  `models/whisper-medium`.
- The local Qwen intent/composition model is present but disabled by default because measured
  latency and concurrent-load behavior were unsuitable for this development machine.
- `/diag/echo` is a separate, optional Anthropic connectivity diagnostic. It requires an API key;
  normal local analysis and question understanding do not call it.

Question-model training is an explicit local operation using generated examples and a reviewed
benchmark. The service does not update the model from live conversations. Additional examples may
be added to `data/reviewed_questions.jsonl` only after human review and labelling; the trainer
rejects unapproved, unknown, duplicate, conflicting, and oversized entries. A candidate is
published atomically only after it passes the independent real-question release gates. Business
data used for an answer comes through the governed .NET surfaces, not from an internet search or
an external provider.

## Run locally

From this directory in PowerShell:

```powershell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
Copy-Item .env.example .env
# Set AI_SHARED_SECRET to the same development secret configured in the .NET app.
uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Keep the service bound to loopback for local development. Production deployment must follow the
platform's AI destination and egress governance rather than this development command.

## Verify the service boundary

```powershell
curl http://localhost:8000/health
curl -i http://localhost:8000/ping
curl -i -H "X-AI-Secret: <your AI_SHARED_SECRET>" http://localhost:8000/ping
```

`/health` is an unauthenticated liveness probe. `/ping` and the AI routes require `X-AI-Secret`.
