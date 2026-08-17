# CrossBuy — AI Provider Owner Decision Form

**To:** Security · Data Protection / Legal · Business Owner
**From:** Engineering (CrossBuy platform)
**Issued:** 2026-08-15 · **Please return by:** ____________

---

> # ⚠ PARTIALLY COMPLETED — 2026-08-15
>
> **The Business Owner has answered all eight policy decisions.** They are marked **[✓ ANSWERED]** below
> and recorded verbatim in [`provider-owner-decision.md`](provider-owner-decision.md) §13.
>
> **Still required from this sheet:**
>
> - **Security approval** — not recorded
> - **Data Protection / Legal approval** — not recorded
> - **Business Owner *formal* approval** — the policy answers were given; the approver name, decision,
>   date and conditions were not
> - **Decision date · Review date · Approval expiry date** — none recorded
> - **Provider details** — legal entity, account/project owner, commercial tier, subprocessor
>   acceptance, deletion-control acceptance, DPA verification: **all blank**
>
> **The selected provider is OpenAI API. It is SELECTED, not APPROVED.** Runtime state is
> `UnderAssessment`; external AI processing remains blocked.

---

**This sheet is the INPUT. It is not the record.** Answers written here must be transcribed into
[`provider-owner-decision.md`](provider-owner-decision.md) §13 to take effect. Until they are, the
system treats a decision as unanswered and refuses all external AI processing.

**Before answering**, please read the reasoning behind each question in
[`provider-owner-decision.md`](provider-owner-decision.md) §12. This sheet deliberately carries no
argument — only the choices.

**Three things to know:**

1. **Nothing is broken.** CrossBuy's AI features work today, run entirely on our own server, and send
   no data anywhere. Answering "no external provider" would have cost us nothing we currently have.
2. **Every box must be filled.** A blank is treated as "not decided", and not-decided blocks approval.
   That is intentional.
3. **An approval with no expiry date is not an approval.** The system rejects it.

---

## The eight decisions

### 1 — External LLM processing · **[✓ ANSWERED: YES]**
May CrossBuy send approved, filtered business data to an external AI provider at all?

☑ **YES — permitted, subject to all governance gates**  ☐ NO — prohibited  ☐ NOT DECIDED

> Recorded with the owner's qualification: *this does not authorise unrestricted data egress.*

### 2 — Residency boundary · **[✓ ANSWERED: GLOBAL]**
Where may our data be processed?

☐ Kuwait only ☐ Kuwait + named GCC countries ☐ GCC ☐ Named Middle East set ☐ Explicit allow-list
☑ **Other — GLOBAL / no product-level geographic restriction** ☐ NOT DECIDED

> Recorded as a **product-level** policy. It does not override customer-specific legal requirements,
> tenant-specific residency requirements, deployment-specific restrictions, local privacy law or
> contract requirements. A future tenant may impose stricter residency, and the architecture can still
> enforce it.

### 3 — Training / data use · **[✓ ANSWERED: NO]**
May the provider use our data to train or improve its models? *(Separate from the provider reading a
request in order to answer it, which is unavoidable.)*

☑ **NO** ☐ YES, under conditions ☐ NOT DECIDED

> Recorded with the owner's condition: *training/data-use evidence must be verified before approval.*

### 4 — Personal data · **[✓ ANSWERED: NEVER]**
May data classified as personal ever be sent externally?

☑ **NEVER** ☐ Only under a separately approved privacy policy ☐ NOT DECIDED

*Current behaviour is DENY and is unchanged. Provider approval must not override it.*

### 5 — Free text · **[✓ ANSWERED: CONDITIONAL — default DENY]**
May free-text business content ever be sent externally?

☐ NEVER ☑ **Selected classes, after explicit per-class review** ☐ NOT DECIDED

