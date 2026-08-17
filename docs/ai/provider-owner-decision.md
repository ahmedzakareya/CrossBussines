# CrossBuy — AI Provider Owner Decision

**Prepared for:** Business Owner · Security · Data Protection / Legal · Infrastructure · Procurement
**Prepared by:** Engineering (CrossBuy platform)
**Date prepared:** 2026-08-15
**Valid until:** 2027-02-15 — after this date the evidence must be re-verified before it is relied on.
**Status:** AWAITING OWNER DECISION. No provider is selected, contracted, connected or approved.

This document contains no passwords, no keys and no confidential commercial terms. It may be circulated
internally as-is.

---

## 1. What is being decided

CrossBuy's AI features work today. They run **inside our own systems**, on our own server, and no
business data leaves the building. That is the situation right now and it is fully approved.

The question in front of you is narrower than it sounds:

> **Do we want to send a limited, filtered slice of CrossBuy data to an external AI company in order to
> get better analysis than we can produce ourselves — and if so, on whose terms?**

You are **not** being asked to approve a connection. Engineering has built the controls, but the
controls are deliberately locked shut, and only a signed decision from this group can open them.

**Choosing "not now" is a legitimate outcome and costs us nothing we already have.**

---

## 2. What would actually be sent — and what would not

Engineering has already restricted this in code, before anyone asked. Even a fully approved provider
would be permitted to receive only two categories of data:

| Category | Example | Permitted to an approved external provider? |
|---|---|---|
| Operational metadata | record identifiers, dates, document types | **Yes** |
| Financial aggregates | totals, balances, account codes, movement summaries | **Yes** |
| Free-text business content | the description a user typed on a journal entry | **No — blocked** |
| Personal data | employee or customer personal details | **No — blocked** |
| Anything unclassified | any new field nobody has categorised | **No — blocked by default** |

The last row matters most: an engineer who adds a new field and forgets to classify it does **not**
accidentally leak it. Unclassified data is refused, not sent. This is enforced by automated tests that
run on every build; 173 of them cover this area specifically, and all pass.

---

## 3. The three candidates

| | Microsoft Azure OpenAI | OpenAI (direct) | Anthropic Claude |
|---|---|---|---|
| Who holds our data | Microsoft | OpenAI | Anthropic |
| Uses our data to train their AI? | No | No | No |
| How long they keep it by default | up to 30 days (abuse checks) | up to 30 days (abuse checks) | not kept by default |
| Can we get "keep nothing"? | Only if Microsoft manages our account | Only with their sales approval | Only with their sales approval |
| Can processing stay in the Gulf? | UAE — but only if we buy reserved capacity | UAE — with their approval | **No. United States only.** |
| Can we lock it to our private network? | **Yes** | No | No |
| Can we use company logins instead of a password-like key? | **Yes** | No | No |
| Formal data-protection contract available | Yes | Yes | Yes |

All three published positions are good on the point most people worry about first: **none of them
trains their AI on our data.** The differences are about *where the computing happens*, *how long they
keep a copy*, and *how tightly we can fence off the connection*.

---

## 4. Recommendation

| Rank | Provider | Recommendation |
|---|---|---|
| **1** | **Microsoft Azure OpenAI** | **RECOMMENDED** |
| **2** | **OpenAI (direct)** | **SECOND CHOICE** |
| **3** | **Anthropic Claude** | **CONDITIONAL** — only if we accept United States processing |

**Why Microsoft is first:** it is the only candidate we can place behind our own private network with
the public internet switched off, sign in to with company identities instead of a shared secret key,
and govern with automated policy. It is also the only one where the AI model's original maker never
sees our data at all — Microsoft runs the models itself.

**Three honest qualifications**, because they change the picture:

1. **"Keep nothing" is hardest to obtain from Microsoft**, not easiest. Microsoft only offers it to
   customers managed by a Microsoft account team or on an eligible programme. Whether CrossBuy
   qualifies is **not known** and is a question for Procurement.
2. **Keeping the computing in the UAE on Microsoft requires buying reserved capacity** — a fixed
   monthly commitment, not pay-as-you-go. On pay-as-you-go, a UAE-based Microsoft deployment stores our
   data in the UAE but performs the actual computing *anywhere in the world*. These two look identical
   in the management portal, which is exactly why this is written down here.
3. **The private-network and company-login advantages need real infrastructure work** — a network link
   from our premises to Microsoft's cloud. That is a cost, not a formality.

**Nobody operates AI computing in Kuwait.** Microsoft has announced an intention to build a Kuwait
region but has not built one. If our requirement is genuinely "the data must never leave Kuwait", then
the correct answer is **none of the three**, and we keep what we have.

---

## 5. The four decisions only you can make

Engineering cannot answer any of these, and the system stays locked until all four are answered.

### Decision 1 — Where may our data be processed?

☐ Kuwait only *(⇒ no external provider is possible; keep local AI)*
☐ GCC / UAE acceptable
☐ European Union acceptable
☐ United States acceptable
☐ No geographic restriction

*Note: choosing "United States acceptable" changes the ranking — Anthropic then moves ahead of OpenAI
into second place.*

### Decision 2 — How long may the provider keep a copy?

☐ Zero retention required *(⇒ we must negotiate this; it is not the default anywhere)*
☐ Up to 30 days acceptable
☐ Other: ________________

### Decision 3 — May our data ever be used to train their AI?

☐ **No, never** *(recommended — and all three currently agree)*
☐ Yes, under specified conditions: ________________

### Decision 4 — Free text and personal data

☐ Keep both permanently blocked *(recommended)*
☐ Refer free text to a separate data-owner review
☐ Refer personal data to a separate privacy review

---

## 6. A recommendation you may not have expected

There is a way to get external AI analysis of journal entries **without ever sending the text someone
typed**.

Instead of sending the description itself, CrossBuy can send a short summary *about* it — how long it
is, which language it is in, and which category it falls into from a list we control — together with
the amounts and account codes we were already willing to send. The external AI analyses that; CrossBuy
puts the real description back on screen locally when it shows you the answer.

**Engineering recommends this approach.** It keeps the free-text block completely intact — no rule is
relaxed, no exception is signed — while still allowing the analysis to happen. It costs us some
accuracy and it costs us the work of maintaining that category list. Both are worth it.

---

## 7. Two separate verdicts

These are frequently confused, so they are stated apart.

> ### Engineering readiness: **GO**
> The security boundary is built, tested and complete. 2,329 automated tests pass; 173 of them cover
> the AI boundary specifically; none fail. No further engineering work is required before a provider
> can be accepted. Engineering is ready and waiting.

> ### Production provider approval: **OWNER DECISION REQUIRED**
> No provider is approved. No provider can be approved, because the standard a provider would be
> measured against — Decisions 1 to 4 above — has not been set. The system correctly refuses to
> approve anyone against an unwritten standard.

**"Recommended" is not "approved."** Microsoft is recommended as the strongest candidate on the
evidence. It becomes approved only when Sections 5 and 8 of this document are completed and signed.

---

## 8. Decision and signatures

```
DECISION

  Proceed with an external AI provider?          YES  /  NO  /  DEFER

  If YES, selected provider:                     ________________________________

  Approved processing region(s):                 ________________________________

  Maximum provider retention:                    ________________________________
     Zero retention required?                    YES  /  NO

  Training on our data permitted?                YES  /  NO

  Free text sent externally?                     NO  /  REFER TO DATA-OWNER REVIEW

  Personal data sent externally?                 NO  /  REFER TO PRIVACY REVIEW

  Journal description approach:                  A (keep blocked)  /  B (send text)  /  C (derived
                                                 summary — recommended)

  Contract / DPA signed and on file?             YES  /  NO      reference: ______________


APPROVALS  — all three are required; any one missing means NOT APPROVED

  Security
     Name: ____________________  Signature: ____________________  Date: __________

  Data Protection / Legal
     Name: ____________________  Signature: ____________________  Date: __________

  Business Owner
     Name: ____________________  Signature: ____________________  Date: __________


EXPIRY  — mandatory. An approval with no expiry date is not an approval.

  This approval expires on:                      ____________________

  Review owner:                                  ____________________

  On expiry, external AI processing stops automatically until re-approved.
```

---

## 9. If you decide "not now"

Nothing breaks. CrossBuy's existing AI features continue to run locally, exactly as they do today. No
feature is lost, no user is affected, and this document can be picked up again unchanged whenever the
business is ready. This has been verified by an automated test that specifically proves the local AI
does not depend on any external approval.

---

## 10. Where the detail lives

The full technical evidence — every source URL, the date each was read, the exact wording each vendor
published, the scoring method, and the arguments made *against* the recommendation — is in
[`provider-decision-package.md`](provider-decision-package.md), Section 26.

Anything in this document that is marked "not known" is genuinely not known. Nothing was assumed in
anyone's favour.

---
---

# 11. OWNER POLICY RECORD — AUTHORITATIVE

**This section, not Section 8 above, is the record the decision is read from.** Section 8 is the
signature page; this is the register of what has actually been decided. Sections 1–10 are unchanged.

**Record opened:** 2026-08-15
**Record status:** **OPEN — NO DECISION RECORDED**
**Recorded by:** Engineering, on behalf of the owner group. Engineering records; it does not decide.

> **Nothing in this register has been answered.** No decision below was inferred from the provider
> research, from the recommendation, or from the instruction that produced this document. A blank
> decision is recorded as `UNKNOWN`, and `UNKNOWN` blocks approval. That is working as intended, not a
> defect in this document.

## 11.1 The eight decisions

| # | Decision | Options | **RECORDED VALUE** | Who must answer |
|---|---|---|---|---|
| 1 | External LLM processing | ALLOW UNDER APPROVED POLICY · DO NOT ALLOW | **UNKNOWN** | Business Owner |
| 2 | Acceptable processing / residency boundary | Kuwait only · UAE · GCC · EU · US · other named region · none acceptable | **UNKNOWN** | Data Protection / Legal + Business Owner |
| 3 | Training / data use on CrossBuy inputs or outputs | NO · YES | **UNKNOWN** | Data Protection / Legal + Security |
| 4 | External personal data | NEVER ALLOW · FUTURE PRIVACY-APPROVED POLICY ONLY | **UNKNOWN** | Data Protection / Legal |
| 5 | External free text | NEVER ALLOW · FUTURE DATA-OWNER APPROVAL ONLY | **UNKNOWN** | Data Owner + Data Protection |
| 6 | Maximum provider retention | ZDR required YES/NO; if NO, maximum duration + required deletion behaviour | **UNKNOWN** | Data Protection / Legal |
| 7 | DPA / contract requirement | required · not required; if required, VERIFIED / NOT VERIFIED | **UNKNOWN** | Legal + Procurement |
| 8 | Provider candidate | Azure OpenAI · OpenAI API · Anthropic Claude API · no external provider · other | **UNKNOWN — NO PROVIDER SELECTED** | Business Owner |

