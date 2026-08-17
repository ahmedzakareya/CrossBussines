# AI Provider / Processor Decision Package

**Status:** READY FOR OWNER DECISION. No provider is selected, connected or approved.
**Audience:** Security · Data Protection / Legal · Infrastructure · Procurement · Business Owner.
**Contains no secrets, no credentials, and no compliance claims that are not evidenced.**

---

## 1. Executive summary

CrossBuy's AI security foundation is complete. Business AI runs **entirely inside the estate** and is
approved. Nothing reaches an external model today, and nothing can, because the policy denies by default.

What remains is **not an engineering task**. Ten facts about any prospective external provider can only be
answered by the business, and until they are, external processing stays blocked. This document is the
package needed to make that decision — and, once made, to record it in a form the code can check.

**The single question for the owner:** *do we want external LLM processing at all, and if so, under what
residency, retention and data-use terms?* If the answer is "not yet", CrossBuy keeps its approved local
ML and loses nothing.

---

## 2. Current AI architecture — verified in code

```
BUSINESS AI (approved)                         DIAGNOSTIC (development only)

  CrossBuy                                       CrossBuy (Development only)
     |                                              |
     v                                              v
  Egress Governance                              /api/ai/diag   [DevOnly] -> 404 in Production
     |                                              |
     v                                              v
  Hop-1 Security (LocalLoopback enforced)        Python
     |                                              |
     v                                              v
  Local Python ML                                Anthropic
     |                                              |
     X  NO EXTERNAL LLM                             X  NOT APPROVED
```

Verified this increment: only `app/routers/diag.py` imports the Anthropic client; no `app/ml/*` module
imports it or makes any network call; the `Diag` action carries `[DevOnly]`.

---

## 3. Internal vs external processing

| | Internal (approved) | External (blocked) |
|---|---|---|
| Routes | `/anomaly/journal`, `/forecast/cashflow`, `/inventory/analyze` | `/diag/echo` only |
| Destination | local Python ML on loopback | third-party LLM |
| Approval | **APPROVED INTERNAL PROCESSING** | **OWNER DECISION REQUIRED — denied** |
| Depends on a provider decision? | **No** | Yes |

**Local ML does not depend on external approval and never will** — enforced by test (PD17).

---

## 4–5. Mandatory provider facts and their owners

| # | Fact | Who can answer authoritatively |
|---|---|---|
| 1 | Provider legal entity | Procurement / Legal |
| 2 | CrossBuy account / project ownership | Infrastructure / Management |
| 3 | Commercial / enterprise tier | Procurement |
| 4 | Data used for provider training? | Data Protection / Legal + Security |
| 5 | Prompt / output retention | Data Protection / Legal |
| 6 | Data residency / processing region | Data Protection / Legal |
| 7 | Subprocessor position | Data Protection / Legal |
| 8 | Encryption in transit | **Engineering** (code-provable) |
| 9 | Deletion / lifecycle controls | Security + Data Protection |
| 10 | DPA / enterprise agreement | Legal / Procurement |
| 11 | *(additional, from code)* Owner requirement policy — residency, retention and training decisions must themselves be decided | Business Owner + Data Protection |

Requirement 11 is not in the original list of ten: the code requires the **business's own policy** to be
complete before any candidate can be measured against it. A candidate cannot pass a test nobody has set.

**Legal conclusions are never assigned to code.** Engineering owns exactly one row.

---

## 6. Evidence levels

| Level | Meaning | Example |
|---|---|---|
| `CodeProven` | Demonstrable from this repository's source | HTTPS enforced by `AiHopOnePolicy` |
| `ConfigProven` | Set in server-owned deployment configuration | processing region |
| `OwnerAttested` | A named person has attested it | account ownership |
| `ContractProven` | Written into a contract / DPA | residency commitment |
| `Unknown` | **Nothing is known. Blocks approval.** | "we probably have enterprise" |

`Unknown` is the zero value of the enum, so an unfilled record cannot read as satisfied.

---

## 7. Provider state machine

```
Unknown ──► UnderAssessment ──► OwnerDecisionRequired ──► ApprovedExternalProcessor
                  │                      │                          │
                  └──────────────────────┴──► Rejected              ├──► Suspended (expired)
                                                                     └──► Suspended (revoked)
```

**There is no `Unknown → Approved` edge**, asserted by test. Every state except
`ApprovedExternalProcessor` maps to `UnapprovedExternal`, so a caller that ignores the state still fails
closed.

---

## 8. Approval and revocation rules

Approval requires **both**, and neither substitutes for the other:

- every mandatory fact known **and** satisfying the owner's policy; **and**
- three separate recorded approvals — Security, Data Protection, Business Owner — with an approver, a
  date and an **expiry**.

An approval with **no expiry date is not an approval**: an approval nobody must re-confirm is how a
provider stays approved long after the contract it rested on has lapsed.

Approval is invalidated by: expiry, explicit revocation, contract/DPA removal, residency change,
retention change, data-use change, account-ownership change, or a security incident. Any of these →
`Suspended` → **external egress fails closed**.

**Configuration alone cannot approve a provider.** The evaluator's signature admits no `IConfiguration`,
no `HttpContext` and no environment — there is no file a developer can edit to turn approval on
(asserted by test).

---

## 9–10. Destination classification and the external data matrix

Approval answers *"may we use this processor at all"* — **never** *"may this payload go"*. Egress still
requires, independently:

`provider approved` **+** `purpose allowed` **+** `classification allowed` **+** `company valid` **+**
`permission valid` **+** `payload within size` **+** `hop-1 valid` **+** `credential present`.

| Classification | Internal | **Approved external** | Unapproved external |
|---|---|---|---|
| `OperationalMetadata` | allow | allow | **DENY** |
| `FinancialAggregate` | allow | allow | **DENY** |
| `FreeTextBusinessContent` | allow | **DENY** | **DENY** |
| `PersonalData` | **DENY** | **DENY** | **DENY** |
| `Unknown` | **DENY** | **DENY** | **DENY** |

Proven by test (PD12/PD15/PD16): an **approved** provider still cannot receive free text or personal data.

---

## 11. `JournalEntry.Description`

Remains **`FreeTextBusinessContent`** — not removed, not reclassified.

- **OPTION A (current):** keep it. External LLM anomaly analysis stays **denied**; local internal ML
  remains available and approved.
- **OPTION B:** the data owner authorises removing `Description` from the outbound DTO; the remaining
  payload would then be re-assessed as `FinancialAggregate`.

**Option B is not executed in this increment.** It is a data-owner decision.

> **2026-08-15:** a third option, **OPTION C (derived features)**, is defined and recommended in §26.8.
> Options A and B above are unchanged.

---

## 12. OWNER DECISION — data residency

> **What processing boundary will the business accept for CrossBuy AI data?**

Shape of the answer (this document does **not** choose): Kuwait only · GCC · Middle East · EU · a named
provider region · other explicitly approved region.

No candidate is claimed to satisfy any boundary; that requires evidence the owner supplies.

## 13. OWNER DECISION — provider-side retention

> **What is the maximum acceptable provider-side prompt/output retention?**

Required: `ZeroRetentionRequired` (yes/no), and if no, `MaximumRetentionDuration`, plus any deletion
requirement. CrossBuy's **internal** `AiProjection` retention is governed separately and already
implemented (TaskLifecycle 730 days, CalendarScheduling 400 days).

## 14. OWNER DECISION — training / data use

> **May CrossBuy business data be used to train provider models? YES / NO**

Until answered this is `Unknown` and external approval is **blocked**. The answer is never inferred.

## 15. OWNER DECISION — personal data

Current default: `PersonalData` → external → **DENY**.
Choose: **A. Never send personal data externally** · **B. Only under a future dedicated privacy-approved
policy.** **B is not enabled now.**

## 16. OWNER DECISION — free text

Current default: `FreeTextBusinessContent` → external → **DENY**.
Impact if this stays: no external processing of `JournalEntry.Description`, and by extension future
comments, task descriptions, emails, CRM notes and documents. **Not enabled in this increment.**

---

## 17. Provider scorecard (template — unpopulated by design)

No provider is ranked. Empty cells are `UNKNOWN`, and **`UNKNOWN` is not a low score — it is an absence
of evidence.** Ranking on guesses is exactly what this package exists to prevent.

> **2026-08-15 — this template is now POPULATED, in §26 below, from first-party provider
> documentation.** The template is kept here unchanged as the record of what was known before that
> evaluation existed. §26 ranks *documented capability*; it does **not** approve anyone, and the
> `UNKNOWN` discipline stated above still governs — see §26.6.

| Provider | Security evidence | Residency | Retention | Training / data use | Enterprise controls | DPA | Authentication | Operational fit | Integration complexity | Cost evidence | Decision |
|---|---|---|---|---|---|---|---|---|---|---|---|
| *(candidate 1)* | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |
| *(candidate 2)* | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN | UNKNOWN |

---

## 18. OWNER DECISION FORM

```
Selected provider candidate:      ____________________________________

Provider legal entity:            ____________________________________

Account / project owner:          ____________________________________

Commercial / enterprise tier:     ____________________________________

Approved processing region:       ____________________________________

Provider retention requirement:   ____________________________________
   Zero retention required?       YES / NO
   Maximum retention duration:    ____________________________________

Training / data use allowed:      YES / NO

Personal data external:           NO  /  FUTURE PRIVACY REVIEW

Free text external:               NO  /  FUTURE DATA-OWNER REVIEW

Subprocessor position accepted:   YES / NO

Deletion controls accepted:       YES / NO

DPA / contract verified:          YES / NO

Security approval:                YES / NO      by: ______________  date: __________

Data Protection approval:         YES / NO      by: ______________  date: __________

Business Owner approval:          YES / NO      by: ______________  date: __________

Decision date:                    ____________________

Review / expiry date:             ____________________   (REQUIRED — no expiry = not approved)
```

**Any mandatory field incomplete ⇒ Provider Approval = BLOCKED.**

---

## 19. Increment 5 — model integration ENTRY contract

A future model call may receive **only** this, and never an EF entity:

```
Trusted BusinessContext (authenticated user, resolved company)
  + current permission re-check
  + declared Purpose
  + safe typed DTO (named fields only)
  + Classification
  + Approved Destination (from the provider state machine)
  + Retention context
  + Correlation ID
        ↓
   Provider Gateway
```

## 20. Increment 5 — model OUTPUT contract

Model responses must be: **bounded in size** (the `MaxResponseBytes` mechanism already exists), typed and
validated, carrying provider metadata and the correlation id, with safe failure semantics — and
**carrying no business write authority and triggering no automatic execution**.

## 21. AI remains READ-ONLY

```
AI recommendation ──► human / existing workflow ──► authorized business service
```

**Never** `LLM ──► DbContext.SaveChanges()`. `SystemContextPolicy` remains `{ View }`. No AI write
authority in Increment 5 unless a separate programme explicitly approves it.

## 22. Provider failure model (future)

Timeout · unavailable · rate limit · auth failure · malformed response · oversized response · policy
denial · approval revoked → **fail safely**: no business data written, no company boundary crossed, and
**no automatic fallback to an unapproved provider**.

## 23. Provider Gateway architecture (future — not implemented)

```
AI application layer ─► Secure Retrieval ─► Egress Governance ─► Provider Gateway ─┬─► Provider A
                                                                                    ├─► Provider B
                                                                                    └─► Internal ML
```

The gateway interface belongs beside the other AI platform types. **Provider-independent and must stay
so:** classification, company isolation, permission re-check, retention, and the approval state check —
which happens *before* invocation, never inside an adapter. No adapters were built; speculative
abstractions were deliberately avoided.

---

