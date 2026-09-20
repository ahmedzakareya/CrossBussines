# 07 — AI capabilities audit

What in CrossBuy is machine learning, what is a large language model, what is neither — and what
data is permitted to leave the building.

> **Method note.** The AI service was **read, not run.** Everything below is repository and
> configuration evidence. No AI call was made during this discovery, and no claim here has been
> verified against a live model.

---

## 1. The architecture

```
  Browser / mobile
        │   (never calls the AI service directly — by design)
        ▼
  CrossBuy .NET  ──  applies identity, permissions and the COMPANY boundary
        │
        │   AiEgressPolicy.EvaluateAsync(...)      ← the one chokepoint
        │   allow?
        ▼
  crossbuy_ai  (FastAPI, port 8000)
        ├── app/ml/anomaly.py    local scikit-learn   no LLM, no API key
        ├── app/ml/forecast.py   local               no LLM, no API key
        ├── app/ml/inventory.py  local               no LLM, no API key
        └── app/clients/anthropic.py                 present; see §5
```

The service's own README states the rule plainly:

> *"Called only by the .NET backend (never directly by browsers/mobile). The .NET app applies user
> identity + permissions, then proxies to this service. The AI here only proposes / classifies /
> extracts / forecasts — it is **never** the source of truth."*

## 2. What is actually shipped

### 2.1 Three statistical capabilities — no LLM involved

| Capability | Endpoint | What it does |
|---|---|---|
| **Journal anomaly scan** | `POST /api/ai/anomaly/journal-scan` | .NET gathers posted entries, company-scoped; Python flags possible anomalies for human review |
| **Cash-flow projection** | `POST /api/ai/forecast/cashflow` (`horizonDays`, default 90) | Opening cash + outstanding AR/AP with FIFO settlement mirroring the aging logic, projected forward |
| **Inventory analysis** | `POST /api/ai/inventory/analyze` (`slowDays`, default 90) | Per-item on-hand, value, outbound demand and reorder point → slow-moving / reorder / stockout-risk classification with suggested quantities |

Every module docstring says the same thing: *"local ML — no LLM, no API key"*. Dependencies are
`numpy`, `pandas`, `scikit-learn`.

**These are the product's real AI features, and they are statistics.** That is not a criticism —
anomaly detection and demand classification are exactly the problems where a model beats a
threshold, and doing them locally means no data leaves the machine. But anyone describing CrossBuy
as "AI-powered" should know that today the working capabilities are scikit-learn, not a language
model.

### 2.2 Three lookup tools, built for an assistant that is not yet here

| Endpoint | Returns |
|---|---|
| `GET /api/ai/accounts/search?q=` | Postable accounts matching `q`, **in the caller's company** |
| `GET /api/ai/costcenters/search?q=` | Active cost centres, in the caller's company |
| `GET /api/ai/cash-accounts` | Cash boxes and bank accounts with their GL account |

Described in the source as *"Read-only tools for the accounting-journal assistant (Phase 1.1) …
The AI proposes; these only let it look up real IDs to build a Draft."* The tools exist; the
assistant that would use them does not.

### 2.3 One connectivity probe

`POST /api/ai/diag` — and it has its **own egress purpose category**, with the reason written
down: it forwards *caller-supplied text*, which is a different risk from a computed business
payload and must be governable separately. A small decision that says a lot about the care taken.

### 2.4 In-app surfaces

`/Accounting/AiInsights` ("رؤى ذكية") is the user-facing screen, served by `AiInsightsService`.
`Crm/OpportunityInsights`, `Crm/AccountInsights`, `Crm/ScoringRules`, `Crm/AutomationRules` and
`Tasks/MatchSuggestions` are described as insight or automation surfaces; **whether each is
model-backed or rule-based was not established** in this discovery and should not be assumed
either way.

---

## 3. The egress boundary — the strongest thing in this audit

`BL/Platform/Ai/AiEgressPolicy.cs` is a single chokepoint. *"Every decision is a pure function of
the request plus server-owned configuration. Nothing here reads a header, a query string or a
body."*

### The matrix

|  | Internal | Approved external processor | Unapproved external |
|---|:---:|:---:|:---:|
| **OperationalMetadata** — counts, quantities, dates, ids, statuses | yes | yes | **no** |
| **FinancialAggregate** — money, balances, positions | yes | yes | **no** |
| **FreeTextBusinessContent** — anything a user typed | yes | **no** | **no** |
| **PersonalData** — about an identifiable person | **no** | **no** | **no** |
| **SyntheticTestData** — manufactured by a test, about nobody | yes | yes | **no** |