> Default remains **DENY**. A category may be allowed in future only after explicit classification,
> data-owner review, security review, purpose-specific approval and a minimised outbound DTO.
> `JournalEntry.Description`, task descriptions, CRM notes, email bodies, comments and documents all
> remain **DENIED** today.

### 6 — Retention

**6A — Is zero data retention mandatory?** · **[✓ ANSWERED: YES]**
☑ **YES** ☐ NO ☐ NOT DECIDED

**6B — Maximum acceptable provider-side retention:** · **[✓ ANSWERED: 0 DAYS]**
☑ **0 days** ☐ 7 days ☐ 30 days ☐ Custom ☐ NOT DECIDED

**6C — Required deletion / lifecycle behaviour:** · **[✓ ANSWERED]**
☑ **Specified:** no persistent provider-side storage of normal business request/response content after
processing. Any exceptional retention for legal obligation, abuse monitoring, security or regulation
must be **documented, separately identified, reviewed by Security and Legal/Data Protection, and
accepted before production approval.**
☐ The provider's documented behaviour is accepted as sufficient ☐ NOT DECIDED

> Explicitly **not** accepted: a provider's default 30-day retention.

### 7 — Contract / DPA · **[✓ ANSWERED: REQUIRED]**

☑ **An executed / verified DPA or equivalent is required**
☐ A different contractual standard ☐ No additional contract required ☐ NOT DECIDED

> "DPA available" ≠ "DPA verified and accepted by CrossBuy."

### 8 — Provider candidate · **[✓ ANSWERED: OPENAI API]**

☐ Microsoft Azure OpenAI / Microsoft Foundry
☑ **OpenAI API — SELECTED**
☐ Anthropic Claude API
☐ NO EXTERNAL PROVIDER
☐ DEFER DECISION

> ### SELECTED ≠ APPROVED
> OpenAI API becomes an approved external processor only when every mandatory fact, all three approvals
> and the expiry condition pass. Runtime state today: `UnderAssessment`.

---

## Provider comparison — retained for the record

| | Azure OpenAI | **OpenAI API (selected)** | Anthropic Claude |
|---|---|---|---|
| Who holds our data | Microsoft | **OpenAI** | Anthropic |
| Trains on our data | No | **No** | No |
| Default retention | up to 30 days | **up to 30 days** | not retained by default |
| "Keep nothing" obtainable | only if Microsoft manages our account | **with prior OpenAI approval** | on request |
| Gulf processing possible | yes — reserved capacity only | yes — pay-per-token, with approval | no — US only |
| Private network possible | yes | **no** | no |
| Company logins instead of a key | yes | **no** | no |
| Formal contract available | yes | **yes** | yes |
| Independent certifications | Azure catalogue | **ISO 27001 family, SOC 2** | SOC 2, ISO 27001, ISO 42001 |
| Setup complexity | highest | **low** | low |

*Decision 2 = GLOBAL removed residency as a discriminator. The owner selected OpenAI on grounds beyond
the documented-capability scorecard, which is the owner's prerogative; see
[`provider-decision-package.md`](provider-decision-package.md) §30.*

---

## Still-unanswered commercial questions — OpenAI

**None answered. None may be assumed favourably.**

| # | Question | Who must find out |
|---|---|---|
| 1 | **Would OpenAI grant CrossBuy Zero Data Retention?** — the deciding gate | Procurement + Legal |
| 2 | Which CrossBuy legal entity would sign? | Legal |
| 3 | Which OpenAI organisation / project, and who owns it? | Infrastructure |
| 4 | What commercial tier are we on? | Procurement |
| 5 | Is "Safety Retention" (retention on classifier hit, surviving ZDR) acceptable? | Security + Legal |
| 6 | Is the published subprocessor list acceptable? | Data Protection / Legal |

*(Azure- and Anthropic-specific questions are retained in the package for the record but are no longer
on the critical path.)*

---

## Provider details required before approval — ALL BLANK