## 24. INCREMENT 5 GO / NO-GO

- [ ] Provider selected
- [ ] Provider legal entity known
- [ ] Account / project ownership known
- [ ] Required contract / tier known
- [ ] Data residency satisfies owner requirement
- [ ] Retention satisfies owner requirement
- [ ] Training / data use satisfies owner requirement
- [ ] Subprocessor position accepted
- [ ] Encryption requirements satisfied
- [ ] Deletion controls accepted
- [ ] DPA / contract requirement satisfied
- [ ] Security approval recorded
- [ ] Data Protection approval recorded
- [ ] Business Owner approval recorded
- [ ] Provider review / expiry date recorded
- [ ] Provider state = `ApprovedExternalProcessor`
- [x] Egress Governance recognises approval **without** weakening classification rules
- [x] External free text remains denied unless separately approved
- [x] Personal data remains denied unless separately approved
- [x] Model integration input / output contracts are ready

**Current result: NO-GO** — 16 unchecked items, all owner decisions. The four checked items are the
engineering prerequisites, and they are complete.

---

## 25. Remaining blockers

| # | Item | Owner |
|---|---|---|
| 1 | Ten provider facts + the owner requirement policy | Security / Data Protection / Procurement / Business Owner |
| 2 | Deployed-server logging behaviour (infrastructure, not source) | Infrastructure |
| 3 | Service identity for `RemoteSecure` (mTLS / signed token) — only if Python is ever deployed remotely | Platform |

Separately tracked, **not** part of this decision: **Platform Kernel backlog** — 7 tables outside the
global company filters, and failed Timeline dispatch rows in the development database.

---
---

# 26. PROVIDER CANDIDATE EVALUATION — reviewed 2026-08-15

**Nothing above this line was rewritten.** Sections 1–25 are the package as it stood before any
candidate was researched, and they remain the record of that state. This section is additive.

**Review date / date all sources were accessed:** 2026-08-15
**Reviewer:** Engineering (CrossBuy platform)
**Expiry of this evaluation:** 2027-02-15 (6 months) — or immediately on any provider policy change.
**What this section is:** an evidence-based comparison of documented capability.
**What this section is NOT:** an approval. No provider is approved. `AiProviderState` remains
`Unknown` for every candidate, and no code change was made to any egress path.

## 26.1 Candidates evaluated

| # | Candidate | Data processor | First-party endpoint |
|---|---|---|---|
| 1 | Microsoft Azure OpenAI / Microsoft Foundry "models sold by Azure" | Microsoft | `https://<resource>.openai.azure.com` |
| 2 | OpenAI API | OpenAI | `https://api.openai.com` (regional: `https://<geo>.api.openai.com`) |
| 3 | Anthropic Claude API | Anthropic | `https://api.anthropic.com` |

Deliberately **out of scope**: Amazon Bedrock and Google Vertex/Agent Platform. On those platforms the
cloud provider — not the model vendor — is the data processor, which changes every fact in §4–5 and
would need its own assessment.

## 26.2 Sources (first-party only)

Every fact below is drawn from vendor-operated documentation. Community answers, blog posts, resellers
and forum threads were **excluded**; where only such a source existed, the fact is recorded `UNKNOWN`.

| Ref | Source | URL | Accessed |
|---|---|---|---|
| S1 | Microsoft — Data, privacy and security for models sold by Azure | `https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/data-privacy` | 2026-08-15 |
| S2 | Microsoft — Abuse monitoring | `https://learn.microsoft.com/en-us/azure/ai-foundry/openai/concepts/abuse-monitoring` | 2026-08-15 |
| S3 | Microsoft — Limited access (modified abuse monitoring eligibility) | `https://learn.microsoft.com/en-us/azure/ai-foundry/responsible-ai/openai/limited-access` | 2026-08-15 |
| S4 | Microsoft — Deployment types and data processing location | `https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/deployment-types` | 2026-08-15 |
| S5 | Microsoft — Region availability for models sold by Azure | `https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure-region-availability` | 2026-08-15 |
| S6 | Microsoft — Entra ID / managed identity authentication | `https://learn.microsoft.com/en-us/azure/ai-foundry/openai/how-to/managed-identity` | 2026-08-15 |
| S7 | Microsoft — Virtual networks, firewall and Private Link | `https://learn.microsoft.com/en-us/azure/ai-services/cognitive-services-virtual-networks` | 2026-08-15 |
| S8 | Microsoft — announcement of intent to establish an Azure region in Kuwait (2025-03-06) | `https://news.microsoft.com/en-xm/2025/03/06/microsoft-strengthens-partnership-with-kuwait-government-announces-intent-to-establish-ai-powered-azure-region-to-accelerate-ai-transformation-and-drive-economic-growth/` | 2026-08-15 |
| S9 | OpenAI — Your data (API data controls, ZDR, residency) | `https://developers.openai.com/api/docs/guides/your-data` | 2026-08-15 |
| S10 | OpenAI — Enterprise privacy / trust portal / DPA | `https://openai.com/enterprise-privacy/` · `https://trust.openai.com/` · `https://openai.com/policies/data-processing-addendum/` | 2026-08-15 |
| S11 | Anthropic — API and data retention (ZDR scope, eligibility, flagged content) | `https://platform.claude.com/docs/en/manage-claude/api-and-data-retention` | 2026-08-15 |
| S12 | Anthropic — Data residency (inference geo / workspace geo) | `https://platform.claude.com/docs/en/manage-claude/data-residency` | 2026-08-15 |
| S13 | Anthropic — Commercial data retention policy | `https://privacy.claude.com/en/articles/7996866-how-long-do-you-store-my-organization-s-data` | 2026-08-15 |
| S14 | Anthropic — Certifications (SOC 2 I & II, ISO 27001:2022, ISO 42001:2023) · Trust Center | `https://privacy.claude.com/en/articles/10015870-what-certifications-has-anthropic-obtained` · `https://trust.anthropic.com/` | 2026-08-15 |

**S10 caveat:** `openai.com/enterprise-privacy` returned HTTP 403 to automated retrieval. Its contents
are therefore recorded at a lower confidence than S9, which was retrieved in full. Any fact resting on
S10 alone is marked accordingly and must be re-verified by a human before it is relied on.

## 26.3 Storage residency vs inference residency

These are **different facts** and the distinction decides this evaluation. A provider can hold your data
at rest in one country while performing the actual model computation in another.

- **Storage residency** — where prompts, outputs, uploads and abuse-monitoring logs sit at rest.
- **Inference residency** — where the model computation happens, in memory, for the seconds it runs.

A commitment about the first is **not** a commitment about the second. Azure states both explicitly and
separately (S1, S4); OpenAI states which of its regions cover processing as well as storage (S9);
Anthropic exposes them as two independent settings with different available values (S12).

**Kuwait.** None of the three candidates operates inference in Kuwait. Microsoft has announced an
*intent* to establish a Kuwait region (S8, March 2025) — an intent is not a region, and there is no
published Azure OpenAI model availability for Kuwait. **A "data stays in Kuwait" requirement cannot be
satisfied by any of these three candidates today.** If that is the owner's requirement, the correct
outcome is to keep the approved local ML and select no external provider.

**UAE ≠ Kuwait.** UAE residency is a *different jurisdiction* with its own law, its own regulator and
its own cross-border transfer position. It may be materially closer than the US or the EU and it may
well be acceptable — but that is a legal determination for Data Protection / Legal, not an engineering
one, and this document does not make it. Recorded `UNKNOWN`.

## 26.4 Evidence table — the facts, per candidate

`UNKNOWN` below means *no first-party source established it*. It is never read as satisfied.

| Fact | Azure OpenAI | OpenAI API | Anthropic Claude API |
|---|---|---|---|
| Trains on customer prompts/outputs by default | **No** — "NOT used to train any generative AI foundation models without your permission or instruction" (S1) | **No** — "data sent to the OpenAI API is not used to train or improve OpenAI models (unless you explicitly opt in)" (S9) | **No** — "Retained data is never used for model training without your express permission" (S11) |
| Default prompt/output retention | Abuse-monitoring store, **up to 30 days**, in the customer's Azure geography (S1) | Abuse-monitoring logs, **up to 30 days**; per-endpoint application state varies (S9) | Conversation content **not retained by default**; commercial backend deletion within 30 days; Covered Models force 30 days (S11, S13) |
| Human review of flagged content | Yes by default; authorised Microsoft staff via SAW + JIT (S1, S2) | Not established from first-party source — `UNKNOWN` | Trust & safety flagging retains up to **2 years** regardless of ZDR (S11) |
| Zero / modified retention obtainable | **Conditional** — only "customers and partners managed by a Microsoft account team or under an eligible program" (S3) | **Conditional** — prior approval from OpenAI sales + additional requirements; not all endpoints eligible (S9) | **Conditional** — enabled per organisation by the Anthropic account team on request (S11) |
| Whether CrossBuy qualifies for that | **UNKNOWN** | **UNKNOWN** | **UNKNOWN** |
| Model vendor sees the data | **No** — models sold by Azure "do NOT interact with any services operated by providers … for example, OpenAI" (S1) | OpenAI **is** the processor | Anthropic **is** the processor |
| At-rest geography controllable | **Yes** — at rest always in the customer-designated Azure geography, for every deployment type (S1, S4) | Yes, for the supported residency regions (S9) | Workspace geo — **`"us"` only**, and it cannot be changed after workspace creation (S12) |
| Inference geography controllable | **Yes, conditionally** — Global = any region; Data Zone = US / EU / APAC only; Standard & Regional Provisioned = the deployment region (S4) | **Yes, conditionally** — US, Europe and **UAE** support regional *processing*; other regions are storage-only (S9) | **`"us"` or `"global"` only**; global "may be routed to select countries in the US, Europe, Asia and Australia" (S12) |
| Any Middle East option | **UAE North**, but see §26.5 | **UAE**, "requires additional approval" (S9) | **None** |
| Encryption in transit | HTTPS; enforced CrossBuy-side by `AiHopOnePolicy` (`CodeProven`) | HTTPS; same enforcement | HTTPS; same enforcement |
| Network isolation | **Private Link / private endpoint, default-deny firewall, VNet and IP rules** (S7) | Public internet only | Public internet only |
| Identity | Microsoft Entra ID, RBAC (`Cognitive Services OpenAI User`), managed identity when hosted in Azure (S6) | API key | API key, with workspace scoping and `allowed_inference_geos` policy (S12) |
| Contract / DPA | Microsoft Products and Services DPA governs (S1) | DPA available (S10) | DPA available via Trust Center (S14) |
| Independent attestations | Azure compliance offerings (broad) | ISO 27001 / 27017 / 27018 / 27701, SOC 2 Type 2 (S10) | SOC 2 Type I & II, ISO 27001:2022, **ISO 42001:2023** (S14) |
| Subprocessor list published | Via Microsoft trust documentation | Via trust portal (S10) | Trust Center → Subprocessors (S14) |

## 26.5 The UAE finding, stated precisely

This is the single most decision-relevant fact found, and it is easy to get wrong.

**Azure OpenAI in `uaenorth` (S5, 2026-07-24 revision of the region table):**

| Deployment type in `uaenorth` | What is actually offered | Where inference runs |
|---|---|---|
| Global Standard | gpt-4.1, gpt-4o, gpt-5.x and the rest of the catalogue | **any Azure region** — not UAE |
| Data Zone Standard | *Not available* — the data zones are US, EU and APAC only (S4) | n/a |
| **Standard / Regional** (pay-per-token) | **`text-embedding-3-large`, `text-embedding-3-small`, `text-embedding-ada-002`, `whisper` only — no chat/completion model** | UAE North |
| **Regional Provisioned Managed** (reserved PTU) | gpt-4.1, gpt-4o, gpt-5-mini, gpt-5.1, o1, o3-mini, o4-mini | UAE North |