**Decision 8 was deliberately left blank.** Azure OpenAI ranks first on the evidence in
`provider-decision-package.md` §26.9. Ranking first is not being chosen. Pre-filling this field from
the recommendation would make the ranking self-executing, which is exactly the failure this whole
governance layer exists to prevent.

## 11.2 Decisions 4 and 5 — current behaviour is unaffected either way

Whatever is eventually recorded, the present system behaviour does not change in this increment:

| Data class | External destination | Status |
|---|---|---|
| `PersonalData` | any | **DENY** — unchanged |
| `FreeTextBusinessContent` | any external, including an approved processor | **DENY** — unchanged |
| `Unknown` | any | **DENY** — unchanged |

Decision 5 covers `JournalEntry.Description` **and** task descriptions, CRM notes, emails, comments,
documents, and any free-text field added in future. Selecting "FUTURE DATA-OWNER APPROVAL ONLY" does
not open anything; it only records that a future review is permitted to be requested.

## 11.3 Required approvals

| Approval | Status | Approver | Date |
|---|---|---|---|
| Security | **NOT RECORDED** | — | — |
| Data Protection / Legal | **NOT RECORDED** | — | — |
| Business Owner | **NOT RECORDED** | — | — |

| Date field | Value |
|---|---|
| Decision date | **NOT RECORDED** |
| Review date | **NOT RECORDED** |
| Approval expiry date | **NOT RECORDED** |

**An approval with no expiry date is not a valid approval.** All three approvals plus all three dates
are required together; two of three is not a partial approval, it is no approval.

## 11.4 Resulting provider state