```
Provider legal entity:                  ______________________________________
OpenAI organisation id:                 ______________________________________
OpenAI production project id:           ______________________________________
Account / project owner (named):        ______________________________________
Environment separation (dev/prod):      ______________________________________
Commercial tier:                        ______________________________________
ZDR granted for the production project: ☐ YES  ☐ NO      evidence: ____________
Subprocessor position accepted:         ☐ YES  ☐ NO
Deletion / lifecycle controls accepted: ☐ YES  ☐ NO
Exceptional-retention behaviour accepted: ☐ YES ☐ NO
Contract / DPA executed and verified:   ☐ YES  ☐ NO      reference: ____________
```

**Do not write API keys or secrets on this sheet.**

---

## Approvals — all three are required · **ALL NOT RECORDED**

```
SECURITY                                              STATUS: NOT RECORDED
  Approver: ____________________  Role: ____________________
  Decision: ☐ APPROVED  ☐ REJECTED        Date: ______________
  Conditions: ______________________________________________
  Signature: ____________________

DATA PROTECTION / LEGAL                               STATUS: NOT RECORDED
  Must specifically review: ZDR eligibility · exceptional abuse/legal retention · DPA ·
  subprocessor position · deletion controls · cross-border processing where
  customer-specific law requires it.
  Approver: ____________________  Role: ____________________
  Decision: ☐ APPROVED  ☐ REJECTED        Date: ______________
  Conditions: ______________________________________________
  Signature: ____________________

BUSINESS OWNER — FORMAL APPROVAL                      STATUS: NOT RECORDED
  (The eight policy decisions above were supplied and are recorded. This is the separate
   formal approval of a named provider, with a date and an expiry.)
  Approver: ____________________  Role: ____________________
  Decision: ☐ APPROVED  ☐ REJECTED        Date: ______________
  Conditions: ______________________________________________
  Signature: ____________________
```

---

## Dates — ALL NOT RECORDED

```
  Decision date:  ______________
  Review date:    ______________

  ╔═══════════════════════════════════════════════════════════════════╗
  ║  APPROVAL EXPIRY DATE (MANDATORY):  ______________                ║
  ║                                                                   ║
  ║  NO EXPIRY  =  NO VALID APPROVAL. The system rejects it.          ║
  ║  On expiry, external AI processing stops automatically.           ║
  ║  Local AI is unaffected. There is no automatic extension.         ║
  ╚═══════════════════════════════════════════════════════════════════╝
```

---

## Final checklist — Increment 5 proceeds only when every box is ticked

```
[x] External LLM processing decision recorded          — YES
[x] Residency boundary recorded                        — GLOBAL
[x] Training / data-use requirement recorded           — NO training
[x] Personal data policy recorded                      — NEVER
[x] Free text policy recorded                          — conditional, default DENY
[x] Retention / ZDR requirement recorded               — ZDR, 0 days, 6C specified
[x] Contract / DPA requirement recorded                — required + verified
[x] Provider candidate selected                        — OpenAI API
[ ] Provider legal entity verified
[ ] Commercial tier verified
[ ] Residency capability verified
[ ] Retention capability verified            <-- ZDR NOT GRANTED / NOT VERIFIED
[ ] Training / data-use capability verified  <-- documented; contract term outstanding
[ ] Contract / DPA verified (executed, not merely available)
[ ] Subprocessor position accepted
[ ] Deletion / lifecycle controls accepted
[ ] Security approval signed
[ ] Data Protection / Legal approval signed
[ ] Business Owner formal approval signed
[ ] Decision date recorded
[ ] Review date recorded
[ ] Approval expiry date recorded
[ ] Runtime evaluator returns approved       <-- currently UnderAssessment
[ ] Runtime authority returns approved       <-- currently false
```

**8 of 24 satisfied. INCREMENT 5 = NO-GO.**

---

*This form contains no passwords, keys or confidential commercial terms and may be circulated
internally as-is.*