**Therefore:** on Azure, in-region UAE inference for CrossBuy's chat-completion workloads is available
**only by purchasing Provisioned Throughput Units** — reserved capacity with a minimum commitment — and
**not** on the pay-per-token tier. Deploying a resource "in UAE North" and using Global Standard gives
UAE storage residency with *worldwide* inference. That configuration would satisfy a storage-residency
requirement and fail an inference-residency requirement, while looking identical in the portal.

**OpenAI**, by contrast, lists the UAE (`ae.api.openai.com`) among the regions that support regional
*processing* as well as storage, on pay-per-token, subject to "additional approval" (S9).

## 26.6 Hard gates

A gate is not a score. A failed gate cannot be compensated for by strength elsewhere.

| Gate | Requirement |
|---|---|
| **G1** | Provider does not train on CrossBuy business data, contractually, by default |
| **G2** | Provider-side retention is bounded, documented, and satisfies the owner's retention policy |
| **G3** | Inference and storage locations are individually knowable and constrainable to an owner-approved region |
| **G4** | Encryption in transit is mandatory and provable from CrossBuy's own code |
| **G5** | A DPA / contract can be executed with the CrossBuy legal entity, with published subprocessors |
| **G6** | CrossBuy's own invariants survive unchanged: company never caller-supplied, external egress restricted to `OperationalMetadata` / `FinancialAggregate`, credential server-held, deletion controls available |

| Gate | Azure OpenAI | OpenAI API | Anthropic Claude API |
|---|---|---|---|
| G1 | **PASS** (S1) | **PASS** (S9) | **PASS** (S11) |
| G2 | **CONDITIONAL** — 30-day abuse retention by default; removing it needs Microsoft-managed status (S1, S3) | **CONDITIONAL** — 30-day abuse logs; ZDR needs sales approval (S9) | **CONDITIONAL** — best default of the three, but flagged content retained up to 2 years regardless (S11) |
| G3 | **CONDITIONAL** — achievable for UAE only via PTU (§26.5) | **CONDITIONAL** — UAE processing available, subject to approval (S9) | **FAIL** against any Gulf/ME requirement — `us` / `global` only (S12) |
| G4 | **PASS** — `CodeProven` | **PASS** — `CodeProven` | **PASS** — `CodeProven` |
| G5 | **PASS** (S1) | **PASS** (S10, lower confidence) | **PASS** (S14) |
| G6 | **PASS** — CrossBuy-side, provider-independent, 173 governance tests green | **PASS** | **PASS** |
| **Owner-policy gate** | **BLOCKED** for all three — `AiProviderRequirement.IsComplete == false` until the business sets residency, retention and training policy | | |

Anthropic's G3 result is conditional on the owner's requirement. If the owner's approved region set
**includes the United States**, Anthropic's G3 becomes **PASS** — and its `usage.inference_geo` response
field plus workspace-level `allowed_inference_geos` make it the only candidate that returns
machine-checkable proof of where each individual request actually ran.

## 26.7 Hypothesis tests

The brief required that the favoured answer be attacked rather than confirmed.

### H1 — "Azure is automatically right for CrossBuy." — **SURVIVES, WEAKENED**

Three genuine challenges were found, and two of them bite:

1. **Azure's zero-retention path is the narrowest of the three.** Modified abuse monitoring is
   restricted to "customers and partners managed by a Microsoft account team or under an eligible
   program" (S3). OpenAI and Anthropic gate theirs on *sales approval*, which is not conditioned on
   account-management status in their published criteria. For a Kuwait-based ISV with no Microsoft
   account team, Azure may be the **hardest** of the three from which to obtain zero retention. This
   materially reduces the assumed Azure advantage on the heaviest-weighted axis.
2. **Azure's UAE advantage is not free.** §26.5: in-region UAE chat inference requires a PTU purchase.
   The claim "Azure is the only way to keep Gulf inference" is **false** — OpenAI offers UAE regional
   processing on pay-per-token. The claim "Azure is the cheapest way" is also **false** under a
   residency constraint.
3. **Azure's identity advantage degrades off-Azure.** Managed identity requires the caller to run in
   Azure (S6). CrossBuy runs on-premises, so the realistic credential is a service principal secret or
   certificate — closer to an API key than the headline suggests. Private Link likewise needs
   VPN/ExpressRoute from the Kuwait premises to an Azure VNet, which is real infrastructure work.

**What survives:** Azure still leads, but on **architecture and contract** — Private Link with public
network access disabled, Entra ID and RBAC instead of a bearer secret, Azure Policy to forbid
`GlobalStandard` deployments outright, the Microsoft DPA, and a subprocessor position where the model
vendor never sees the data. It does **not** lead by winning residency for free.

### H2 — "OpenAI direct is better, because it uniquely offers UAE inference without a PTU commitment." — **PARTLY SURVIVES**

True and material on residency and cost. It fails on the axes Azure wins: no private networking, no
federated identity, no policy engine, and OpenAI itself is the processor. "Requires additional
approval" also makes CrossBuy's actual attainability `UNKNOWN`.

### H3 — "Anthropic is fine, we already integrate it." — **REJECTED**

Explicitly rejected, on the brief's own instruction and independently on the evidence. The existing
`/diag/echo` integration is `[DevOnly]`, returns 404 outside Development, carries a fixed diagnostic
string and no business data. **Prior integration is not evidence of governance fit.** On the merits
Anthropic has the strongest documented retention default and the only AI-management-system
certification (ISO 42001), and it fails hardest on residency: `us` or `global` only, workspace geo
`us`, unchangeable after creation. Ranking it third is a residency judgement, not a security one.

### H4 — "Do nothing." — **REMAINS VIABLE AND IS NOT A FAILURE**

Local ML on loopback is approved, in production use, and provably independent of any provider decision
(test `PD17_local_ml_remains_usable_with_no_provider_approved`). Selecting no provider costs CrossBuy
no existing capability.

## 26.8 `JournalEntry.Description` — OPTION C

Restating §11: `Description` is `FreeTextBusinessContent`, and free text to an external destination is
DENY. Option A keeps that and forgoes external anomaly analysis. Option B asks the data owner to strip
`Description` from the DTO so the remainder re-assesses as `FinancialAggregate`.

**OPTION C — derived features.** `Description` is never sent and never reclassified. Instead CrossBuy
computes, inside the estate, a small typed feature set *about* the description and sends that:

```
JournalEntryId          (already OperationalMetadata)
DescriptionLength       int
DescriptionLanguage     enum { Arabic, English, Mixed, Unknown }
DescriptionCategory     enum, matched against a SERVER-OWNED controlled vocabulary
HasAttachmentReference  bool
+ the existing structured facts: amounts, account codes, dates, counterparty id
```

The model reasons over derived features and returns findings keyed by `JournalEntryId`. CrossBuy
re-joins the actual description locally, so the user still sees a fully contextual result.

**RECOMMENDED: Option C, with Option A remaining the standing default until Increment 5 implements C.**

Why C over B: it preserves the anomaly use case *without weakening a single rule*. No classification is
changed, no waiver is signed, and the guarantee becomes structural rather than procedural — the
minimized builder simply has no free-text field to emit, so the deny rule is never even reached. Option
B is rejected as the default because it converts a policy boundary into a per-DTO judgement call, and
the same argument would then be available for task descriptions, comments, CRM notes and email bodies.
Option C also carries a real cost that must be stated: a controlled vocabulary has to be authored and
maintained, and it will be less expressive than raw text. That is the price of not weakening the rule.

## 26.9 Weighted scorecard

Weights are as briefed. Scores are 0–5 and rank **documented capability only**. A score is *not*
approval, and no `UNKNOWN` was scored as a pass — unattainable-by-CrossBuy facts are held in the gates
in §26.6, not smuggled into a number here.

| Criterion | Weight | Azure OpenAI | OpenAI API | Anthropic Claude |
|---|---|---|---|---|
| Data governance (training, retention, subprocessor position) | 25% | 4.0 | 3.5 | **4.5** |
| Data residency (Gulf-proximate, inference *and* storage) | 20% | **4.0** | 3.5 | 1.0 |
| Security & networking | 15% | **5.0** | 2.0 | 2.0 |
| Identity & operations | 10% | **4.0** | 2.0 | 2.5 |
| Contract & compliance | 10% | **5.0** | 4.0 | 4.0 |
| Model capability (as constrained by residency) | 10% | 4.0 | **4.5** | **4.5** |
| Cost (under the residency constraint) | 5% | 2.5 | **4.5** | 4.0 |
| Portability / exit | 5% | **4.5** | 4.0 | 3.5 |
| **Weighted total** | 100% | **4.20** | **3.35** | **3.10** |

### Ranking

| Rank | Candidate | Outcome |
|---|---|---|
| **#1** | **Microsoft Azure OpenAI / Microsoft Foundry** | **RECOMMENDED** |
| **#2** | **OpenAI API** | **SECOND CHOICE** |
| **#3** | **Anthropic Claude API** | **CONDITIONAL** — conditional on the approved region set including the United States |

### Sensitivity analysis — the ranking is not robust to the residency decision

Re-scored on the assumption that the owner's approved region set **includes the United States**, so
residency stops being a discriminator:

| Criterion | Weight | Azure | OpenAI | Anthropic |
|---|---|---|---|---|
| Data governance | 25% | 4.0 | 3.5 | 4.5 |
| Data residency | 20% | 4.5 | 4.5 | 4.0 |
| Security & networking | 15% | 5.0 | 2.0 | 2.0 |
| Identity & operations | 10% | 4.0 | 2.0 | 2.5 |
| Contract & compliance | 10% | 5.0 | 4.0 | 4.0 |
| Model capability | 10% | 4.5 | 4.5 | 4.5 |
| Cost | 5% | 4.0 | 4.5 | 4.0 |
| Portability | 5% | 4.5 | 4.0 | 3.5 |
| **Weighted total** | 100% | **4.43** | **3.55** | **3.70** |

**Anthropic overtakes OpenAI for second place.** Azure stays first in both scenarios, but the gap
narrows and the #2/#3 order *inverts* purely on the residency answer. **This is why the residency
decision must be made before, not after, the provider is chosen** — and why the scorecard alone must
not be used to pick a provider.

## 26.10 What this evaluation deliberately does not claim

- It does **not** claim CrossBuy qualifies for Azure modified abuse monitoring, OpenAI ZDR, OpenAI UAE
  region access, or Anthropic ZDR. All four are `UNKNOWN` and all four are commercial conversations.
- It does **not** claim any candidate satisfies Kuwaiti law on cross-border transfer. Not an
  engineering determination.
- It does **not** claim pricing. No quotation was obtained; PTU minimums were not priced.
- It does **not** claim a UAE deployment is "in-region" unless the deployment type is
  Standard/Regional or Regional Provisioned Managed (§26.5).
- It does **not** convert a favourable document into a contractual commitment. Public documentation
  can change without notice; only the executed DPA binds. Hence the expiry date at the head of §26.

## 26.11 Effect on the §24 GO / NO-GO

Unchanged. This evaluation supplies **evidence for** owner decisions; it does not make them. Every one
of the 16 unchecked items in §24 remains unchecked, because each requires a named person to decide or a
contract to exist. **INCREMENT 5 REMAINS NO-GO.**

---
---

# 27. OWNER DECISION RESULT — recorded 2026-08-15

**Nothing above this line was rewritten.** §26 and everything before it stand as written.