No `AiProviderCandidate` has been registered. Evaluated against the code at
[AiProviderGovernance.cs:207-208](../../CrossBuy/BL/Platform/Ai/AiProviderGovernance.cs#L207-L208):

```
AiProviderState        = Unknown
Reason                 = "No provider candidate has been supplied."
DestinationClass       = UnapprovedExternal
IsApproved             = false
```

Stated precisely, because the two are easy to confuse: the **process** is at "owner decision required";
the **code state** is `Unknown`, because `OwnerDecisionRequired` is reached only once a named candidate
exists and its facts have been entered. Either way `DestinationClass` resolves to `UnapprovedExternal`
and external egress fails closed.

`AiProviderRequirement.IsComplete` is `false` — Decisions 2, 3 and 6 are the three inputs it requires,
and none is recorded. So even a fully documented candidate could not be approved today: it would be
measured against a standard that does not exist.

## 11.5 Provider fact match — NOT PERFORMED, and why

A fact match compares **one selected provider** against **a recorded policy**. Neither exists. Running
it anyway would mean inventing either the selection or the standard.

| Requirement | Result |
|---|---|
| Training | **NOT ASSESSED — no provider selected** |
| Residency | **NOT ASSESSED — no provider selected and no boundary recorded** |
| Retention | **NOT ASSESSED — no provider selected and no maximum recorded** |
| Contract | **NOT ASSESSED — no provider selected** |
| Encryption | **PASS (provider-independent)** — enforced by `AiHopOnePolicy`, `CodeProven` |
| Subprocessor position | **NOT ASSESSED — no provider selected** |
| Deletion / lifecycle | **NOT ASSESSED — no provider selected** |
| Account / project ownership | **NOT ASSESSED — no provider selected** |
| Provider-specific eligibility | **NOT ASSESSED — no provider selected** |

Encryption is the one row that can be answered without a provider, because CrossBuy enforces it from
its own side regardless of who is at the other end.

The provider-specific checks — Azure deployment type and processing boundary, OpenAI regional
eligibility, Anthropic inference geo — are **conditional on Decision 8** and were therefore not run.
The evidence needed to run each of them, once a provider is selected, is already collected in
`provider-decision-package.md` §26.4, §26.5 and §26.6.

## 11.6 Journal entry description — status

**Unchanged and not implemented.** `JournalEntry.Description` remains `FreeTextBusinessContent` and
remains **DENIED** to every external destination, approved or not.

**Option C (derived server-side features)** is recorded as the **recommended future option** and is
explicitly **not implemented in this increment**. It becomes available for implementation only after
Decision 5 is recorded and a data owner has approved the derived feature set.

## 11.7 Provider fallback policy — RECORDED

**NO AUTOMATIC FALLBACK.** If an approved provider fails, CrossBuy does not silently call another one.
A second provider requires its own separate approval under this same register, plus an explicit routing
policy. This is recorded now, while no provider exists, so it cannot later be introduced as an
operational convenience.

## 11.8 Approval expiry semantics — RECORDED

On expiry: provider state → `Suspended`; external egress → **fails closed**; local Python ML →
**unaffected**. There is no automatic extension and no grace period. Re-approval requires the same
three signatures.

## 11.9 What must be filled in for this record to close

Every row below is currently blank. Increment 5 stays NO-GO until all of them are answered.

```
OWNER DECISION 1  External LLM processing ............ ______________________________
OWNER DECISION 2  Residency boundary (explicit set) .. ______________________________
OWNER DECISION 3  Training / data use ................ ______________________________
OWNER DECISION 4  External personal data ............. ______________________________
OWNER DECISION 5  External free text ................. ______________________________
OWNER DECISION 6  Zero retention required? ........... YES / NO
                  If NO, maximum retention ........... ______________________________
                  Required deletion behaviour ........ ______________________________
OWNER DECISION 7  DPA / contract required? ........... YES / NO
                  Contract verification status ....... VERIFIED / NOT VERIFIED
OWNER DECISION 8  Provider candidate ................. ______________________________

Provider legal entity ................................ ______________________________
Account / project owner .............................. ______________________________
Commercial / enterprise tier ......................... ______________________________
Subprocessor position accepted ....................... YES / NO
Deletion / lifecycle controls accepted ............... YES / NO

Security approval .................. APPROVED / REJECTED   by ____________  date ________
Data Protection / Legal approval ... APPROVED / REJECTED   by ____________  date ________
Business Owner approval ............ APPROVED / REJECTED   by ____________  date ________

Decision date ........................................ ______________________________
Review date .......................................... ______________________________
Approval expiry date (MANDATORY) ..................... ______________________________
```

## 11.10 Outcome

**OUTCOME B — OWNER POLICY INCOMPLETE.**

**INCREMENT 5 = NO-GO.**

This is a legitimate and expected result, not a failure. The controls refused to approve a provider
against an unwritten standard, which is precisely what they were built to do.

---

## 11.11 Update — 2026-08-15: this register is now enforced by the running system

When this register was opened, it was a document. The running program did not read it, and a single
line in a settings file could have granted an external provider the authority this page reserves for
the three signatures below. That gap was found, proven by test, and closed the same day.

**What changed, in one sentence:** a settings file can now say *where* to send data, but only a signed
entry in this register — carried into the source by a reviewed change — can say *whether* sending to an
external provider is permitted at all.

**What this means for the signatories:**

- Filling in §11.9 is no longer a paperwork step that the system might bypass. It is the only route.
- An approval with no expiry date still cannot take effect; the code refuses it, not just this page.
- On expiry or revocation, external processing stops automatically. Local AI is unaffected.
- Nothing about the eight decisions changed. All eight remain **UNKNOWN**, and no provider is selected.

No owner decision was made, implied or pre-filled by that work. Full detail:
[`provider-decision-package.md`](provider-decision-package.md) §28.

---
---

# 12. THE DECISION INSTRUMENT — prepared 2026-08-15

**§11 is the register — what has been decided. §12 is the instrument — what there is to decide.**
Sections 1–11 are unchanged. Nothing below is answered.

Each decision states the question, why it must be answered, the choices, what each choice does
technically and to our security posture, engineering's position, and the current answer. **Engineering's
position is a recommendation with reasons attached. It is not a default, and it has not been applied to
any field.**

A one-page fillable version for circulation is
[`provider-owner-decision-form.md`](provider-owner-decision-form.md). That sheet is the **input**; this
document is the **record**. Answers written on the sheet must be transcribed into §11 to take effect.

---

## DECISION 1 — External LLM processing

**A. Question.** May CrossBuy send approved, filtered, business-derived data to an external LLM
provider at all?

**B. Why we need it.** Every other decision is conditional on this one. Until it is answered the
question "which provider?" cannot be meaningful, and the system correctly refuses to approve anyone.

**C. Choices.**

| | Choice |
|---|---|
| **A** | **YES** — external processing permitted, subject to every governance gate |
| **B** | **NO** — external LLM processing prohibited |
| **C** | **NOT DECIDED** |

**D. Technical consequence.**

- **YES** → Increment 5 becomes *possible*, not automatic; Decisions 2–8 must still be answered and a
  provider must still satisfy them.
- **NO** → Increment 5's external-provider integration does not run. Local Python ML continues
  unchanged. External RAG, external chatbot and external agents remain blocked.
- **NOT DECIDED** → identical to NO in behaviour, but leaves the question open.

**E. Security consequence.** NO removes an entire class of risk permanently: no business data crosses
a jurisdiction, no third-party processor exists, no contract needs policing, no approval needs
expiring. YES accepts that risk in exchange for capability, and makes the remaining decisions load-bearing.

**F. Engineering position.** **No recommendation.** This is a business-appetite question, not a
technical one. Engineering's only statement is that **NO is not a failure**: CrossBuy's approved AI
features work today and lose nothing. Section 9 of this document says the same.

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 2 — Residency boundary

**A. Question.** Which processing boundary will the business accept for CrossBuy AI data?

**B. Why we need it.** This is the field the runtime compares a provider against
(`AcceptableResidencyRegions`). Empty means nothing qualifies. It is also the decision that changes the
provider ranking — see §26.9 of the evidence package.

**C. Choices.**

| | Choice |
|---|---|
| **A** | Kuwait only |
| **B** | Kuwait + specifically named GCC countries (list them) |
| **C** | GCC |
| **D** | A named Middle East region set (list them) |
| **E** | An explicit country/region allow-list (list them) |
| **F** | Other — owner specifies |
| **G** | NOT DECIDED |

**D. Technical consequence — read this before choosing A.**

> **No candidate performs LLM inference inside Kuwait.** Microsoft announced an *intent* to build a
> Kuwait Azure region in March 2025; an intent is not a region, and no Azure OpenAI model availability
> is published for Kuwait. OpenAI and Anthropic list no Kuwait option at all.
>
> **Therefore choosing A (Kuwait only) means: NO EXTERNAL PROVIDER.** That is a coherent, defensible
> answer — it is the same outcome as Decision 1 = NO, reached by a different route. It is recorded here
> so it is chosen deliberately rather than discovered later.

**UAE is not Kuwait.** UAE is a separate jurisdiction with its own law, regulator and cross-border
transfer position. It may well be acceptable — that is a legal determination for Data Protection, and
this document does not make it. Selecting "GCC" or "Middle East" implicitly includes the UAE; if that
is not intended, use choice E and name the countries.

**Storage residency is not inference residency.** A provider can hold data at rest in one country while
performing the computation in another. If the requirement is about where the *computation* happens, say
so explicitly — the two are configured separately on every candidate.

**E. Security consequence.** A tighter boundary reduces jurisdictional exposure and narrows the
candidate set, in some cases to zero. A wider boundary increases candidate choice and cost efficiency
and increases the number of legal regimes our data touches.

**F. Engineering position.** No recommendation — this is a legal and jurisdictional judgement.
Engineering's contribution is the evidence in §26.3–§26.5 of the package and one warning: **a UAE-North
Azure resource on a Global deployment stores in the UAE and computes worldwide, and looks identical in
the portal.** If a Gulf inference boundary is chosen, the deployment type must be named, not assumed.

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 3 — Training / data use

**A. Question.** May the provider use CrossBuy business inputs or outputs to train or improve its
models?

**B. Why we need it.** `TrainingOnBusinessDataAllowed` is `null` until answered, and null blocks
approval. The code will not infer this answer, deliberately.

**C. Choices.**

| | Choice |
|---|---|
| **A** | **NO** — provider must not train or improve models on CrossBuy data |
| **B** | **YES**, under conditions the owner defines explicitly |
| **C** | NOT DECIDED |

**D. A distinction that matters.** Two different things are often confused:

| | What it is | Status |
|---|---|---|
| **Model training** | The provider keeps our data and uses it to improve its models for everyone | This decision governs it |
| **Processing to answer the request** | The provider reads the prompt to produce a response, in memory, for the seconds it runs | Unavoidable — it *is* the service. Not what this decision is about |

Choosing NO does not mean "the provider may not read our data". It means the provider may not *learn*
from it.

**E. Security consequence.** YES means CrossBuy financial patterns could influence a model other
customers use. NO confines our data to answering our own requests.

**F. Engineering position.** **A (NO).** All three candidates already publish a no-training-by-default
position for commercial/API use, so choosing A costs nothing in candidate availability while making the
requirement enforceable rather than assumed. **Recorded as a recommendation; the field is blank.**

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 4 — External personal data

**A. Question.** May data classified `PersonalData` ever leave the estate?

**B. Why we need it.** To record the policy explicitly rather than relying on the current code default.

**C. Choices.**

| | Choice |
|---|---|
| **A** | **NEVER** — personal data is never sent externally |
| **B** | Only under a separately approved privacy policy, reviewed on its own |
| **C** | NOT DECIDED |

**D. Technical consequence — important.**

> **Current runtime behaviour: `PersonalData` → external → DENY. This decision does not change it.**
>
> The classification matrix is in code, not in the governance record. Choosing B does **not** open
> anything; it records only that a future review may be *requested*. Any actual change would require a
> separate engineering and security review, its own increment, and its own tests.

**E. Security consequence.** A remains the strongest posture and is the current behaviour. B creates a
route to a future exception — a route that does not exist today.

**F. Engineering position.** **A (NEVER).** No current or planned AI purpose needs personal data;
anomaly detection, cashflow forecasting and inventory analysis work on identifiers and amounts.

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 5 — External free text

**A. Question.** May `FreeTextBusinessContent` ever leave the estate?

**B. Why we need it.** Free text is the category most likely to contain something nobody classified — a
customer name, a person, a case reference — inside a field that looks structural.

**C. Choices.**

| | Choice |
|---|---|
| **A** | **NEVER** — free text is never sent externally |
| **B** | Selected free-text classes may be allowed after an explicit, per-class classification review |
| **C** | NOT DECIDED |

**D. Scope.** This decision covers `JournalEntry.Description`, task descriptions, CRM notes, email
bodies, comments, documents, **and any free-text field added in future**. It is a category decision,
not a field decision.

> **Current runtime behaviour: `FreeTextBusinessContent` → external → DENY, even for an approved
> processor.** Approval is for a *purpose*; it is not a licence to receive unbounded prose.

**E. Security consequence.** A keeps the guarantee structural. B makes it procedural — every future
free-text field becomes a judgement call, and judgement calls drift.

**F. Engineering position.** **A (NEVER), together with OPTION C for journal descriptions.**

> **OPTION C — derived features.** Rather than sending the description, CrossBuy computes and sends a
> small typed summary *about* it — length, language, and a category matched against a controlled
> vocabulary we own — alongside the amounts and account codes we were already willing to send. The model
> reasons over those; CrossBuy re-joins the real description locally when it displays the result.
>
> This preserves the anomaly-detection use case **without weakening a single rule**: no
> reclassification, no waiver, and the outbound builder simply has no free-text field to emit. Its cost
> is a controlled vocabulary to author and maintain, and less expressive input.
>
> **Option C is NOT implemented.** It is a future engineering option, available only after this decision
> is recorded and a data owner approves the derived feature set.

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 6 — Retention

Three separate answers are required. A single "reasonable retention" is not answerable by the code.

### 6A — Is Zero Data Retention mandatory?

| | Choice |
|---|---|
| **A** | YES — the provider must retain nothing |
| **B** | NO — bounded retention is acceptable |
| **C** | NOT DECIDED |

### 6B — If not mandatory, the maximum acceptable provider-side retention

| | Choice |
|---|---|
| **A** | 0 days |
| **B** | 7 days |
| **C** | 30 days |
| **D** | Custom: ____________ |
| **E** | NOT DECIDED |

### 6C — Required deletion / lifecycle behaviour

The owner must either specify the required behaviour, or explicitly approve the provider's documented
behaviour as sufficient. **Silence is not approval.**

**Why this matters.** Every candidate's *default* is up to 30 days of abuse-monitoring retention, and
in every case zero-retention must be applied for and granted — it is never the shipped default. So
choosing 6A = YES is a commitment to a commercial negotiation, not a configuration change.

**Unknown retention evidence fails closed.** A provider whose retention is not established is not
"probably fine" — `ProviderRetention` is null, which blocks approval. Provider default retention is
never automatically accepted.

**F. Engineering position.** No recommendation on the duration — that is a data-protection judgement.
Engineering notes only that 6A = YES narrows the candidate set to whoever will actually grant it, and
that whether CrossBuy qualifies is **UNKNOWN for all three candidates** (§12.4 below).

**G. CURRENT OWNER ANSWERS: 6A `NOT RECORDED` · 6B `NOT RECORDED` · 6C `NOT RECORDED`**

---

## DECISION 7 — Contract / DPA

**A. Question.** Is an executed data-processing agreement required before any external processing?

**C. Choices.**

| | Choice |
|---|---|
| **A** | An executed / verified DPA or equivalent contract is required |
| **B** | A different contractual standard — owner specifies |
| **C** | No additional contract required |
| **D** | NOT DECIDED |

**D. A distinction the code enforces.**

| Statement | Meaning | Domain value |
|---|---|---|
| "The provider offers a DPA" | A product exists | **Not expressible** — and not sufficient |
| "CrossBuy has executed and verified the DPA" | We have one, for us | `ContractInPlace = Yes` |
| "We have not" | — | `ContractInPlace = No` → denies |
| "Nobody checked" | — | `ContractInPlace = Unknown` → denies |

All three candidates offer a DPA. **None of them has one with CrossBuy.** Availability is not execution,
and the model has no way to record availability — only the executed status.

**E. Security consequence.** Without a contract, every published provider commitment is a web page that
can change without notice. The contract is what makes it enforceable.

**F. Engineering position.** **A.** `ContractRequired` already defaults to `true` in code, which is the
strict reading; choosing A confirms it deliberately rather than by inheritance. **Not recorded as the
answer.**

**G. CURRENT OWNER ANSWER: `NOT RECORDED`**

---

## DECISION 8 — Provider candidate

**A. Question.** Which provider, if any?

**C. Choices.**

| | Choice |
|---|---|
| **1** | Microsoft Azure OpenAI / Microsoft Foundry |
| **2** | OpenAI API |
| **3** | Anthropic Claude API |
| **4** | **NO EXTERNAL PROVIDER** |
| **5** | DEFER DECISION |

> ## RANKING ≠ SELECTION · RECOMMENDED ≠ APPROVED
>
> The evidence package ranks Azure first, OpenAI second and Anthropic conditional. **That ranking is
> evidence for a decision. It is not the decision.** No provider has been selected, and this field was
> deliberately left blank so that ranking first cannot become choosing first.

**F. Engineering position.** Azure is the strongest candidate **on the documented evidence**, with three
qualifications that materially weaken it (§26.7 of the package): its zero-retention route is the
narrowest of the three, UAE in-region chat inference requires reserved capacity, and its identity
advantage degrades because CrossBuy runs on-premises. **The ranking also inverts between #2 and #3 on
Decision 2 alone** — so Decision 2 should be answered before Decision 8, not after.

**G. CURRENT OWNER ANSWER: `NOT RECORDED — NO PROVIDER SELECTED`**

---

## 12.1 Provider comparison — management view

`UNKNOWN` below means no first-party source established it. It has not been converted to a pass.

| | **Azure OpenAI** | **OpenAI API** | **Anthropic Claude** |
|---|---|---|---|
| Who processes our data | Microsoft | OpenAI | Anthropic |
| Storage residency | Customer-designated Azure geography, all deployment types | Supported residency regions incl. UAE | **US only**, fixed at workspace creation |
| **Inference residency** | Global = anywhere · Data Zone = US/EU/APAC only · Regional = deployment region | US, Europe **and UAE** support regional processing | **`us` or `global` only — no Gulf option** |
| Gulf inference achievable? | **Yes — but only on reserved capacity (PTU)**; pay-per-token in UAE North carries embeddings + whisper only, no chat model | **Yes — pay-per-token, subject to approval** | **No** |
| Default retention | Up to 30 days, abuse monitoring, human review possible | Up to 30 days abuse logs | **Content not retained by default** — strongest |
| Zero retention attainable? | Only for customers managed by a Microsoft account team or an eligible programme | Sales approval + added requirements | Enabled per organisation on request |
| Trains on our data? | No | No | No |
| Model vendor sees our data? | **No — Microsoft runs the models** | OpenAI is the processor | Anthropic is the processor |
| Network controls | **Private Link, deny-public-network, VNet + IP rules** | Public internet only | Public internet only |
| Authentication | **Entra ID + RBAC**; managed identity only if hosted in Azure | API key | API key + workspace geo policy |
| Contract / DPA | Microsoft Products & Services DPA | DPA available | DPA available |
| Attestations | Azure compliance catalogue | ISO 27001/27017/27018/27701, SOC 2 Type 2 | SOC 2 I & II, ISO 27001, **ISO 42001** |
| Operational complexity | **Highest** — VNet/VPN or ExpressRoute from Kuwait premises | Low | Low |
| Commercial unknowns | Managed-customer status; PTU cost | UAE region approval; ZDR approval | ZDR approval |
| Flagged-content caveat | Human review unless modified monitoring granted | Not established — **UNKNOWN** | **Up to 2 years, regardless of ZDR** |
| CrossBuy compatibility | Good; on-prem reduces the identity benefit | Good; simplest integration | Good technically; fails a Gulf residency requirement |
| **Blocking unknowns** | Eligibility for modified abuse monitoring | UAE access + ZDR eligibility | ZDR eligibility |

### 12.2 Candidate notes

**Azure OpenAI.** Strongest on architecture and contract, not on residency. The distinction that
decides it: **a UAE North *resource* does not mean UAE *inference*.** At-rest data stays in the
designated geography for every deployment type, but processing follows the deployment type — Global
processes in any Azure region, Data Zone is US/EU/APAC only (there is no Middle East data zone), and
only Standard/Regional or Regional Provisioned process in the deployment's own region. In UAE North the
pay-per-token Standard tier carries **only** embeddings and whisper; the chat models appear **only**
under Regional Provisioned Managed, which is reserved capacity with a minimum commitment. **Whether
CrossBuy qualifies for modified abuse monitoring is UNKNOWN and is a Procurement question.**

**OpenAI API.** The only candidate offering **UAE regional processing on pay-per-token**, which is the
most surprising finding in the evaluation and the one that disproves "Azure is the only way to keep Gulf
inference". It is subject to additional approval, and **whether that approval would be granted to the
CrossBuy account is UNKNOWN.** ZDR likewise requires prior sales approval and does not cover every
endpoint. Integration is the simplest of the three; the trade is public-internet transport and an API
key rather than private networking and federated identity.

**Anthropic Claude.** The **best documented default retention** of the three — conversation content is
not retained by default, even without a ZDR agreement — and the only candidate holding **ISO 42001**, the
AI-management-system standard. It is ranked third **solely on residency**: `inference_geo` accepts only
`us` or `global`, workspace geo is `us` and cannot be changed after creation. If the approved region set
includes the United States, Anthropic moves **ahead of OpenAI into second place**. One caveat carried
forward honestly: content flagged by trust-and-safety systems may be retained **up to 2 years regardless
of any ZDR arrangement**. Anthropic is not rejected for any reason other than residency.

### 12.3 The no-provider option

**Choosing "NO EXTERNAL PROVIDER" is a first-class architecture decision, not a failure.**

What continues, unchanged and already approved: **journal anomaly detection · cashflow forecasting ·
inventory analysis**, running on local Python ML over loopback, requiring no provider, no credential, no
contract and no approval. This independence is proven by test
(`PD17_local_ml_remains_usable_with_no_provider_approved`), not asserted.

What does not proceed: external model integration, external RAG, external agents. No existing feature is
lost, no user is affected, and the decision can be revisited at any time without rework — the governance
layer that would carry it is already built and verified.

### 12.4 Unresolved commercial facts — all `UNKNOWN`

These cannot be answered by engineering or by reading documentation. Each requires a conversation with
the provider.

| # | Question | Owner | Status |
|---|---|---|---|
| 1 | Does CrossBuy qualify for Azure **modified abuse monitoring** (managed-customer or eligible-programme status)? | Procurement | **UNKNOWN** |
| 2 | Would OpenAI grant CrossBuy **Zero Data Retention**? | Procurement + Legal | **UNKNOWN** |
| 3 | Would OpenAI grant CrossBuy **UAE regional processing** access? | Procurement | **UNKNOWN** |
| 4 | Would Anthropic grant CrossBuy **Zero Data Retention**? | Procurement + Legal | **UNKNOWN** |
| 5 | Cost of Azure **Regional Provisioned (PTU)** capacity in UAE North, if Gulf inference is required | Procurement | **UNKNOWN — no quotation obtained** |
| 6 | Cost and lead time of a **network link** (VPN/ExpressRoute) from the Kuwait premises to Azure | Infrastructure | **UNKNOWN** |
| 7 | Does **Kuwaiti law** permit cross-border transfer of this data to the chosen jurisdiction? | Data Protection / Legal | **UNKNOWN — not an engineering determination** |
| 8 | Which **CrossBuy legal entity** would contract with the provider? | Legal | **UNKNOWN** |
| 9 | Does the provider's **flagged-content retention** (Anthropic: up to 2 years) fall within our policy? | Data Protection / Legal | **UNKNOWN** |

**None of these has been answered, and none may be assumed favourably.**

### 12.5 Required provider metadata

Before any provider can reach `ApprovedExternalProcessor`, all of the following must exist:

| # | Item | Domain field |
|---|---|---|
| 1 | Provider candidate | `ProviderId` / `ProviderName` |
| 2 | Provider legal entity | `LegalEntity` |
| 3 | Account / project owner | `AccountOwner` |
| 4 | Commercial tier | `CommercialTier` |
| 5 | Residency facts | `ResidencyRegion` |
| 6 | Retention facts | `ProviderRetention` |
| 7 | Training / data-use facts | `TrainsOnCustomerData` |
| 8 | Contract / DPA status | `ContractInPlace` |
| 9 | Subprocessor position | `SubprocessorPositionAccepted` |
| 10 | Deletion / lifecycle controls | `DeletionControlsAvailable` |
| 11 | Encryption in transit | `EncryptionInTransit` |
| 12 | Security approval | `SecurityApproved` |
| 13 | Data Protection / Legal approval | `DataProtectionApproved` |
| 14 | Business Owner approval | `BusinessOwnerApproved` |
| 15 | Decision date | `ApprovedAtUtc` |
| 16 | Review date | *(see §12.7 — model gap)* |
| 17 | **Approval expiry date — MANDATORY** | `ExpiresAtUtc` |

**An approval with no expiry date is not an approval.** The evaluator refuses it explicitly, and a test
pins that refusal.

### 12.6 Signature gates

```
SECURITY APPROVAL
  Approver:    NOT RECORDED          Role:      NOT RECORDED
  Decision:    NOT RECORDED          Date:      NOT RECORDED
  Conditions:  NOT RECORDED

DATA PROTECTION / LEGAL APPROVAL
  Approver:    NOT RECORDED          Role:      NOT RECORDED
  Decision:    NOT RECORDED          Date:      NOT RECORDED
  Conditions:  NOT RECORDED

BUSINESS OWNER APPROVAL
  Approver:    NOT RECORDED          Role:      NOT RECORDED
  Decision:    NOT RECORDED          Date:      NOT RECORDED
  Conditions:  NOT RECORDED
```

```
        DECISION DATE:            NOT RECORDED
        REVIEW DATE:              NOT RECORDED

  ╔══════════════════════════════════════════════════════════════════╗
  ║   APPROVAL EXPIRY DATE:      NOT RECORDED                        ║
  ║                                                                  ║
  ║   NO EXPIRY  =  NO VALID PROVIDER APPROVAL                       ║
  ║   On expiry: provider → Suspended · external egress fails closed ║
  ║              local Python ML unaffected · no auto-extension      ║
  ╚══════════════════════════════════════════════════════════════════╝
```

### 12.7 Owner field → domain model — can the runtime represent the decision?

| Owner field | Domain field | Representable? | Notes |
|---|---|---|---|
| D1 External LLM allowed | *(none)* | **PARTIAL — GAP** | Expressed only by leaving the record empty, or per-candidate via `ExplicitlyRejected`. There is no program-level "external processing is prohibited" flag |
| D2 Residency boundary | `AcceptableResidencyRegions` | **YES** | Multi-value list; supports an explicit allow-list |
| D3 Training / data use | `TrainingOnBusinessDataAllowed` | **YES** | Nullable; null blocks |
| D4 Personal data policy | *(none)* | **NO — GAP** | The rule is in `AiEgressPolicy.Permitted`, not the governance record. The answer is recorded here in prose only |
| D5 Free text policy | *(none)* | **NO — GAP** | Same as D4 |
| D6A Zero retention | `ZeroRetentionRequired` | **YES** | |
| D6B Maximum retention | `MaximumProviderRetention` | **YES** | |
| D6C Deletion / lifecycle behaviour | `DeletionControlsAvailable` | **PARTIAL — GAP** | A three-valued fact ("controls exist"), not a specification of *required* behaviour |
| D7 Contract requirement | `ContractRequired` + `ContractInPlace` | **YES** | Cannot express "available but not executed" — by design |
| D8 Provider candidate | `ProviderId` / `ProviderName` | **YES** | |
| Provider metadata 2–11 | as listed in §12.5 | **YES** | |
| Three approvals | `SecurityApproved` / `DataProtectionApproved` / `BusinessOwnerApproved` | **YES** | |
| Approver names, per approval | `ApprovedBy` (one string) | **PARTIAL — GAP** | One field for three approvers; individual names are recorded here, not in the model |
| Per-approval conditions | *(none)* | **NO — GAP** | Recorded in §12.6 prose only |
| Decision date | `ApprovedAtUtc` | **YES** | |
| **Review date** | *(none)* | **NO — GAP** | Only expiry is modelled |
| Expiry | `ExpiresAtUtc` | **YES** | Null = not approved |
| Revocation | `Revoked` + `RevokedReason` | **YES** | |
| Provider state | `AiProviderState` | **YES** | Six states |

**Six model gaps identified. None is fixed here** — §22 of the brief forbids expanding the model in this
task, and none of them blocks recording a decision: every gapped item is capturable in this document.
They are logged as engineering follow-ups for whichever increment implements the approved record.

**None of the gaps is a security weakness.** Every gap is a field the model cannot *store*; not one of
them is a check the runtime fails to *make*. The four hard-coded rules (personal data, free text,
unknown classification, and the classification matrix generally) are stricter in code than any record
could make them.

### 12.8 What provider approval does NOT do

Verified in Increment 4.3 and pinned by tests. Approval opens **one** gate.

| Approval does **not** mean | Enforced by | Test |
|---|---|---|
| `PersonalData` becomes allowed | Classification matrix | `RT16_RT19`, `PD15`, `B7` |
| `FreeTextBusinessContent` becomes allowed | Classification matrix | `RT16_RT19`, `PD12_PD16`, `B6` |
| `Unknown` classification becomes allowed | Unknown-first fail-closed | `RT16_RT19` |
| Company isolation can be bypassed | Context vs `DataCompanyId` comparison | `RT18`, `PD13`, `B8` |
| Permission / trusted context can be bypassed | `IBusinessContextAccessor` resolution | `RT19b`, `PD14` |
| Hop-1 can be bypassed | `AiHopOnePolicy.Validate` | `B10` |
| The payload ceiling can be lifted | `AiEgressLimits.MaxPayloadBytes` | `RT19c` |
| A credential is no longer needed | `IsUsableSecret` | `B9` |

### 12.9 Owner decision cannot be replaced by configuration

`AiService:DestinationClass` is **TECHNICAL-HINT-ONLY**. It names the intended destination; it confers
no authority. No owner field is, or may become, a configuration boolean such as
`"ProviderApproved": true`. The authoritative decision lives in this register and in the source-controlled
governance record — never in deployment configuration. Verified by `B1`, `B15`, the RT matrix, and the
repository-wide configuration scan in §29.5 of the package.

### 12.10 Owner decision effect matrix

| Register state | Result |
|---|---|
| D1 = NO | **No Increment 5 external provider.** Local ML continues. A complete outcome |
| D1 = YES, D2 unresolved | **NO-GO** — `IsComplete` false; no standard to measure against |
| D2 = Kuwait only | **No candidate qualifies** → effectively no external provider |
| Provider selected, any mandatory fact `Unknown` | **NO-GO** — state `UnderAssessment` |
| Provider selected, Security missing | **NO-GO** — `OwnerDecisionRequired` |
| Provider selected, Data Protection missing | **NO-GO** — `OwnerDecisionRequired` |
| Provider selected, Business Owner missing | **NO-GO** — `OwnerDecisionRequired` |
| All three signed, expiry missing | **NO-GO** — `OwnerDecisionRequired` |
| All three signed, expiry passed | **NO-GO** — `Suspended` |
| All three signed, approval revoked | **NO-GO** — `Suspended` |
| Provider facts contradict the requirement | **NO-GO** — `Rejected` |
| Everything satisfied and current | **Eligible for runtime approval** — `ApprovedExternalProcessor` |

**"Eligible" is not "GO".** Increment 5 becomes GO only when the *actual* register satisfies every gate
and the runtime evaluator returns approved from the real governance record.

### 12.11 GO / NO-GO checklist

```
[ ] External LLM processing decision recorded
[ ] Residency boundary recorded
[ ] Training / data-use requirement recorded
[ ] PersonalData policy recorded
[ ] FreeText policy recorded
[ ] Retention / ZDR requirement recorded (6A + 6B + 6C)
[ ] Contract / DPA requirement recorded
[ ] Provider candidate selected
[ ] Provider legal entity verified
[ ] Commercial tier verified
[ ] Residency capability verified
[ ] Retention capability verified
[ ] Training / data-use capability verified
[ ] Contract / DPA verified (executed, not merely available)
[ ] Subprocessor position accepted
[ ] Deletion / lifecycle controls accepted
[ ] Security approval signed
[ ] Data Protection / Legal approval signed
[ ] Business Owner approval signed
[ ] Decision date recorded
[ ] Review date recorded
[ ] Approval expiry date recorded
[ ] Runtime AiProviderEvaluator returns ApprovedExternalProcessor
[ ] Runtime IAiProviderAuthority returns IsApproved = true
```

**24 items. 0 satisfied. INCREMENT 5 = NO-GO.**

### 12.12 A note on the future governance admin surface

Populating the approved record currently requires a **reviewed source change**, not a screen. That is
deliberate for now — it makes approval harder to obtain than deployment, which was the entire point of
Increment 4.3. If the business later wants approvals maintained without a code change, that is a
**future governance-admin requirement** with its own security design (who may edit, four-eyes, audit
trail, tamper evidence). **It is recorded here and deliberately not built.**

---
---

# 13. OWNER POLICY — RECORDED

**Recorded into this register:** 2026-08-15 (the date the decisions were *transcribed*; this is **not**
the formal Decision Date, which remains NOT RECORDED — see §13.4).
**Source:** decisions supplied by the Business Owner and recorded verbatim.
**Sections 1–12 are unchanged.** §11.1's table is superseded by §13.1 below and left in place as the
record of the state before these answers existed.

> **POLICY DECISIONS ARE NOW COMPLETE. FORMAL APPROVAL IS NOT.**
> These are two different things and are tracked separately throughout this section. Recording what the
> business requires is not the same as three named people signing that a specific provider meets it.

## 13.1 The eight decisions — RECORDED

| # | Decision | **RECORDED VALUE** |
|---|---|---|
| **1** | External LLM processing | **YES** — permitted under approved governance, security, legal, classification, tenancy, permission, retention and provider controls. **This does not authorise unrestricted data egress.** |
| **2** | Residency boundary | **GLOBAL — no product-level geographic restriction.** CrossBuy is designed for international operation |
| **3** | Training / data use | **NO** — providers must not use CrossBuy or customer business inputs/outputs to train or improve foundation models. Temporary processing to answer a request is allowed only under approved service terms. **Evidence must be verified before approval** |
| **4** | External personal data | **NO** — `PersonalData` → external → **DENY**. Provider approval must not override this. Any future change needs a separate privacy/security policy and explicit future approval |
| **5** | External free text | **CONDITIONAL ALLOW, default DENY.** Selected categories may be allowed in future only after explicit classification, data-owner review, security review, purpose-specific approval and a minimised outbound DTO |
| **6A** | Zero data retention | **YES — ZDR REQUIRED** for normal business request/response content |
| **6B** | Maximum retention | **0 DAYS** for normal business request/response content |
| **6C** | Deletion / lifecycle | **No persistent provider-side storage** of normal business request/response content after processing. Exceptional retention for legal obligation, abuse monitoring, security or regulation must be **documented, separately identified, reviewed by Security and Legal/Data Protection, and accepted before production approval** |
| **7** | DPA / contract | **YES — REQUIRED**, and must be **verified and accepted**, not merely available |
| **8** | Provider candidate | **OPENAI API — SELECTED** |

### 13.2 What Decision 2 does and does not mean

Recorded as the owner stated it: **GLOBAL is a product-level policy.** It removes geography as a
*product* constraint. It does **not** override:

- customer-specific legal requirements;
- tenant-specific residency requirements;
- deployment-specific restrictions;
- local privacy law;
- contract requirements.

**A future tenant or deployment may impose stricter residency, and the architecture must remain capable
of enforcing it.** It does: `AcceptableResidencyRegions` is a per-requirement list, and the evaluator
rejects a provider whose `ResidencyRegion` is outside it. Recording GLOBAL today does not remove that
capability — it sets this deployment's value. Residency is **not** deleted from governance; it becomes
informational at product level and enforceable at tenant level.

### 13.3 What Decision 8 does and does not mean

> ## SELECTED ≠ APPROVED
>
> OpenAI API is the **selected candidate**. It becomes `ApprovedExternalProcessor` only when every
> mandatory fact, every approval and the expiry condition passes. Selection changes the *subject* of the
> assessment; it changes nothing about the *outcome*. Runtime state is still `Unknown` and external
> egress still fails closed.

### 13.4 Approvals and dates — UNCHANGED

| | Status |
|---|---|
| Security approval | **NOT RECORDED** — approver, role, decision, date, conditions all blank |
| Data Protection / Legal approval | **NOT RECORDED** — same |
| Business Owner **formal approval** | **NOT RECORDED** — see below |
| Decision date | **NOT RECORDED** |
| Review date | **NOT RECORDED** |
| Approval expiry date | **NOT RECORDED** |

**On the Business Owner row specifically.** The Business Owner supplied the eight *policy decisions*,
and those are recorded above as given. This register's process (§11.3, §12.6) additionally requires a
formal approval carrying an **approver name, a decision, a date and conditions** — and that metadata was
not supplied. So:

- **Policy decisions: RECORDED.**
- **Business Owner formal approval: NOT RECORDED.**

These were deliberately not merged. A policy answer says *what the business requires of any provider*; a
formal approval says *this named person approves this named provider on this date, expiring on that
one*. Treating the first as the second would manufacture a signature, which §0.3 forbids and which the
expiry rule exists to prevent.

## 13.5 Recorded policy → domain model

| Decision | Domain field / runtime rule | Representable? |
|---|---|---|
| D1 external processing = YES | *(none)* | **DOCUMENT-ONLY** — no program-level flag exists; expressed by the record being populated at all |
| D2 residency = GLOBAL | `AcceptableResidencyRegions = ["Global"]` | **FULL** |
| D3 training = NO | `TrainingOnBusinessDataAllowed = false` | **FULL** |
| D4 personal data = DENY | `AiEgressPolicy.Permitted` (hard-coded) | **DOCUMENT-ONLY in the governance model — but ENFORCED in code, and more strictly than a record could** |
| D5 free text = conditional, default DENY | `AiEgressPolicy.Permitted` (hard-coded DENY) | **PARTIAL** — the DENY is enforced; the "conditional future allow" pathway is document-only |
| D6A ZDR required = YES | `ZeroRetentionRequired = true` | **FULL** |
| D6B max retention = 0 days | `MaximumProviderRetention` (redundant while 6A is true) | **FULL** |
| D6C deletion / exceptional-retention review | `DeletionControlsAvailable` (Yes/No/Unknown) | **PARTIAL** — records *that* controls exist, not *which behaviour is required* nor *that Security and Legal accepted the exceptions* |
| D7 DPA required + verified | `ContractRequired = true` + `ContractInPlace` | **FULL** |
| D8 provider = OpenAI API | `ProviderId` / `ProviderName` | **FULL** |
| Approver identities and conditions | `ApprovedBy` (one string, three approvers) | **PARTIAL** |
| Review date | *(none)* | **NOT REPRESENTABLE** |

**Six of the seven gaps from §12.7 are re-confirmed unchanged.** `AiProviderRequirement.IsComplete` is
now **true** — residency, retention and training are all recorded — so the eleventh mandatory fact,
`OwnerRequirementPolicy`, is satisfied for the first time.

## 13.6 ⚠ OWNER POLICY ENFORCEMENT GAP — found while recording D6A

This was found by mapping the owner's hard gate onto the code, and it is reported rather than fixed.

**The owner's hard gate:** ZDR is required, and CrossBuy-account ZDR eligibility must be *verified*.

**What the model does** ([AiProviderGovernance.cs:271](../../CrossBuy/BL/Platform/Ai/AiProviderGovernance.cs#L271)):

```csharp
Need(c.ProviderRetention.HasValue || r.ZeroRetentionRequired, "ProviderRetention");
```

Because `ZeroRetentionRequired` is now `true`, **`ProviderRetention` is no longer a required fact at
all.** And `PolicyBreach` only fires when retention is *known and greater than zero*:

```csharp
if (r.ZeroRetentionRequired && c.ProviderRetention is { } zr && zr > TimeSpan.Zero) …
```

**Consequence:** a candidate whose retention is **UNKNOWN** passes both checks. The model can represent
"the provider retains 30 days" (→ `Rejected`) and "the provider retains nothing" (→ fine), but it
**cannot represent "we require ZDR and have not been granted it"** — which is precisely CrossBuy's
position with OpenAI today.

**Severity: MEDIUM, not HIGH.** Nothing is exposed. Six other mandatory facts are missing, so the
provider cannot approach approval regardless, and every classification, tenancy, permission, credential
and hop-1 gate is untouched. The gap is that this specific hard gate would not be the thing that stopped
it — and the owner named it as the hard gate.

**Not fixed here.** §27 of the brief instructs me to stop and report rather than expand scope. The fix
belongs in the increment that populates the record: either make `ProviderRetention` unconditionally
mandatory, or add an explicit `ZeroRetentionGranted` three-valued fact so "requested but not granted" is
representable. **This is a blocker for production approval, not for recording the policy.**

## 13.7 Enforcement unchanged by these decisions

| Rule | Status | Proven by |
|---|---|---|
| `PersonalData` → external → DENY | **UNCHANGED** | `RT16_RT19`, `PD15`, `B7` |
| `FreeTextBusinessContent` → external → DENY | **UNCHANGED** | `RT16_RT19`, `PD12_PD16`, `B6` |
| `Unknown` classification → DENY | **UNCHANGED** | `RT16_RT19` |
| `JournalEntry.Description` = `FreeTextBusinessContent` → DENY | **UNCHANGED** | classification unchanged |
| Company isolation | **UNCHANGED** | `RT18`, `PD13`, `B8` |
| Permission / trusted context | **UNCHANGED** | `RT19b`, `PD14` |
| Hop-1 | **UNCHANGED** | `B10` |
| Configuration cannot approve | **UNCHANGED** | `B1`, `B15`, RT matrix |
| Local Python ML independent of any provider | **UNCHANGED** | `PD17`, `RT20` |

**No product code was changed by recording these decisions.** `JournalEntry.Description` remains
`FreeTextBusinessContent` and externally denied; **Option C remains a future option and is not
implemented.**

### The future free-text path (documented, not built)

```
   Free-text class
        │
        ▼
   Explicit data-owner approval
        │
        ▼
   Purpose-specific minimised DTO
        │
        ▼
   Security review
        │
        ▼
   Possible policy extension  ← requires its own increment, tests and approval
```

---
---

# 14. EVIDENCE INTAKE SPECIFICATION — the eleven outstanding verifications

**Opened 2026-08-15.** Sections 1–13 unchanged.

§13 recorded *what the business requires*. §12.5 listed *which fields are blank*. This section specifies
**what counts as proof** for each one — because "Commercial tier: Enterprise" written into a blank is not
evidence, and the difference is the whole point of the evidence-level model.

**Engineering cannot verify any of these.** Every item requires a fact held by OpenAI, by Legal, by
Procurement or by a named approver. Nothing here can be researched, inferred or read out of
documentation, and none of it may be filled with a plausible value.

## 14.1 The count is eleven, not ten

The outstanding list circulated as ten items. The evaluator reports **six** missing candidate facts, and
one of them — **`LegalEntity`** — was not on that list. It is mandatory: `Need(!string.IsNullOrWhiteSpace(c.LegalEntity), "LegalEntity")`.
Added below as item 0 so the intake matches what the code actually requires.

## 14.2 Required minimum evidence level per item

`AiEvidenceLevel` is already modelled: `Unknown` → `CodeProven` → `ConfigProven` → `OwnerAttested` →
`ContractProven`. Each item below states the **minimum** level that may be accepted. Anything weaker
leaves the fact `Unknown`.

| # | Item | What counts as proof | Minimum level | Owner | Domain result |
|---|---|---|---|---|---|
| **0** | **Provider legal entity** *(added — was missing from the circulated list)* | The counterparty name as it appears on the executed agreement | `ContractProven` | Legal | `LegalEntity = "<entity>"` |
| **1** | **ZDR for our account** | Written confirmation from OpenAI that Zero Data Retention is **enabled** for a **named organisation and production project**. A support ticket, an account-manager email or a console screenshot showing the setting. **Not** a link to the ZDR documentation | `OwnerAttested` (`ContractProven` preferred) | Procurement + Legal | `ProviderRetention = TimeSpan.Zero` — **see §14.4** |
| **2** | **DPA / contract** | The **executed** DPA, countersigned, with a reference we can cite. Availability of a DPA is not proof | `ContractProven` | Legal | `ContractInPlace = Yes` |
| **3** | **Account / project owner** | OpenAI **organisation id** + **production project id** + the named business owner and infrastructure owner + evidence that dev and production are separate projects. **No API keys** | `OwnerAttested` | Infrastructure | `AccountOwner = "<named owner>"` |
| **4** | **Commercial tier** | The tier as stated on the contract or in the account console, not as assumed | `ContractProven` or `ConfigProven` | Procurement | `CommercialTier = "<tier>"` |
| **5** | **Subprocessors** | A dated statement from Data Protection/Legal that the **current published list has been reviewed and accepted**, naming the version or retrieval date | `OwnerAttested` | Data Protection / Legal | `SubprocessorPositionAccepted = Yes` |
| **6** | **Deletion / lifecycle** | A dated statement from **Security and Legal jointly** accepting (a) the per-endpoint deletion behaviour and (b) the exceptional-retention mechanisms that survive ZDR — Safety Retention and legal obligation. D6C requires both | `OwnerAttested` | Security + Data Protection / Legal | `DeletionControlsAvailable = Yes` |
| **7** | **Security approval** | Named approver, role, APPROVED/REJECTED, date, conditions | `OwnerAttested` | Security | `SecurityApproved = true` |
| **8** | **Legal / Data Protection approval** | Same five fields. Must specifically address ZDR eligibility, exceptional retention, DPA, subprocessors, deletion controls, and cross-border processing where customer law requires it | `OwnerAttested` | Data Protection / Legal | `DataProtectionApproved = true` |
| **9** | **Business Owner formal approval** | Named approver, role, decision, date, conditions. **Distinct from the eight policy answers already recorded in §13** | `OwnerAttested` | Business Owner | `BusinessOwnerApproved = true` |
| **10** | **Decision / Review / Expiry dates** | Three actual dates. **Expiry is mandatory** — an approval without one is refused by the evaluator | `OwnerAttested` | Business Owner | `ApprovedAtUtc`, `ExpiresAtUtc` *(review date: see §12.7 gap)* |

## 14.3 Order of work — items 1 and 2 gate the rest

Items 7–10 are signatures on a conclusion. Asking Security or Legal to sign before items 1–6 exist means
asking them to approve an assessment that has not been made.

```
  Procurement                Legal                 Infrastructure
       │                       │                        │
       ▼                       ▼                        ▼
  [1] ZDR granted        [2] DPA executed        [3] org + project ids
  [4] tier                [0] legal entity            owners, dev/prod split
       └───────────┬───────────┴────────────┬───────────┘
                   ▼                        ▼
        [5] subprocessors accepted   [6] deletion + exceptional
                   └───────────┬────────────┘   retention accepted
                               ▼
                 [7] Security   [8] Legal   [9] Business Owner
                               ▼
                    [10] decision · review · EXPIRY
                               ▼
                     evaluator re-run → decision
```

**Item 1 is the critical path.** If OpenAI declines ZDR for the CrossBuy account, items 5–10 are wasted
effort: the owner recorded ZDR as mandatory (D6A) and a 30-day default as explicitly unacceptable (D6C),
so a refusal makes the candidate `Rejected` and the correct response is to re-open Decision 8 rather than
to soften Decision 6.

## 14.4 Item 1 has a prerequisite in our own code

Recorded in §13.6 and repeated here because it lands exactly on this intake step:

**The model currently cannot represent "ZDR required but not granted."** When `ZeroRetentionRequired` is
true, `ProviderRetention` stops being a mandatory fact, and the breach check fires only on a *known*
non-zero value. So supplying evidence for item 1 will not, on its own, cause the evaluator to behave
differently — and neither would supplying nothing.

**Consequence for intake:** item 1's evidence must be recorded, but it will not be *enforced* until the
gap is closed. Closing it is an engineering task for the increment that populates the record — either
make `ProviderRetention` unconditionally mandatory, or add an explicit `ZeroRetentionGranted`
three-valued fact. Until then, item 1 is verified **by document, not by the runtime**, and this section
is the only thing preventing that from being overlooked.

> ### ✅ CLOSED 2026-08-15 by Increment 4.4 — and the severity above was understated
>
> The gap is fixed. `ZeroRetentionGranted` is now an explicit three-valued provider fact, required
> whenever the owner's policy demands ZDR, and a claim of "granted" must carry evidence at
> **`OwnerAttested` or `ContractProven`** — a strength a public documentation link cannot reach.
> **Item 1 is now enforced by the runtime, not only by this document.**
>
> **Correction:** §13.6 rated this **MEDIUM** because six other facts were missing, so nothing could
> approach approval. Reproducing it proved that reasoning wrong: with those six facts supplied and all
> three approvals signed, a provider with **no ZDR evidence whatsoever reached
> `ApprovedExternalProcessor`**. The gate was not weak — it was absent. Correct severity: **HIGH**.
>
> Full record: [`provider-decision-package.md`](provider-decision-package.md) §31.
>
> **Nothing about OpenAI changed.** Its grant remains `Unknown`; the evaluator's missing-fact list grew
> from six to seven because the gate became real.

## 14.5 What to send back

For each item: the fact, who established it, the evidence reference, and the date. That maps directly
onto `AiProviderEvidence` (`Fact`, `Level`, `Owner`, `Reference`), which already exists and is already
the shape the record expects.

**Do not send:** API keys, secrets, credentials, console tokens, or screenshots containing any of them.
Ids and names only.

## 14.6 What happens when evidence arrives

1. The fact is recorded in the candidate record with its evidence level and reference.
2. `AiProviderEvaluator` is re-run over the recorded policy and the updated candidate.
3. The resulting state is reported as it comes out — `UnderAssessment`, `OwnerDecisionRequired`,
   `Rejected` or `ApprovedExternalProcessor`.
4. **The record is populated only by a reviewed source change**, so approval remains harder to obtain
   than deployment. That was the point of Increment 4.3 and does not change here.

**Partial evidence produces a partial result, not a partial approval.** Ten of eleven items satisfied
still yields a non-approved state, and the runtime still fails closed.

---
---

# 15. PROVIDER EVIDENCE REGISTER

**Append-only.** Each intake attempt is recorded as its own dated entry. Earlier `UNKNOWN` states are
never overwritten — the history of what was known, and when, is the point of a register.

## ENTRY 1 — Item 1 · OpenAI Zero Data Retention · 2026-08-16

| Field | Value |
|---|---|
| **Verification item** | 1 — OpenAI ZDR for the CrossBuy account/project |
| **Provider fact** | `ZeroRetentionGranted` |
| **Status** | **UNKNOWN** — unchanged |
| **Evidence supplied** | **NONE** |
| **Evidence level** | `Unknown` — no `AiProviderEvidence` entry exists for `ZeroRetentionGranted` |
| **Evidence owner** | — (would be Procurement + Legal) |
| **Evidence reference** | — |
| **Evidence date** | — |
| **OpenAI organisation id** | **UNKNOWN / MISSING** |
| **OpenAI production project id** | **UNKNOWN / MISSING** |
| **Account / project owner** | **UNKNOWN / MISSING** |
| **Outcome** | **A — no CrossBuy-specific evidence available** |
| **Item 1 result** | **BLOCKED — EXTERNAL EVIDENCE REQUIRED** |

### What was actually done

The repository was searched for any supplied evidence — a written provider confirmation, a support-ticket
reference, console evidence, or contractual language. **Nothing was found.** Every match for
zero-retention terminology resolved to CrossBuy's own requirement text, its enforcement code, or its
tests. The only occurrence phrased as a status reads *"Is ZDR enabled for the CrossBuy account? —
UNKNOWN, NOT VERIFIED, no evidence exists."*

**No public documentation was consulted as verification.** OpenAI's ZDR documentation was already read
and recorded during the candidate evaluation; it establishes that the *product* offers ZDR subject to
prior approval, and it is deliberately **not** treated as evidence of a grant to CrossBuy. Re-reading it
would not have changed the answer, and calling that "verification" is the precise failure this register
exists to prevent.

### Why nothing was recorded

`ZeroRetentionGranted` stays `Unknown` because no acceptable evidence exists. Recording anything else
would have required inventing an organisation, a project, a confirmation or a date. The fact that the
runtime cannot be moved forward without external input is the control working, not a blocked task.

### Evaluator result — before and after this entry: identical

```
AiProviderState  = UnderAssessment
IsApproved       = false
DestinationClass = UnapprovedExternal
MissingFacts     = AccountOwner, CommercialTier, ContractInPlace, DeletionControlsAvailable,
                   LegalEntity, SubprocessorPositionAccepted, ZeroRetentionGranted
PolicyBreaches   = none
MissingApprovals = Security, Data Protection / Legal, Business Owner (formal)
Expiry           = NOT RECORDED
```

### What Procurement / Legal must obtain

> **Written confirmation from OpenAI that Zero Data Retention is enabled/granted for the named CrossBuy
> OpenAI organisation and production project.**

The confirmation must establish:

1. **what** was granted — zero data retention for normal request/response content;
2. **to whom** — the CrossBuy contracting entity;
3. **which scope** — the specific organisation id and production project id, or scope sufficient to
   establish that the grant covers the production environment;
4. **who** established or verified it — a named person on our side;
5. an **evidence reference** — ticket number, email reference, contract clause, or sanitised console
   evidence;
6. the **date**.

**Acceptable forms:** written provider confirmation · authorised account-representative statement ·
support-ticket confirmation · provider console evidence · executed contractual language.

**Not acceptable, and enforced as such by the evaluator:** public ZDR documentation · privacy or
marketing pages · "ZDR is generally available" · "we use Enterprise" · `ProviderRetention = 0` · an
application setting or environment variable · a developer assertion · an API key · the fact that OpenAI
was selected or ranked first.

**Never send:** API keys, tokens, passwords, credentials, session cookies. If console evidence contains
any of these, **do not commit the screenshot** — supply a sanitised reference instead.

### Scope note — this item does not settle Item 6

A granted ZDR would establish only the **normal** request/response retention arrangement. It would not
establish acceptance of Safety Retention, abuse-monitoring retention or legal-obligation retention, and
it must not set `DeletionControlsAvailable`, `SubprocessorPositionAccepted` or `ContractInPlace`. Item 6
remains separate and outstanding.

### If OpenAI declines

Record `ZeroRetentionGranted = No` with the real evidence. The candidate becomes **`Rejected`**, and the
correct response is to **re-open Owner Decision 8 (provider candidate)** — *not* to weaken Decision 6.
`ZeroRetentionRequired` stays `true` and the maximum normal retention stays 0 days.

---
---

# 16. OPENAI OPERATIONAL VERIFICATION PACKAGE — Item 1A, 2026-08-16

**Append-only.** §15's Entry 1 is unchanged. This section prepares the operational work needed to
*unblock* Item 1; it supplies no evidence and changes no provider state.

**Sendable request document:** [`openai-zdr-verification-request.md`](openai-zdr-verification-request.md)

## 16.1 Discovery result — nothing exists yet

The repository, all configuration files and all documentation were searched for an existing OpenAI
account identity. **Nothing was found**, and that is the honest starting point rather than a problem:

| Item | Status | Notes |
|---|---|---|
| OpenAI Organization ID | **UNKNOWN** | No reference anywhere in the tree |
| OpenAI Production Project ID | **UNKNOWN** | No reference anywhere in the tree |
| Business Owner | **UNKNOWN** | No named person recorded for the AI programme |
| Infrastructure Owner | **UNKNOWN** | Same |
| Development / Production separation | **UNKNOWN** | Cannot be separated before it exists |
| Any OpenAI configuration section | **NONE** | No `OpenAI:*` key in any `appsettings` file |
| Any OpenAI credential | **NONE** | No key, token or secret found — nor should there be |

The only match for "account / project owner" was CrossBuy's own §14.2 row *describing the requirement*.
A requirement is not evidence of its own satisfaction.

**Note on CrossBuy's existing `AiService` configuration:** `appsettings.json` and
`appsettings.Production.json` do carry an `AiService` section, but that is the **local Python ML service
on loopback** — unrelated to OpenAI, and it holds no provider account identity.

## 16.2 Required OpenAI environment — to be established by Infrastructure

**Do not create this from engineering.** It is documented so that when Infrastructure does create it, the
shape is already agreed.

```
   OpenAI Organization
        │
        ├── Development Project      — experimentation, never production data
        │
        └── Production Project       — CrossBuy Production only
                 │
                 └── CrossBuy Production
```

**Development and Production must be separate projects.** ZDR is granted per organisation or project;
sharing one project means a development experiment and a production request live under the same
retention arrangement, and a mistake in one becomes a mistake in the other.

### Ownership model — four distinct roles, none of them invented here

| Role | Responsibility | Currently |
|---|---|---|
| Business Owner | Accountable for the decision to use the provider | **UNKNOWN** |
| Infrastructure Owner | Owns the organisation/project and its lifecycle | **UNKNOWN** |
| Billing / Procurement Owner | Owns the commercial relationship and tier | **UNKNOWN** |
| Security Contact | Named recipient for provider security notices | **UNKNOWN** |

**A personal developer account must not be the sole production owner.** People leave; the production
environment must not leave with them.

## 16.3 Production project requirements

| Attribute | Required value |
|---|---|
| Purpose | CrossBuy Production AI |
| Environment | Production |
| Provider | OpenAI API |
| Zero Data Retention | **MANDATORY** |
| Business-data training | **NOT ALLOWED** |
| Personal data | **NOT ALLOWED** |
| Free-text business content | **DENIED BY DEFAULT** |
| Credential handling | **Server-side only** |
| Browser / mobile API-key exposure | **PROHIBITED** |
| Development credentials in Production | **MUST NOT be reused** |
| Production credentials in source control | **MUST NOT be committed** |
| Provider approval | **REQUIRED before any use** |

## 16.4 What Infrastructure must return

Once the organisation and production project legitimately exist:

1. OpenAI **Organization ID**
2. OpenAI **Production Project ID**
3. **Business Owner** — named
4. **Infrastructure Owner** — named
5. **Evidence that Development and Production are separate projects**
6. **Date established**
7. **Evidence reference**

**Do not return:** API key · secret · token · password. None of these is governance evidence, and
CrossBuy will not accept or store them as such.

## 16.5 Credential classification — stated once, plainly

| Value | Classification | May appear in governance evidence? |
|---|---|---|
| Organization ID | **Not a secret** — an identifier | **Yes** |
| Project ID | **Not a credential** — an identifier | **Yes** |
| API key | **SECRET** | **Never** |
| Token / password / session cookie | **SECRET** | **Never** |

If console evidence contains a secret, **do not commit the screenshot** — supply a sanitised reference.

## 16.6 Security rules for when the environment is created

Not implemented here, and deliberately so — this increment creates nothing.

**Do not:** share one project between Development and Production · make a personal developer account the
sole production owner · put API keys in `appsettings.json` · commit credentials · expose credentials to
JavaScript, Flutter/mobile or browser requests · send credentials by email, chat or documentation ·
reuse development credentials in production.

When production credentials are eventually authorised, they go through the approved **server-side**
secret-management mechanism. **That mechanism is not built in this task.**

## 16.7 Item status — unchanged

| | |
|---|---|
| **Item 1 — ZDR** | **BLOCKED — EXTERNAL EVIDENCE REQUIRED** |
| `ZeroRetentionGranted` | **UNKNOWN** |
| Organization ID | **UNKNOWN** |
| Production Project ID | **UNKNOWN** |
| `AiProviderState` | **UnderAssessment** |
| `IsApproved` | **false** |
| `DestinationClass` | **UnapprovedExternal** |
| External OpenAI egress | **DENY** |
| Increment 5 | **NO-GO** |

**Item 3 is NOT complete.** This section prepares its information requirements; the actual identifiers
and ownership evidence are still required. A template is not a fact.

**Item 2 (DPA) is deliberately not started.** Item 1 is the critical path: if OpenAI cannot grant the
required ZDR, the candidate is `Rejected` and Decision 8 re-opens — making downstream contract effort
wasted.

## 16.8 Human action sequence

| Step | Owner | Action |
|---|---|---|
| 1 | Infrastructure | Establish / identify the OpenAI **Organization** |
| 2 | Infrastructure | Establish a dedicated CrossBuy **Production Project**, separate from Development |
| 3 | Infrastructure | Return sanitised Organization ID, Production Project ID, Business Owner, Infrastructure Owner, proof of Dev/Prod separation |
| 4 | Procurement / Legal | Fill the two placeholders and send [`openai-zdr-verification-request.md`](openai-zdr-verification-request.md) to OpenAI |
| 5 | Procurement / Legal | Return OpenAI's written confirmation **or refusal** as evidence |

**No API key at any step.**

---
---

# 17. INCREMENT 4.9 — ENVIRONMENT / PROJECT-SCOPED PROVIDER GOVERNANCE

**Recorded 2026-08-16.** §§11–16 are unchanged. The eight owner decisions of §11 stand exactly as
recorded, and none is reinterpreted here. What follows narrows the *scope* those decisions apply to — it
does not change their content.

## 17.1 What changed about the shape of an approval

**BEFORE — provider-global.** An approval named a provider. "OpenAI is approved" was true of every
organisation, every project and every environment at once. A **Development approval would have
authorised Production traffic**, and no code could tell the difference.

**AFTER — scope-bound.** An approval names a provider **and** an organisation **and** a project **and**
an environment, and all four must match the request exactly. There is no wildcard and no inheritance
between environments.

> **A Development approval cannot approve Production.**
> A Production approval cannot approve Development either — it is tempting to read Production as "the
> stricter one, therefore it must include Development", and that reading is wrong. An approval is about
> one project.

## 17.2 Effect on the recorded owner decisions

| Decision (§11) | Effect |
|---|---|
| 1 — external LLM permitted in principle | **Unchanged.** Still permitted in principle, still approves nobody. |
| 2 — global residency accepted | **Unchanged**, and recorded as provider-wide. If it is ever narrowed to a region, residency becomes project-scoped and must be re-evaluated. |
| 3 — training on business data prohibited | **Unchanged.** Global CrossBuy policy, not duplicated per environment. |
| 4 — PersonalData never leaves | **Unchanged.** Verified still enforced (`SCOPE22`). |
| 5 — FreeTextBusinessContent default-deny | **Unchanged.** Verified still enforced (`SCOPE23`). |
| 6A — ZDR is a hard gate | **Narrowed in scope, not in strength.** ZDR evidence must now name the organisation and project it was granted for. A development-project grant no longer satisfies a production candidate. |
| 6C — exceptional retention not accepted | **Unchanged**, still outstanding. |
| 7 — DPA required before any production use | **Unchanged**, recorded as provider-wide (a DPA binds legal entities, not projects). |
| 8 — OpenAI SELECTED, not APPROVED | **Unchanged. SELECTED still does not mean APPROVED.** |

**Owner policy remains global.** It was not duplicated per environment, deliberately: a per-environment
policy invites a weaker development policy, which is precisely the failure this increment closes.

## 17.3 What the owner must now supply that was not required before

The verification package of §16 asked Infrastructure for an Organization ID and a Production Project ID.
Those are no longer merely *documentation* — the evaluator now reports them as **missing facts**, and
three further consequences follow for the approval paperwork:

| # | Requirement | Why |
|---|---|---|
| 1 | The **ZDR confirmation must name the organisation and project** it is granted for | Unscoped evidence cannot establish a scoped fact. A confirmation covering "CrossBuy" without naming a project satisfies nothing. |
| 2 | Security, Legal and Business Owner sign **per environment** | Each signature covers one scope. A development sign-off is not a production sign-off. |
| 3 | Every approval carries a **start date as well as an expiry** | An approval with no beginning is valid retroactively for all time, which makes "we approved this on the 14th" unprovable. |

Item 1A of the verification intake ([§16](#164-what-infrastructure-must-return)) is therefore now
enforced by code as well as by document: without the identifiers, no scope is complete, and an
incomplete scope is refused before anything else is considered.

## 17.4 Item status — unchanged

| | |
|---|---|
| **Item 1 — ZDR** | **BLOCKED — EXTERNAL EVIDENCE REQUIRED** |
| `ZeroRetentionGranted` | **UNKNOWN** |
| Organization ID | **UNKNOWN** (now an explicit MissingFact) |
| Development Project ID | **UNKNOWN** (now an explicit MissingFact) |
| Production Project ID | **UNKNOWN** (now an explicit MissingFact) |
| `AiProviderState` | **UnderAssessment** |
| `IsApproved` | **false** |
| `DestinationClass` | **UnapprovedExternal** |
| External OpenAI egress | **DENY** |
| Provider switch | **DISABLED** in every environment |
| Increment 5 | **NO-GO** |

**Nothing about OpenAI moved closer to approval.** The missing-fact list grew from 7 to 10 because the
record was always silent about which account it described, and that silence now reads as *missing*
rather than as *irrelevant*. A stricter model is not progress toward approval; it is a more honest
account of the distance remaining.

---
---

# 18. INCREMENT 4.10 — ACCOUNT REGISTERED · SYNTHETIC-ONLY DEVELOPMENT CONSTRAINT

**Recorded 2026-08-16.** §§11–17 are unchanged. The eight owner decisions of §11 stand exactly as
recorded. This section adds one new owner constraint and records the first established facts about the
OpenAI account.

## 18.1 Item 1A — CLOSED. The account identifiers exist.

| | |
|---|---|
| Organisation | `org-aUyGCBpWvfaqal2KIUuYkU9t` |
| Development project | `proj_GHPPdHzQWFh6VhXkPXV2dp7D` |
| Production project | `proj_tuL0VF1UOGgbPD1JX7wXW4k8` |

Development and Production are **separate projects**, which satisfies §16.3's production-project
requirement for separation. Identifiers, not secrets: they name an account and cannot authenticate to it.

**Item 1A is not the same as Item 1.** The identifiers are what the ZDR request needed in order to be
*sendable* — a grant must name the organisation and project it applies to. Item 1 itself remains open.

## 18.2 NEW OWNER CONSTRAINT — Development smoke test, synthetic content only

> CrossBuy Development may eventually perform a controlled OpenAI Development smoke test **only with
> fully synthetic test content**, created deterministically by the test itself.
>
> **Prohibited in that payload:** PersonalData · customer names · vendor names · employee names · email
> addresses · phone numbers · addresses · real invoice numbers · real journal descriptions · real
> free-text business content · uploaded documents · production database content · CrossBuyDB2 content ·
> credentials · secrets · authentication tokens · connection strings.

**What this constraint is NOT.** Stated explicitly because each of these is a reading someone could
reach for when the smoke test is blocked:

| It is not | Because |
|---|---|
| ZDR evidence | ZDR is a grant from the provider about retention. This is a rule about what CrossBuy sends. |
| Legal / Data Protection approval | No one signed anything. |
| Security approval | No one signed anything. |
| Business Owner production approval | No one signed anything. |
| Production authorisation | It is a *development* constraint and mentions production only to forbid it. |
| Permission to mark OpenAI `ApprovedExternalProcessor` | Approval requires seven outstanding facts and three signatures. |

The constraint is enforced in code, not merely written down: a payload labelled synthetic must pass an
exact-match allowlist at the adapter, and a payload that merely *claims* the label is refused before any
socket is opened.

## 18.3 What the identifiers changed — and what they did not

| | Before 4.10 | After 4.10 |
|---|---|---|
| Missing facts | 10 | **7** |
| `OrganizationId` / `ProjectId` / `Environment` | UNKNOWN | **established** |
| `AiProviderState` | `UnderAssessment` | `UnderAssessment` |
| `IsApproved` | false | **false** |
| ZDR | UNKNOWN | **UNKNOWN** |
| Security / Legal / Business Owner | unsigned | **unsigned** |
| External OpenAI egress | DENY | **DENY** |
| Increment 5 | NO-GO | **NO-GO** |

## 18.4 Remaining inputs, in the order they unblock each other

| # | Input | Owner | Note |
|---|---|---|---|
| 1 | **ZDR grant**, naming the organisation AND the project | Procurement / Legal | Item 1. The critical path — a refusal makes the candidate `Rejected` and re-opens Decision 8. Send [`openai-zdr-verification-request.md`](openai-zdr-verification-request.md) now that the identifiers exist. |
| 2 | **AccountOwner** — the named Infrastructure owner of the organisation and each project | Infrastructure | A mandatory fact and still missing. §16.4 asked for it; the identifiers arrived without it. |
| 3 | **LegalEntity** — which CrossBuy entity contracts with OpenAI | Legal | Mandatory fact. |
| 4 | **ContractInPlace** — an executed DPA | Legal / Procurement | Item 2. Deliberately not started before Item 1 resolves. |
| 5 | **CommercialTier** | Procurement | Mandatory fact. Also unblocks pricing, which is currently unconfigured. |
| 6 | **SubprocessorPositionAccepted** · **DeletionControlsAvailable** | Security / Legal | Published by the provider; not yet accepted by CrossBuy. |
| 7 | **Three signatures**, each scoped to Development or Production, each with a start AND an expiry date | Security · Legal · Business Owner | A Development signature does not cover Production. |

**Nothing on this list can be supplied by engineering**, which is why the increment ends here rather
than with a call.