Both refusals are reasoned, not reflexive:

> **Free text is refused at an external processor even an approved one.** *"Free text can contain a
> customer name, a person, or a case reference, and an approved processor is approved for a
> PURPOSE — it is not a blanket licence to receive unbounded prose."* The trigger was a real
> finding in `JournalEntry.Description`.

> **Personal data is refused everywhere, including internal**, *"because no current AI purpose
> needs it. A future purpose that does must be added here deliberately, which is the point."*

### Fail-closed by construction

- Every enum's zero value is `Unknown`, so a default-constructed or partly-populated request is
  refused rather than inheriting the first real member's meaning. Purpose, destination and
  classification are each checked before anything else happens.
- **The company must come from the trusted context, not the request.** It is passed separately
  precisely so the two can be compared — *disagreement denies, never coerces*.
- The claim "approved external processor" arrives *on the request*, so the policy **re-derives it
  independently** rather than trusting the caller. The stated reason: hardening the resolver alone
  would have secured only the callers that use it, and the boundary's whole purpose is that it does
  not depend on its callers behaving.
- A `SyntheticTestData` label is not self-asserted — the payload must also pass an exact-match
  allowlist over a fixed shape, so relabelling real free text as synthetic fails at the content
  check.
- The payload is **measured before the decision**, and the decision taken **before the network** —
  an ordering the project asserts by test.
- A denial returns a synthetic 403 rather than throwing, so the three insight screens degrade
  exactly as they would for an unreachable service — **and zero outbound calls are made.**

### It is audited

`Platform.AiEgressAudit` is a persisted entity, and the audit table **stores the classification
name, not its numeric value**, so adding a member cannot reinterpret anything already written.

**Four purposes** exist and no more: `JournalAnomalyDetection`, `CashflowForecast`,
`InventoryAnalysis`, `ConnectivityDiagnostic`.

---

## 4. What leaves the building today: nothing

From `appsettings.json`, measured:

```
AiService:DestinationClass   Internal
AiService:DeploymentMode     LocalLoopback
AiService:BaseUrl            http://localhost:8000
AiService:Secret             <present>
```

The destination is **Internal** and the deployment mode is **LocalLoopback**. Every AI call goes to
a Python process on the same machine. The comment on the key records that *without* it the
resolver returns `UnapprovedExternal` — i.e. the default with no configuration is **deny**.

No approved external processor is configured, and reaching the external column additionally
requires a complete provider scope and an approved governance record — neither of which exists.

> **So: as configured, no customer data reaches any third party, including Anthropic.**

## 5. The Anthropic client

`crossbuy_ai/app/clients/anthropic.py` exists and `anthropic==0.112.0` is pinned in
`requirements.txt`. The README names Claude as the intended LLM and points at two design documents
(`docs/CrossBuy_AI_Platform_Analysis.md`, `docs/CrossBuy_AI_Implementation_Prompts.md`).

The README also describes the service as **Phase 0 — "Infrastructure only — no AI capability
yet"** — with `/health` open and `/ping` requiring an `X-AI-Secret` header. The ML routers have
since been added beyond that description, so the README is behind the code.

**The honest position:** the LLM path is wired, documented and governed, and is not switched on.
Turning it on is a governance decision (approve a processor, grant a provider scope), not an
engineering one — which is precisely what the egress policy was built to make true.

---

## 6. Findings

| | Finding |
|---|---|
| **Strength** | The egress boundary is the best-governed part of the product: one chokepoint, a written matrix, fail-closed enums, independent re-derivation of the caller's claim, an audit table that stores names not numbers, and an ordering asserted by test. Most products this size have no such boundary at all. |
| **Strength** | Three working capabilities run locally with no API key and no data leaving the machine. |
| **Strength** | A real defect was found and closed: AI endpoints once took `companyId` from the query string, over four tables with no global company filter, so `?companyId=7` returned another tenant's chart of accounts. The parameter is now gone from every signature. The remediation comment is the clearest security writing in the codebase. |
| **Gap** | The README describes Phase 0; the code is past it. A reader trusting the README will understate what ships. |
| **Gap** | The journal assistant's lookup tools exist with no assistant. Dead surface until Phase 1.1 lands. |
| **Unverified** | Whether the CRM insight, scoring and automation screens and `Tasks/MatchSuggestions` are model-backed or rule-based. Do not describe them as AI without checking. |
| **Unverified** | Nothing in this document was exercised against a running AI service. |
| **Decision pending** | The LLM path is one governance record away from live. Who approves a processor, and on what basis, is not recorded anywhere in the repository. |