**Decision date:** none — no decision was made.
**Approval status:** **NOT APPROVED. No provider selected.**
**Expiry:** not applicable — an approval that does not exist cannot expire.
**Authoritative register:** [`provider-owner-decision.md`](provider-owner-decision.md) §11.

## 27.1 Result

The owner-policy register was opened and **all eight decisions are `UNKNOWN`**. None of the three
required approvals is recorded. No decision, review or expiry date exists.

| Decision | Recorded |
|---|---|
| 1 External LLM processing | UNKNOWN |
| 2 Residency boundary | UNKNOWN |
| 3 Training / data use | UNKNOWN |
| 4 External personal data | UNKNOWN |
| 5 External free text | UNKNOWN |
| 6 Maximum provider retention | UNKNOWN |
| 7 DPA / contract requirement | UNKNOWN |
| 8 Provider candidate | UNKNOWN — none selected |

No value was inferred from §26's ranking. Azure OpenAI ranked first and was **not** selected; the
ranking is evidence for a decision, not the decision.

**`AiProviderState` for every candidate: `Unknown`.** `DestinationClass` → `UnapprovedExternal`.
`AiProviderRequirement.IsComplete` → `false`. **INCREMENT 5 = NO-GO.**

## 27.2 Provider fact match — not performed

A fact match requires a selected provider **and** a recorded policy. Neither exists, so the match was
not run rather than run against invented inputs. The per-provider evidence that would feed it is
already in §26.4–§26.6 and is unaffected by this outcome. The one provider-independent row —
encryption in transit — is `PASS`, `CodeProven`, because CrossBuy enforces it from its own side.

The Azure, OpenAI and Anthropic specific checks are conditional on Decision 8 and were not run.

## 27.3 FINDING — the governance model is not on the runtime path

Recorded because §9 of this increment's brief requires that owner approval must not be expressible as a
configuration value, and verifying that turned up a real gap.

**What §8 of this package claims:** *"Configuration alone cannot approve a provider. The evaluator's
signature admits no `IConfiguration`, no `HttpContext` and no environment."*

**That claim is true of `AiProviderEvaluator`, and it is narrower than it reads.** Verified this
increment:

- `AiProviderEvaluator` is referenced by exactly two files — its own definition and
  `AiProviderGovernanceTests`. **It is not called from any runtime code path.**
- The runtime egress path derives its destination class from configuration:
  `AiDestinationResolver.Resolve` reads `AiService:DestinationClass`
  ([AiEgressPolicy.cs:229-255](../../CrossBuy/BL/Platform/Ai/AiEgressPolicy.cs#L229-L255)).
- Therefore a deployment that sets `AiService:DestinationClass = "ApprovedExternalProcessor"` obtains
  the approved destination class **without any owner-approval record existing**.

**Current exposure: none, and the default is correct.** The tracked production template ships
`"__SET_ON_SERVER__"`, which does not parse to a member, so `Resolve` returns `Unknown` and egress
denies. Unset also denies. Reaching the approved class requires an operator to deliberately type the
value. Every other control still applies independently — the classification matrix (free text and
personal data remain denied), tenancy, permission, payload size, hop-1 validation and the credential
check.

**Why it is still a blocker for Increment 5.** The moment a real provider is approved and a real
endpoint is configured, `DestinationClass` becomes the single field standing between "governed" and
"sending", and the owner-approval register would have no runtime effect at all. That is the
configuration bypass §9 exists to forbid.

**Not fixed in this increment, deliberately.** This increment is a decision and documentation
increment; re-wiring the egress path is product code and belongs to the increment that has a provider
to wire. It is recorded here as a **mandatory precondition of Increment 5** rather than left to be
rediscovered.

**Required before Increment 5 may start (BLOCKER-1):** the egress path must consult a recorded owner
approval — an `AiProviderAssessment` produced by `AiProviderEvaluator` from a controlled governance
artifact — and `AiService:DestinationClass` must no longer be sufficient on its own to yield
`ApprovedExternalProcessor`.

> **CLOSED 2026-08-15 by Increment 4.3.** See §28. The bypass was first reproduced by test, then
> removed; `AiService:DestinationClass` can no longer produce approved-processor authority.

## 27.4 Recorded standing rules

| Rule | Status |
|---|---|
| No automatic provider fallback — a second provider needs its own approval and an explicit routing policy | **RECORDED** |
| On expiry: state → `Suspended`, external egress fails closed, local ML unaffected, no auto-extension | **RECORDED** |
| `PersonalData`, `FreeTextBusinessContent` and `Unknown` remain externally DENIED | **UNCHANGED** |
| `JournalEntry.Description` remains `FreeTextBusinessContent` | **UNCHANGED** |
| Option C (derived features) is a future option, not implemented | **RECORDED** |
| Local Python ML remains operational and independent of any external decision | **UNCHANGED** |

## 27.5 Increment 5 entry contract — held, not issued

Because the outcome is NO-GO, the bounded Increment 5 scope is **not** issued. It is held here so that
it is ready the moment the register closes, and so that its boundaries are agreed before there is
commercial pressure to widen them.

**Would-be entry path** (unchanged from §19–§20):

```
Trusted BusinessContext -> Secure Retrieval -> Current Permission -> Purpose
  -> Safe Typed DTO -> Classification -> Egress Governance
  -> ApprovedExternalProcessor -> Provider Gateway -> ONE approved provider adapter
```

Output: bounded · typed · validated · correlated · audited · **READ-ONLY**. No business mutation.

**Explicitly excluded from Increment 5 even after GO:** embeddings · vector database · RAG · chatbot ·
agents · autonomous actions · business writes · multi-provider fallback · Report Studio. Each belongs
to a later increment with its own assessment.

**Additional precondition:** BLOCKER-1 in §27.3.

## 27.6 Effect on §24

Unchanged. All 16 owner items in §24 remain unchecked. One engineering item is now **added** by §27.3
and is also unchecked.

**INCREMENT 5 = NO-GO.**

---
---

# 28. BLOCKER-1 CLOSED — Increment 4.3, 2026-08-15

**Nothing above this line was rewritten.** §27.3 stands as the record of the finding; this records the
remediation.

## 28.1 What was wrong

`AiProviderEvaluator` — the whole owner-approval state machine — was referenced by exactly two files:
its own definition and its own tests. **It was not on any runtime path.** The runtime derived its
destination class from one settings line, so `AiService:DestinationClass = "ApprovedExternalProcessor"`
conferred approved-processor authority with no candidate, no owner policy, no signatures and no expiry.

## 28.2 Proven before it was fixed

A temporary test (`Blocker1ProofTests`) asserted the **insecure** behaviour against the unmodified tree
and **passed on all three counts**:

| Proof | Result against the unmodified tree |
|---|---|
| Configuration alone yields `ApprovedExternalProcessor` | **PASSED** — bypass confirmed |
| The evaluator, asked independently, reports nobody approved | **PASSED** — `Unknown` |
| The egress boundary allows the external send and mints an approval token | **PASSED** — the defect |

The file was deleted after remediation. Keeping a test that expects insecure behaviour would leave a
green assertion that the hole is open. Its scenarios live on inverted, as `B1`–`B5`.

## 28.3 What changed

**Configuration states an intent. Governance grants the authority.** They were one field; they are now
two inputs, and only one is reachable from a settings file.

| Change | File |
|---|---|
| New `IAiProviderAuthority` + empty shipped governance record | `BL/Platform/Ai/AiProviderAuthority.cs` (new) |
| `Resolve` gains an authoritative 3-arg overload; the 1- and 2-arg overloads supply no authority and therefore can never return the approved class | `BL/Platform/Ai/AiEgressPolicy.cs` |
| The diagnostic reclassification rule now **assigns** instead of returning early, so it passes through the gate | `BL/Platform/Ai/AiEgressPolicy.cs` |
| `AiEgressPolicy` re-derives authority itself and denies a claim it cannot back | `BL/Platform/Ai/AiEgressPolicy.cs` |
| New deny reason `ProviderNotApproved` | `Models/Platform/AiEgressContracts.cs` |
| Authority registered as a singleton | `Program.cs` |
| Both production call sites pass the real governance state | `BL/AiInsightsService.cs`, `Controllers/Api/AiController.cs` |
| Template comment corrected — the key states an intent, not an approval | `appsettings.Production.json` |

**Two layers, not one.** Hardening the resolver alone would have protected only callers that use it;
the destination class arrives *on the request*, so a caller can put anything there. The boundary
therefore asks the governance record itself and does not trust what it was handed.

**Internal is deliberately not gated.** Loopback ML never involved a provider. Making a working,
approved, entirely-internal capability depend on an external approval nobody has made would have taken
it offline to govern something it does not use.

## 28.4 Approval is necessary, not sufficient

Adding a gate must not turn the other gates into a formality. Proven by test, with an approved
provider in place: free text still denied, personal data still denied, cross-company still refused,
missing credential still refused, invalid hop-1 still refused.

## 28.5 Verification

- Full suite: **2,357 passed · 189 skipped · 0 failed** (was 2,329 / 189 / 0 — net +28 tests, no
  regressions).
- Focused AI governance set: **300 passed · 0 failed · 0 skipped** (was 262).
- Solution builds clean.
- One existing assertion changed: the theory case asserting that configuring
  `"ApprovedExternalProcessor"` produces that class. It passed, and **that pass was the defect**. It is
  kept as `The_approved_class_cannot_be_configured_into_existence` rather than deleted, so the rule
  that replaced it is visible where the old one stood.

## 28.6 Effect on the owner decision

**None.** No owner decision was recorded, invented or implied. All eight decisions in
[`provider-owner-decision.md`](provider-owner-decision.md) §11 remain `UNKNOWN`; no provider is
selected; `AiProviderState` remains `Unknown`. No provider SDK, resource, credential or network call
was added.

This increment did not move CrossBuy closer to using an external provider. It made the distance
between "configured" and "approved" real.

**INCREMENT 5 = NO-GO** — unchanged, and now for the right reason: the owner register is empty, and
the runtime finally enforces that.

---
---

# 29. INCREMENT 4.3 — FINAL VERIFICATION, 2026-08-15

**Nothing above this line was rewritten.** §28 records the remediation; this records its verification.

## 29.1 `AiService:DestinationClass` — disposition

**Classification: TECHNICAL-HINT-ONLY.**

| It CAN | It CANNOT |
|---|---|
| Name the intended destination class for this deployment | Approve a provider |
| Select `Internal` — honoured only when `BaseUrl` is genuinely loopback | Make a remote host internal by labelling it so |
| Express the intent to use an approved external processor | Create the approval that intent depends on |
| Deny, by being unset, mistyped, or set to `UnapprovedExternal` | Override `Suspended`, `Rejected`, `UnderAssessment` or `Unknown` |
| — | Reactivate an expired approval or restore a revoked one |

Not `REMOVED`: the setting still carries real technical meaning — a deployment must be able to say
which destination it intends, and `Internal` vs external is a genuine per-deployment fact. Not
`DEPRECATED`: it is the current, correct mechanism for that fact. It is simply no longer an authority.

## 29.2 Runtime governance source — the seven questions

| # | Question | Answer |
|---|---|---|
| 1 | Concrete production implementation registered? | `AiProviderAuthority`, as a singleton in `Program.cs` |
| 2 | Where does its state come from? | Three `private static readonly` fields in its own source file — candidate, requirement, approval — evaluated by `AiProviderEvaluator` |
| 3 | Can configuration mutate the approval result? | **No.** Asserted structurally: the type's source contains no `IConfiguration`, `HttpContext` or environment reference |
| 4 | Can request input mutate it? | **No.** `Assess(DateTime)` takes only a clock value |
| 5 | Can environment variables mutate it? | **No.** Same assertion |
| 6 | What does it return with no approved owner record? | `AiProviderState.Unknown`, `IsApproved = false`, `DestinationClass = UnapprovedExternal` |
| 7 | How will a future approved record be supplied? | A reviewed source change to that one file, landing in the same commit as the signed register in `provider-owner-decision.md` §11. The evaluator re-checks every fact, so a partially-filled record still denies |

**No persistence was added** (§29 of the brief): no table, no migration, no SQL, no seed row. The
fail-closed source-controlled abstraction meets the required security property, so nothing heavier was
justified.

## 29.3 Startup behaviour — OPTION A (start · deny · warn)

When `DestinationClass = ApprovedExternalProcessor` but no provider is approved, the application
**starts**, external AI stays **denied**, and a warning names the mismatch and the governance state.

Option B (refuse to start) was rejected for three reasons, in order of weight:

1. It would take **local Python ML offline** — journal anomaly, cashflow forecast, inventory analysis
   are approved, internal, and have nothing to do with an external provider. Governing one thing by
   disabling another is not a control.
2. Crashing an ERP over an AI settings typo is worse than losing an optional analysis feature. This
   mirrors the hop-1 startup block directly above it in `Program.cs`.
3. Nothing is left permissive. The boundary already refuses on every request, independently. The
   warning only explains *why* — otherwise an operator debugs the wrong layer.

`CertificationDataGate` still throws, and that difference is deliberate: a production host serving
snapshot data is not something to warn about and continue through.

## 29.4 Logging

The refusal logs the governance **state** (a closed enum) and the request's purpose, destination class,
classification and byte count. It does **not** log the assessment's reason string — that can name a
candidate the business has not announced. No secret, no prompt, no `JournalEntry.Description`, no
personal data, no payload, no provider response body.

`CorrelationId` is absent from the provider-refusal line specifically, because the provider gate runs
**before** tenancy resolution and no context exists yet. Moving the gate later to obtain a correlation
id would delay a refusal for a logging convenience; the deny is recorded at the boundary either way.

## 29.5 Repository configuration scan

Scanned `*.json`, `*.yml`, `*.yaml`, `*.ps1`, `*.sh`, `*.cmd`, `*.bat`, `Dockerfile*`, `*.config`,
`*.bicep`, `*.tf` across the tree for `DestinationClass`, `ApprovedExternalProcessor`, `ProviderApproved`.

| Artifact | Occurrences | Disposition |
|---|---|---|
| `appsettings.Production.json` | 1 key + 2 comments | **Corrected** — labelled TECHNICAL HINT ONLY, with an explicit note that it grants no approval |
| `appsettings.json` (local, gitignored) | none | Unset ⇒ denies |
| `appsettings.Development.json` | none | Unset ⇒ denies |
| `launchSettings.json` | none | — |
| CI / pipelines / Docker / deployment scripts | none | — |
| `docs/DEPLOYMENT.md` | none | Mentions `AiService__Secret` only; no approval wording to correct |

**No production or deployment artifact instructs an operator that setting the key constitutes provider
approval.**

## 29.6 Test-source isolation

`AiTestProviderAuthority` lives in `CrossBuy.Tests` and is `internal` to that assembly. The product
does not reference the test project, so it cannot reach it — the isolation is a compile-time fact, not
a convention. The only `IAiProviderAuthority` in production DI is `AiProviderAuthority`, whose record
is empty and asserted empty for past, present and future dates.

## 29.7 Verification results

| Gate | Result |
|---|---|
| Debug build | **0 errors** · 686 warnings, **0 in any file this increment touched** |
| Release build | **0 errors** |
| TestRun build | **0 errors** |
| MSB3021 / MSB3027 | **0 / 0** in all three configurations |
| `CrossBuy.Tests` | **2,396 passed · 189 skipped · 0 failed** |
| `CrossBuy.Analyzers.Tests` | **102 passed · 0 skipped · 0 failed** |
| Focused AI matrix (18 suites) | **396 passed · 0 failed · 0 skipped** |

**One transient recorded honestly:** the first `CrossBuy.Analyzers.Tests` execution — run immediately
after a full `--no-incremental` solution rebuild — reported 1 failure. It did not reproduce in four
subsequent runs (102/102 each). That project contains **zero** references to anything AI, so it has no
dependency on any file this increment touched; the failure is attributed to build/assembly-load
contention, not to a regression.

## 29.8 Owner decision status — unchanged

All eight owner decisions remain **UNKNOWN**. All three approvals remain **NOT RECORDED**. No decision,
review or expiry date exists. `AiProviderState` remains `Unknown`. No provider selected, connected,
credentialled or called.

**AI FOUNDATION INCREMENT 4.3 = VERIFIED.**
**BLOCKER-1 = CLOSED.**
**INCREMENT 5 = NO-GO** — owner decision, not engineering.

---

## 29.9 Cross-reference — the owner decision instrument (added 2026-08-15)

The evidence in §26 is now carried into a decision instrument the owner group can act on without
reading source or this package:

| Document | Role |
|---|---|
| [`provider-owner-decision.md`](provider-owner-decision.md) §11 | **The register.** What has been decided. All eight decisions `UNKNOWN` |
| [`provider-owner-decision.md`](provider-owner-decision.md) §12 | **The instrument.** Each decision's question, choices, consequences, engineering position, and current answer; the comparison, unknown commercial facts, model mapping, effect matrix and checklist |
| [`provider-owner-decision-form.md`](provider-owner-decision-form.md) | **The input sheet.** One page, choices only, for circulation to the three approvers |

Flow is one-way: **form → register.** Answers returned on the sheet must be transcribed into §11 to
take effect; nothing is read from the sheet by the runtime.

§12.7 records **six domain-model gaps** — fields an owner can decide but the current model cannot store
(program-level "no external processing", personal-data policy, free-text policy, required deletion
behaviour, per-approver names and conditions, review date). None was fixed here; none blocks recording a
decision; **none is a security weakness** — every gap is a field the model cannot store, not a check the
runtime fails to make.

No finding in §26–§28 was altered. No owner field was populated.

---
---

# 30. OPENAI API — SELECTED CANDIDATE, NOT APPROVED

**Recorded 2026-08-15.** §26's ranking and §§26–29 findings are unchanged and remain the historical
record. This section assesses the candidate the owner has now selected.

> ## OPENAI API = **SELECTED** · **NOT APPROVED**
> Runtime state remains `Unknown`. External egress still fails closed. Selection changed the *subject*
> of the assessment, not its *outcome*.

**Ranking history preserved:** §26.9 ranked Azure #1, OpenAI #2, Anthropic #3-conditional — under a
Gulf-residency assumption. The owner has since recorded **Decision 2 = GLOBAL**, which removes
residency as a discriminator. §26.9's own sensitivity analysis anticipated exactly this: with residency
neutral the totals become Azure 4.43 · Anthropic 3.70 · OpenAI 3.55. **The owner selected OpenAI, which
is neither the #1 nor the #2 of that re-scoring.** That is the owner's prerogative — the scorecard ranks
documented capability, not business fit, and factors the ranking never carried (integration effort,
existing relationships, commercial terms, strategic direction) are legitimately the owner's to weigh.
It is recorded here for transparency, not as an objection.

## 30.1 Candidate record — evidenced facts only

| Domain field | Recorded value | Evidence level | Source |
|---|---|---|---|
| `ProviderName` | `OpenAI API` | OwnerAttested | Owner decision D8 |
| `ProviderId` | `openai-api` | OwnerAttested | Owner decision D8 |
| `LegalEntity` | **UNKNOWN** | Unknown | Contracting entity not established — Legal/Procurement |
| `AccountOwner` | **UNKNOWN** | Unknown | No organisation/project ownership evidence — Infrastructure |
| `CommercialTier` | **UNKNOWN** | Unknown | Not established — Procurement |
| `TrainsOnCustomerData` | **`false`** | ConfigProven (vendor documentation) | S9 — *"data sent to the OpenAI API is not used to train or improve OpenAI models (unless you explicitly opt in)"* |
| `ProviderRetention` | **UNKNOWN** | Unknown | Depends on whether CrossBuy is granted ZDR — see §30.3 |
| `ResidencyRegion` | `Global` | ConfigProven | S9 — default global processing; owner policy is GLOBAL |
| `SubprocessorPositionAccepted` | **UNKNOWN** | Unknown | Provider publishes a list; **CrossBuy has not accepted it** |
| `EncryptionInTransit` | **`Yes`** | **CodeProven** | `AiHopOnePolicy` mandates HTTPS for `RemoteSecure`; OpenAI API is HTTPS-only |
| `DeletionControlsAvailable` | **UNKNOWN** | Unknown | Controls documented; **not accepted by Security/Legal** |
| `ContractInPlace` | **UNKNOWN** | Unknown | DPA is *available*; **not executed or verified for CrossBuy** |

**Nothing account-specific is recorded.** Every fact that depends on OpenAI's answer about *this
customer* is `UNKNOWN` and stays that way until evidenced.

## 30.2 G2 — Training / data use gate

**Result: PASS (documentation-level), with a verification condition.**

OpenAI's API documentation states that data sent to the API is not used to train or improve OpenAI
models unless the customer explicitly opts in — a default in force since 1 March 2023. This is the
**API** position, deliberately assessed separately from consumer ChatGPT policy, which is a different
product with a different default.

| | |
|---|---|
| Default API training behaviour | **Excluded from training by default** |
| Opt-in | Exists and is explicit; CrossBuy must not opt in |
| Evidence level | `ConfigProven` — vendor documentation, not a contract term for CrossBuy |
| Owner requirement | "Training/data-use evidence must be verified before approval" |
| **Gate result** | **PASS** on documentation · the owner's *verification* condition is satisfied only when D7's DPA is executed and carries the term |

Recorded as `TrainsOnCustomerData = false`. A published page can change without notice; only the
executed contract binds it, which is why D7 exists.

## 30.3 G3 — ZDR gate. **THE DECIDING GATE.**

**Result: UNKNOWN / NOT VERIFIED → PRODUCTION PROVIDER APPROVAL = NOT READY.**

| Question | Answer | Source |
|---|---|---|
| Does ZDR exist for the OpenAI API? | **Yes** | S9 |
| Is it the default? | **No** | S9 |
| Does it require approval? | **Yes** — *"subject to prior approval by OpenAI"* | S9 |
| Scope | Organisation **or** project level, once enabled | S9 |
| Effect on `store` | Forced to `false` for `/v1/responses` and `/v1/chat/completions`, even if the request sets `true` | S9 |
| Abuse monitoring under ZDR | Customer content excluded from abuse-monitoring logs. **"Safety Retention" still permits retention and review when classifiers detect potential policy violations** | S9 |
| **Is ZDR enabled for the CrossBuy account?** | **UNKNOWN — NOT VERIFIED** | no evidence exists |

> **Documentation showing ZDR EXISTS is not evidence that CrossBuy HAS IT.**
> This distinction is the gate. It is not satisfiable by reading anything; it requires OpenAI to grant
> it to a named CrossBuy organisation or project, and evidence of that grant.

**The owner's retention policy was not adjusted to accommodate this**, and must not be. D6A/D6B stand
at ZDR-required / 0 days.

## 30.4 Normal vs exceptional retention — assessed separately, as D6C requires

**A. Normal application/request state**

| Endpoint likely to serve Increment 5 | Retained by default | Under ZDR |
|---|---|---|
| `/v1/chat/completions` | none, except audio outputs 1 h and prompt caching up to 24 h on GPU | none |
| `/v1/responses` | **30 days when `store=true` (the default)**; background mode ~10 min | `store` forced `false` → none |
| `/v1/embeddings` | none | none |

**Result: CONDITIONAL PASS — conditional on ZDR being granted.** Without ZDR, `/v1/responses` defaults
to `store=true` and retains 30 days, which **breaches D6B outright**. Increment 5 must therefore either
run under a granted ZDR arrangement or explicitly set `store=false`; the safe design is both.

**B. Exceptional retention (abuse / security / legal)**

Two mechanisms survive ZDR and are **not** normal application state:

1. **Safety Retention** — content may be retained and reviewed when classifiers detect potential policy
   violations.
2. **Legal obligation** — retention where required by law.

Under D6C these must be *documented, separately identified, reviewed by Security and Legal/Data
Protection, and accepted before production approval*. They are documented here and **NOT ACCEPTED** —
no such review has occurred.

**Explicitly not claimed:** that OpenAI physically deletes content at response completion. The
documentation states content is *excluded from abuse-monitoring logs* and that application state is not
persisted; it does not describe physical deletion timing, and this package does not assert one.

## 30.5 ZDR endpoint / feature compatibility — for Increment 5 design only

Nothing here is implemented. This exists so Increment 5 selects an API mode compatible with the
approved retention policy, rather than discovering the conflict after building.

| Feature | ZDR eligible | State persisted | Suitable for Increment 5 | Suitable for future RAG |
|---|---|---|---|---|
| `/v1/chat/completions` | **Yes** | none (audio 1 h; prompt cache ≤24 h) | **SUPPORTED** | n/a |
| `/v1/responses` | **Yes** (with `store=false`) | 30 days if `store=true` | **CONDITIONAL** — `store=false` mandatory | n/a |
| `/v1/embeddings` | **Yes** | none | **SUPPORTED** (not needed in Inc-5) | **SUPPORTED** |
| `/v1/moderations` | Yes | none | SUPPORTED | n/a |
| `/v1/completions` (legacy) | Yes | none | SUPPORTED — not preferred | n/a |
| `/v1/files` | **No** | until deleted | **NOT SUPPORTED** | **NOT SUPPORTED** |
| `/v1/vector_stores` | **No** | until deleted | **NOT SUPPORTED** | **NOT SUPPORTED** |
| `/v1/assistants`, `/v1/threads*` | **No** | until deleted | **NOT SUPPORTED** | **NOT SUPPORTED** |
| `/v1/conversations*`, chatkit threads | **No** | until deleted | **NOT SUPPORTED** | **NOT SUPPORTED** |
| `/v1/batches` | **No** | until deleted | **NOT SUPPORTED** | **NOT SUPPORTED** |
| `/v1/fine_tuning/jobs` | **No** | until deleted | NOT SUPPORTED | NOT SUPPORTED |
| `/v1/evals` | **No** | until deleted | NOT SUPPORTED | NOT SUPPORTED |
| Code Interpreter / File Search | **No** | until deleted | NOT SUPPORTED | NOT SUPPORTED |

> ### Forward-looking finding, worth surfacing now
> **OpenAI-hosted vector stores and the Files API are NOT ZDR-eligible.** A future RAG built on them
> would place CrossBuy business content in provider-side storage "until deleted" — a direct breach of
> D6A/D6B. If RAG is wanted later under this retention policy, the vector store must be **inside
> CrossBuy's estate**, with only ZDR-eligible embedding and completion calls crossing the boundary.
> That is a design constraint discovered before it was expensive, not a decision being made now.

## 30.6 Account, tier, contract, subprocessors, deletion controls

| Item | Provider-side | CrossBuy-side | Status |
|---|---|---|---|
| **Account / project owner** | n/a | not established | **UNKNOWN — OWNER/INFRASTRUCTURE ACTION REQUIRED** |
| **Commercial tier** | tiers exist | not established | **UNKNOWN — PROCUREMENT ACTION REQUIRED** |
| **DPA** | **AVAILABLE** — OpenAI publishes one and can execute it | not executed, not verified | **NOT RECORDED** |
| **Subprocessors** | **INFORMATION AVAILABLE** — list published via the trust portal | not reviewed, not accepted | **NOT RECORDED** |
| **Deletion / lifecycle controls** | documented per endpoint (§30.5); `expires_after` on files; manual deletion | not accepted | **NOT RECORDED — requires Security + Legal acceptance** |

**Account/project owner — exactly what must be supplied** (no secrets, no keys):

1. OpenAI **organisation id** and **project id**, with evidence of ownership;
2. the named **business owner** and **infrastructure owner** accountable for the account;
3. **environment separation** — distinct projects for development and production;
4. the **production project identity** that will carry the ZDR grant.

**Commercial tier — what depends on it:** ZDR approval, any regional-processing entitlement, the DPA
route (self-serve vs negotiated), and support/SLA terms. Procurement must verify the tier before any of
those four can be answered.

## 30.7 Residency treatment under Decision 2 = GLOBAL

Recorded as: **product-level residency requirement = GLOBAL**, provider residency capability =
**informational**. Geography does not block selection at product level.

**Residency is not removed from governance.** `AcceptableResidencyRegions` remains a per-requirement
list and the evaluator still rejects a provider outside it — so a future tenant or deployment can impose
a stricter boundary without any code change. Recorded for that future: OpenAI supports regional
processing in the US, Europe and the **UAE** (the last subject to additional approval), and
storage-only residency in several further regions. **None of that is claimed for CrossBuy.**

## 30.8 OpenAI provider fact matrix

| Fact | Owner requirement | OpenAI evidence | CrossBuy-specific status | Result |
|---|---|---|---|---|
| Provider legal entity | Required | n/a | Not established | **UNKNOWN** |
| Account / project owner | Required | n/a | Not established | **UNKNOWN** |
| Commercial tier | Required | tiers exist | Not established | **UNKNOWN** |
| Training / data use | Must NOT train | Not used to train by default (API) | Applies by default | **PASS** (doc-level) |
| Normal retention | 0 days | none for chat/embeddings; 30 d for `/v1/responses` if `store=true` | Depends on ZDR + `store` | **CONDITIONAL** |
| **ZDR** | **REQUIRED** | Exists; prior OpenAI approval required | **Not granted / not verified** | **UNKNOWN — HARD GATE FAILED** |
| Exceptional retention | Documented + Security/Legal accepted | Safety Retention + legal obligation | Documented, **not accepted** | **NOT ACCEPTED** |
| Residency | GLOBAL | Global by default | Satisfies product policy | **PASS** |
| Subprocessors | Position accepted | List published | **Not accepted** | **NOT RECORDED** |
| Encryption in transit | Required | HTTPS only | Enforced by `AiHopOnePolicy` | **PASS — CodeProven** |
| Deletion / lifecycle | No normal persistence | Documented per endpoint | **Not accepted** | **NOT RECORDED** |
| DPA / contract | Required + verified | **Available** | **Not executed, not verified** | **NOT RECORDED** |
| Owner requirement policy | Must be complete | n/a | **COMPLETE** — D1–D8 recorded | **PASS** |
| Security approval | Required | n/a | Not recorded | **NOT RECORDED** |
| Legal / Data Protection approval | Required | n/a | Not recorded | **NOT RECORDED** |
| Business Owner formal approval | Required | n/a | Policy given; signature metadata not supplied | **NOT RECORDED** |
| Decision date | Required | n/a | Not supplied | **NOT RECORDED** |
| Review date | Required | n/a | Not supplied | **NOT RECORDED** |
| Expiry date | **MANDATORY** | n/a | Not supplied | **NOT RECORDED** |

## 30.9 Hard gates G1–G10 — reported individually, not averaged

| Gate | Requirement | Result |
|---|---|---|
| **G1** | External processing permitted | **PASS** — D1 = YES |
| **G2** | No training on CrossBuy data | **PASS** (documentation-level; contract verification outstanding) |
| **G3** | ZDR / retention | **UNKNOWN — NOT VERIFIED.** The deciding gate |
| **G4** | Contract / DPA executed and verified | **NOT RECORDED** — available ≠ executed |
| **G5** | Encryption in transit | **PASS — CodeProven** |
| **G6** | Governance / classification invariants intact | **PASS** — personal data, free text, unknown, tenancy, permission, hop-1 all unchanged and test-proven |
| **G7** | Security approval | **NOT RECORDED** |
| **G8** | Data Protection / Legal approval | **NOT RECORDED** |
| **G9** | Business Owner formal approval | **NOT RECORDED** — policy decisions given; signature metadata not supplied |
| **G10** | Approval expiry date | **NOT RECORDED** |

**2 PASS · 1 PASS-with-condition · 1 UNKNOWN · 6 NOT RECORDED. Any one of the seven non-passes blocks
approval on its own.**

## 30.10 Real evaluator result

`AiProviderEvaluator.Evaluate` was run against the recorded owner requirement and the evidenced
candidate above — no invented facts, no recorded approvals — and pinned by test
(`OpenAiCandidateAssessmentTests`).

```
AiProviderState  = UnderAssessment
IsApproved       = false
DestinationClass = UnapprovedExternal
MissingFacts     = LegalEntity, AccountOwner, CommercialTier,
                   SubprocessorPositionAccepted, DeletionControlsAvailable, ContractInPlace
```

**`UnderAssessment`, not `OwnerDecisionRequired`.** The distinction is real and worth stating: the code
reaches `OwnerDecisionRequired` only when facts are missing *and* all three approvals are already
signed, or when every fact is satisfied and the signatures are outstanding. Here both facts and
signatures are incomplete, so the honest state is "still gathering evidence".

**`OwnerRequirementPolicy` is absent from `MissingFacts` for the first time** — `IsComplete` is now
`true` because D2, D3 and D6A were recorded. That is the one thing this increment moved, and it moved
inside the model rather than in prose.

## 30.11 Increment 5

**NO-GO.** Unchanged. The reason has changed shape: previously nothing had been decided; now the policy
is complete and the *provider evidence* is not.

---
---

# 31. INCREMENT 4.4 — ZDR RUNTIME EVIDENCE ENFORCEMENT

**Completed 2026-08-15.** §§26–30 are unchanged and remain the historical record, including §30.3's
statement that the ZDR gate could not be enforced. That statement was true when written; this section
records what changed.

## 31.1 BEFORE

The owner made Zero Data Retention a hard gate (Decision 6A). The model could not enforce it:

```csharp
Need(c.ProviderRetention.HasValue || r.ZeroRetentionRequired, "ProviderRetention");   // optional when ZDR required
if (r.ZeroRetentionRequired && c.ProviderRetention is { } zr && zr > TimeSpan.Zero)   // fires only when KNOWN
```

Nothing on the candidate could express *"ZDR has been granted to our account"*. The nearest substitute —
`ProviderRetention = 0` — describes a documented duration, so quoting a vendor web page was
indistinguishable from holding a grant.

### 31.2 Proven before it was fixed, and it was worse than reported

A temporary test file asserted the broken behaviour against the unmodified evaluator and **passed 4/4**:

| Case | Setup | Result on the unmodified tree |
|---|---|---|
| **A** | ZDR required · grant unknown · retention absent · **all other facts complete and all three approvals signed** | **`ApprovedExternalProcessor`** — fully approved with no ZDR evidence of any kind |
| **B** | ZDR required · retention known to be 30 days | `Rejected` — this half already worked |
| **C** | No candidate field could express an account-specific grant; `ProviderRetention = 0` **also approved** | "granted", "unknown" and "documented as zero" were indistinguishable |

> **Correction to §13.6 of the owner-decision document.** I recorded that gap as **MEDIUM** severity on
> the grounds that six other facts were missing so nothing could approach approval. That was true of
> OpenAI's record *on that day*, and it understated the defect. Case A shows that once those six facts
> arrive, **the ZDR gate was simply absent** — the provider would have been approved. The correct
> severity was **HIGH**, and the earlier assessment is corrected here rather than quietly restated.

The temporary file was deleted after remediation; a green test asserting an open hole is worse than no
test. Its cases live on as ZDR1, ZDR2, ZDR9 and ZDR10.

## 31.3 AFTER — the design chosen

**Option B: an explicit provider fact.** `AiProviderCandidate.ZeroRetentionGranted`, typed
`AiProviderFact` (Unknown / Yes / No) — the same three-valued type already used by
`SubprocessorPositionAccepted`, `EncryptionInTransit`, `DeletionControlsAvailable` and `ContractInPlace`.

**Why not Option A (make `ProviderRetention` unconditionally mandatory).** It would have closed the
"unknown passes" half and left the more important half open: it still conflates *the provider's
documented duration* with *a grant to our account*, so a deployment could satisfy the owner's hard gate
by recording a number read off a public page. §3 of the brief requires account/project-scoped semantics
and §7 forbids inferring a grant from a duration. Option A cannot do either. **Option A was rejected on
correctness, not on effort.**

**Smallest change that fits the current model.** No new enum, no new state, no new evaluator stage — the
fact slots into `MissingFacts` and `PolicyBreach` exactly as the ten existing facts do.

### 31.4 Evidence integration and the minimum acceptable level

A claim of "granted" is not self-supporting. When `ZeroRetentionGranted == Yes`, the evaluator requires
an `AiProviderEvidence` entry keyed `AiProviderEvidenceKeys.ZeroRetentionGranted` at
**`OwnerAttested` or `ContractProven`**.

| Level | Accepted | Why |
|---|---|---|
| `Unknown` | **No** | — |
| `CodeProven` | **No** | This repository cannot demonstrate what another company granted us |
| `ConfigProven` | **No** | Where a link to the provider's ZDR page would land — *"the provider offers ZDR"* is the exact claim that must not pass |
| `OwnerAttested` | **Yes** | Written provider confirmation, account-manager statement, support-ticket reference, console evidence — a named person stands behind it |
| `ContractProven` | **Yes** | A contract term |

The check is an explicit set, not `>= OwnerAttested`, so reordering the enum cannot quietly admit a
weaker level. **No secret, key or credential is ever evidence.**

### 31.5 Three-state behaviour

| Requirement | Grant | Result | State |
|---|---|---|---|
| ZDR required | **Unknown** | not approved — assessment incomplete | `UnderAssessment` (no signatures) / `OwnerDecisionRequired` (signed) |
| ZDR required | **No** | **REJECTED** — a known policy mismatch, final while it stands | `Rejected` |
| ZDR required | **Yes**, evidence too weak | not approved — `ZeroRetentionGrantedEvidence` missing | `UnderAssessment` |
| ZDR required | **Yes**, evidence sufficient | **gate passes** — every other gate still applies | continues |
| **ZDR not required** | any | **not a gate at all**; `MaximumProviderRetention` governs as before | unaffected |

**Both fail-closed states are correct and they say different things:** *"we are still gathering
evidence"* versus *"the humans signed and the evidence is still outstanding"*. The second is the more
uncomfortable one and the more important to surface.

### 31.6 The four retention concepts, kept distinct (§7)

| Concept | Question it answers |
|---|---|
| `ProviderRetention` | How long does the provider retain, per its documentation? |
| `MaximumProviderRetention` | What duration does the business accept? |
| `ZeroRetentionRequired` | Does the business demand zero? |
| `ZeroRetentionGranted` | **Has zero retention been granted to a named CrossBuy account, and can we show it?** |

Pinned by test: a documented zero **does not** imply a grant · an absent duration **does not** imply a
grant · a grant alongside a contradictory documented duration **still fails**.

`ProviderRetention` remains optional under a ZDR policy, and that is now correct rather than a hole: the
duration answers a question a ZDR arrangement does not turn on.

### 31.7 Exceptional retention (§8) — unchanged and still outstanding

ZDR is **not** modelled as "the provider can never retain any byte". Decision 6C separates normal
business request/response retention from exceptional legal, security and abuse retention. This increment
establishes only the former. **Safety Retention and legal-obligation retention remain subject to Security
+ Legal review, and remain NOT ACCEPTED.** A granted ZDR does **not** set
`DeletionControlsAvailable = Yes`; they are separate facts and both are required.

### 31.8 OpenAI — unchanged, and now visible in the evaluator's output

```
ZeroRetentionGranted = Unknown        <- recorded explicitly, not left to a default
AiProviderState      = UnderAssessment
MissingFacts         = AccountOwner, CommercialTier, ContractInPlace, DeletionControlsAvailable,
                       LegalEntity, SubprocessorPositionAccepted, ZeroRetentionGranted   (7, was 6)
```

**Nothing about OpenAI was verified, granted or upgraded.** The list grew by one because the gate became
real, which is the whole result of this increment. Item 1 of the verification intake
([provider-owner-decision.md §14](provider-owner-decision.md)) is now enforced by code as well as by
document.

### 31.9 Verification

| Gate | Result |
|---|---|
| Debug / Release / TestRun builds | **0 errors each** · MSB3021 = 0 · MSB3027 = 0 |
| Warnings | 686 / 676 / 686 — **0 in any file this increment touched** |
| ZDR1–ZDR16 matrix | **31 passed · 0 failed** |
| Focused governance (14 suites) | **356 passed · 0 failed · 0 skipped** |
| `CrossBuy.Tests` | **2,437 passed · 189 skipped · 0 failed** (was 2,406) |
| `CrossBuy.Analyzers.Tests` | **102 passed · 0 failed** |
| OpenAI calls · credentials · SDKs | **0 · 0 · 0** |
| UI · database | **unchanged · unchanged** |

**A duplicate fixture was found and removed.** Two files each held their own inline copy of the
"approved provider" record. Tightening the rule updated the shared builder and left both copies behind,
failing ~24 unrelated tests on a fixture that looked correct in the file anyone would think to edit.
Both now delegate to the single builder.

---
---

# 32. INCREMENT 4.9 — ENVIRONMENT / PROJECT-SCOPED PROVIDER GOVERNANCE

**Completed 2026-08-16.** §§26–31 are unchanged and remain the historical record. Nothing below revises
an earlier finding; it records a hole in the *shape* of the approval model that every earlier section
was written against.

## 32.1 BEFORE — approval was provider-global

Every governance artefact named a **provider** and nothing else. `AiProviderCandidate` carried
`ProviderId`, `ProviderName`, a legal entity, a tier and a set of facts; `AiProviderAuthority.Assess()`
took a clock and returned a verdict. The strongest sentence the model could express was:

```
"OpenAI is an ApprovedExternalProcessor."
```

That sentence is true of every organisation, every project and every environment simultaneously. Three
consequences followed, and all three were latent rather than triggered — the record approves nobody
today, which is the only reason none of them had been reached:

| # | Consequence | Why it matters |
|---|---|---|
| 1 | A **Development** approval authorises **Production** traffic | The development approval is the cheap one, obtained first, signed while the real review is still running. It would have carried production data. |
| 2 | An approval for **project A** authorises **project B** | Including a colleague's sandbox project in the same organisation. |
| 3 | A ZDR grant for the **development project** satisfies a **production** candidate | `AiProviderEvidence` recorded *what* was granted and never *for which account*. The evidence would have been genuine, correctly levelled, and about the wrong thing. |

Consequence 3 is the sharpest, because Zero Data Retention is **granted per organisation and project**
by the provider — see §31 and [`openai-zdr-verification-request.md`](openai-zdr-verification-request.md).
A grant is inherently scoped. The model that stored it was not.

A fourth hole sat beside them. Increment 4.2 established that an approval with **no expiry** is not an
approval. Its mirror — an approval with **no start date** — was still accepted. Such an approval is
valid retroactively for all time, so a record signed today silently covers a call made last month, and
"we approved this on the 14th" becomes unprovable.

## 32.2 AFTER — approval is bound to a four-part scope

```
AiProviderScope = ( ProviderId , OrganizationId , ProjectId , Environment )
```

`AiProviderEnvironment` is a typed enum — `Unknown = 0`, `Development`, `Production` — and `Unknown` is
the default because a default of `Development` would be the single most dangerous convenience in the
model: every unscoped caller would silently acquire whatever the development record allows.

Matching is **ordinal, exact, on all four parts**. There is no wildcard, no inheritance between
environments, and no case-insensitivity — project identifiers are opaque provider-issued strings, and
case-folding them would be a guess about the provider's identifier semantics.

**A Development approval cannot approve Production.** That is now a property of the code rather than a
sentence in a document, enforced independently at five places:

| Layer | Enforcement |
|---|---|
| `AiProviderEvaluator` | `OrganizationId`, `ProjectId` and `Environment` are **mandatory facts**. Absent → `MissingFacts` → never approved. |
| `AiProviderEvaluator` | ZDR evidence must carry a `Scope` that **covers the candidate's scope**. Unscoped evidence cannot establish a scoped fact — even at `ContractProven`, the strongest level. |
| `AiProviderAuthority` | `Assess(nowUtc, requested)` refuses an incomplete requested scope, then requires the recorded candidate's scope to **cover the request**. |
| `AiEgressPolicy` | An external request with no complete `ProviderScope` is denied with the new reason `ProviderScopeMissing` — **never defaulted**. |
| `OpenAiProviderAdapter` | The approval it was handed must carry a complete scope that **covers the scope this host is configured to call**. A Development token presented against a Production call is refused with `approval:scope-mismatch`, before any socket is opened. |

Backward compatibility deliberately does **not** mean unsafe compatibility: a caller that supplies no
scope for an external provider is **denied**, not treated as Development.

## 32.3 The new required evidence scope

`AiProviderEvidence` gained a nullable `Scope`. The rule is asymmetric on purpose:

- **`null` scope** — the evidence is not account-bound and therefore **cannot satisfy a scope-sensitive
  fact**. It remains usable for facts that are genuinely provider-wide.
- **A scope that does not cover the candidate** — the evidence is about a different account and is
  ignored for this candidate.

So the ZDR confirmation requested from OpenAI must now name the **organisation and the project** it was
granted for, and must be recorded against that scope. A single confirmation covering "CrossBuy" without
naming a project satisfies nothing.

## 32.4 Which facts are scope-sensitive, and which are not — §12 classification

Each fact is classified **A** (legitimately provider/organisation-wide), **B** (project/environment-
specific) or **C** (ambiguous). **Only the minimum was implemented**; the rest is recorded as the
required future evidence scope rather than silently assumed safe.

| Fact / approval | Class | Reasoning | Treatment now |
|---|---|---|---|
| `ZeroRetentionGranted` | **B** | ZDR is opt-in and granted to a named organisation/project. This is the one that was actively unsafe. | **Enforced scope-bound.** Evidence must cover the candidate's scope. |
| `ContractInPlace` (DPA) | **A** | A DPA is executed between two legal entities. It does not vary by project, and it covers projects created after signature. | Provider/organisation-wide. Unchanged. |
| `SubprocessorPositionAccepted` | **A** | The subprocessor list is a property of the provider's service, published organisation-wide. | Provider-wide. Unchanged. |
| `DeletionControlsAvailable` | **C → treated as A, with a stated limit** | The *availability* of deletion controls is a product property (A). Whether they are *configured and proven* for a given project is (B) — but that is an operational verification, not a fact the provider states. | Provider-wide today. **Required future evidence scope:** per-project proof of deletion, supplied as scoped evidence when Item 2 completes. |
| `EncryptionInTransit` | **A** | A transport property of the endpoint. | Provider-wide. Unchanged. |
| `ResidencyRegion` | **C → treated as A under Decision 2** | Residency *can* be project-configurable at some providers. Decision 2 accepts **global** residency, so no project-level distinction is currently material. | Provider-wide. **If Decision 2 is ever narrowed, this becomes B and must be re-scoped.** |
| `TrainsOnCustomerData` | **A** | A provider-wide policy statement. | Provider-wide. Unchanged. |
| **Security approval** | **B** | Security signs off on a deployment: its network position, its credential handling, its blast radius. A development sandbox and a production project are different deployments. | **Bound to scope** through the record's candidate. A signature covers one scope. |
| **Data Protection / Legal approval** | **B** | Legal signs off on what personal and business data may flow to a *specific* processing arrangement. Development and Production carry different data. | **Bound to scope.** |
| **Business Owner approval** | **B** | The business owner accepts cost and operational risk, both of which differ by an order of magnitude between environments. | **Bound to scope.** |
| **Owner POLICY** — external LLM allowed, training prohibited, PersonalData denied, FreeText default-deny, ZDR required, DPA required | **A** | This is CrossBuy's own standard, not a provider fact. It does not vary by environment, and duplicating it per environment would invite a weaker development policy — the exact thing this increment exists to prevent. | **Remains global.** Not duplicated. |

The distinction the classification rests on: **POLICY** is what CrossBuy requires, **EVIDENCE** is what
the provider states, and **APPROVAL** is what a named human signed. Policy is global; evidence is scoped
where the provider scopes it; approval is scoped always.

## 32.5 Validity window — both ends now required

| Rule | State | Note |
|---|---|---|
| No `ExpiresAtUtc` | `OwnerDecisionRequired` | Unchanged from Increment 4.2. Checked **first**: it is the older and more consequential rule, so when both ends are absent that is the reason reported. |
| No `ValidFromUtc` | `OwnerDecisionRequired` | **New.** |
| `ValidFromUtc >= ExpiresAtUtc` | `Rejected` | A window that does not start before it ends authorises nothing and is almost certainly a transcription error. Rejected rather than read as expired: the record is *wrong* and needs re-issuing, not re-confirming. |
| `now < ValidFromUtc` | `OwnerDecisionRequired` | Recorded but not yet in force. |
| `now >= ExpiresAtUtc` | `Suspended` | Unchanged. Suspended, not Rejected — approval once existed, and an audit of what was sent and when must be able to tell those apart. |

## 32.6 Provider switch — availability split by environment

`Ai:Providers:{id}:Enabled` armed Development and Production together, so "we only enabled it for dev"
was not a true statement about the configuration. The switch is now **two keys, both required**:

```
Ai:Providers:OpenAI:Enabled                 = false      <- master; off here is off everywhere
Ai:Providers:OpenAI:Development:Enabled     = false
Ai:Providers:OpenAI:Production:Enabled      = false
```

This is **defence in depth, not authority**. With all three set to `true`, external egress is still
refused by the governance record. An `Unknown` environment is never enabled.

## 32.7 Configuration architecture — Dev and Prod project ids are separated by FILE

| Setting | Where | Value today |
|---|---|---|
| Production `OrganizationId` / `ProjectId` | tracked `appsettings.Production.json` | **empty** |
| Development `OrganizationId` / `ProjectId` | gitignored per-machine `appsettings.Development.json` | **empty** |
| `OpenAi:Environment` | per file | `Production` / `Development` |
| API key | `OPENAI_API_KEY` environment variable, development machine only | present locally, never in any file |

Neither deployment can read the other's project identifier; there is no single key holding both. The
values are left **empty rather than placeholdered**, and that is a security decision rather than
untidiness: an empty identifier makes the scope **incomplete**, which is refused before any call is
attempted, whereas a dummy such as `__SET_ON_SERVER__` would read as a real identifier and make an
incomplete scope look complete.

`OpenAi:Environment` is parsed as a typed value **and is never derived from `ASPNETCORE_ENVIRONMENT`**.
The deployment saying "I am production" must not be the same statement as "production is approved" —
that is B15's rule, extended to the new field.

## 32.8 OpenAI — no closer to approval

The model became stricter, so the gap became **larger and more visible**. Nothing was verified, granted
or upgraded.

```
AiProviderState  = UnderAssessment
OrganizationId   = UNKNOWN        <- now an explicit MissingFact, not an absent concept
ProjectId        = UNKNOWN        <- now an explicit MissingFact
Environment      = Unknown        <- now an explicit MissingFact
ZeroRetention    = Unknown        (unchanged, §31)
MissingFacts     = AccountOwner, CommercialTier, ContractInPlace, DeletionControlsAvailable,
                   Environment, LegalEntity, OrganizationId, ProjectId,
                   SubprocessorPositionAccepted, ZeroRetentionGranted        (10, was 7)
```

The three new entries are the increment's result, not a regression: the record was always silent about
which account it described, and silence now reads as missing rather than as irrelevant.

## 32.9 Verification

| Gate | Result |
|---|---|
| `SCOPE1`–`SCOPE25` matrix (new) | **30 passed · 0 failed** |
| `CrossBuy.Tests` | **2,640 passed · 195 skipped · 0 failed** (was 2,610) |
| `CrossBuy.Analyzers.Tests` | **102 passed · 0 failed** |
| Database changes | **none** — provider governance remains source-controlled |
| CrossBuyDB2 writes · Production writes | **0 · 0** |
| Real OpenAI calls · credits consumed | **0 · 0** |
| Provider switch | **not enabled**, in any environment |

---
---

# 33. INCREMENT 4.10 — REAL ACCOUNT SCOPE REGISTRATION + SYNTHETIC-ONLY DEVELOPMENT

**Completed 2026-08-16.** §§26–32 are unchanged. This section records the first facts about OpenAI that
anyone has actually established, and it records that establishing them changed nothing about approval.

## 33.1 The identifiers — established, and what that does and does not mean

Verification Item 1A returned the account context. These are **identifiers, not secrets**: they name an
account, they do not authenticate to it, and they are inert without the API key — which appears in no
file, no test and no document, and whose value is never read.

| | |
|---|---|
| Organisation | `org-aUyGCBpWvfaqal2KIUuYkU9t` |
| Development project | `proj_GHPPdHzQWFh6VhXkPXV2dp7D` |
| Production project | `proj_tuL0VF1UOGgbPD1JX7wXW4k8` |

**The missing-fact list went DOWN for the first time — from 10 to 7.** `OrganizationId`, `ProjectId` and
`Environment` are established. The seven that remain each require a human to decide or the provider to
confirm, and none of them moved:

```
AccountOwner · CommercialTier · ContractInPlace · DeletionControlsAvailable
LegalEntity · SubprocessorPositionAccepted · ZeroRetentionGranted
```

`AiProviderState` is still `UnderAssessment`. The governance record still approves nobody.

## 33.2 A latent defect the identifiers exposed

The documentary OpenAI candidate carried `ProviderId = "openai-api"`, while the runtime scope uses
`OpenAiOptions.ProviderId` = `"OpenAI"`. Nothing had ever failed, because the candidate was documentary
and the runtime record is empty — but `ProviderId` is one of the four parts of a scope, so the day
someone transcribed this candidate into `AiProviderAuthority` **it would have covered no request at
all**, and the denial would have read as a governance decision rather than a typo. Corrected, and pinned
by a test that compares the documented candidate's scope against the scope a configured deployment
would request.

## 33.3 Configuration — the two projects, separated by file

| | Development | Production |
|---|---|---|
| File | `appsettings.Development.json` (gitignored, per-machine) | `appsettings.Production.json` (tracked) |
| `OpenAi:OrganizationId` | set | set |
| `OpenAi:ProjectId` | the **Development** project | the **Production** project |
| `OpenAi:Environment` | `Development` | `Production` |
| `Ai:Providers:OpenAI:Enabled` | **false** | **false** |
| `…:Development:Enabled` / `…:Production:Enabled` | **false** | **false** / **false** |
| API key | `OPENAI_API_KEY`, this machine only | not present, not created |

No file holds both project identifiers, and a test asserts that each environment's file contains its own
and not the other's. No second configuration model was introduced — `OpenAiOptions` already carried the
scope from Increment 4.9.

## 33.4 The synthetic-only development constraint — recorded as a CHECK

The owner recorded that a controlled OpenAI Development smoke test may carry **fully synthetic content
only**. Prohibited: personal data; customer, vendor or employee names; email addresses, phone numbers,
addresses; real invoice numbers; real journal descriptions; free-text business content; uploaded
documents; production or CrossBuyDB2 content; credentials, secrets, tokens and connection strings.

Implemented as `AiDataClassification.SyntheticTestData` plus `AiSyntheticPayload`:

- **The classification alone is worth nothing**, and that is the design. A label is chosen by the caller,
  and `SyntheticTestData` is permitted at an external destination while `FreeTextBusinessContent` is not
  — so if the label were taken at face value, relabelling real journal descriptions would carry them
  past the matrix. Any payload carrying it must also **pass an exact-match allowlist** at the adapter,
  before a socket is opened.
- **Allowlist, not denylist.** The payload must match one fixed, closed shape: five known properties,
  each equal to a compile-time constant. An extra property is rejected by
  `UnmappedMemberHandling.Disallow` — that is where a smuggled field would go. A denylist has to imagine
  every leak; an allowlist only has to describe one payload.
- **No route to any data.** `Build()` takes no parameters at all — no `DbContext`, no configuration, no
  caller content — so there is no channel through which real data could arrive. The guarantee is
  structural, not a rule someone has to remember. Tests assert this by reflection and over the source.

> **This decision is not evidence and not approval.** It is not ZDR, not Legal, Security or Business
> Owner approval, not production authorisation, and not permission to mark OpenAI an approved external
> processor. An external send of synthetic content is refused by the governance record exactly as any
> other external send is — proved by `ID13`.

## 33.5 The readiness report — four answers, never one

`OpenAiDevelopmentReadiness` reports **CONFIGURED**, **ENABLED**, **APPROVED** and **CALL PERMITTED** as
four independent results over fifteen gates. Collapsing them into one boolean would rebuild BLOCKER-1 in
a new place: it would be true whenever the easy half was done, and the easy half is configuration.

Actual output for the Development scope, with both switches on and a key present:

```
CONFIGURED=True  ENABLED=True  APPROVED=False  CALL-PERMITTED=False  (authority state: Unknown)
  [OK ] ProviderScopeComplete · OrganizationIdConfigured · ProjectIdConfigured · EnvironmentStated
  [OK ] ApiKeyPresent · ProviderSwitchEnabled · RateLimitConfigured · DurableAuditReady
  [OK ] SyntheticPayloadGuardReady
  [NO ] ProviderApproved · ZeroRetentionEvidence · SecurityApproval
  [NO ] LegalDataProtectionApproval · BusinessOwnerApproval · PricingConfigured
  => BLOCKED: ProviderApproved — Unknown — No provider candidate has been supplied.
```

For the Production scope the first blocker is earlier still — `ProviderSwitchEnabled` is off, and stays
off. The report is a **pure report**: nothing consults it before a send, and it can approve nothing.

## 33.6 Verification

| Gate | Result |
|---|---|
| `ID1`–`ID18` | **18 passed · 0 failed** |
| Synthetic payload guard | **19 passed · 0 failed** |
| `SCOPE1`–`SCOPE25` · `ZDR1`–`ZDR16` · `DEV1`–`DEV15` · `B14`–`B16` · `RT1`–`RT22` | **all green, none weakened** |
| `CrossBuy.Tests` | **2,684 passed · 195 skipped · 0 failed** |
| Real OpenAI HTTP calls · credits | **0 · 0** |
| Database changes | **none** |
