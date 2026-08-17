# OpenAI — Zero Data Retention Verification Request

**Prepared by:** Engineering (CrossBuy platform) · **Date:** 2026-08-16
**For:** Procurement · Legal / Data Protection · Infrastructure · and onward to the OpenAI account team
**Status:** READY TO SEND once the organisation and production project exist

> **This document contains no credentials and is safe to circulate.** It contains no API key, token,
> password or authentication material, and it asks OpenAI not to send any either.

---

## 1. Purpose

CrossBuy has selected the OpenAI API as its candidate external AI provider. **Selection is not
approval.** Before any production use, CrossBuy must verify — with written evidence — that Zero Data
Retention is actually **granted for the specific OpenAI production environment CrossBuy will use.**

This is the single item blocking every downstream approval step. It cannot be satisfied by
documentation: OpenAI publishes that ZDR exists and requires prior approval, which establishes the
product's capability and says nothing about our account.

## 2. The CrossBuy requirement

Recorded by the Business Owner on 2026-08-15 and enforced in CrossBuy's code:

| Requirement | Value |
|---|---|
| Zero Data Retention | **MANDATORY** for normal business request/response content |
| Maximum normal provider-side retention | **0 days** |
| Provider training on CrossBuy or customer business data | **NOT PERMITTED** |
| Personal data sent externally | **NEVER** |
| Free-text business content sent externally | **DENIED** by default |
| Executed DPA / contract | **REQUIRED** and must be verified |

**These are not negotiable to accommodate a provider.** If the required ZDR arrangement cannot be
granted, CrossBuy re-opens its provider selection rather than relaxing the requirement.

## 3. Environment being verified

Both identifiers must be supplied by Infrastructure before this request is sent. **Do not send this
request with the placeholders unfilled** — a request that cannot name its scope cannot receive an answer
that proves scope.

```
OpenAI Organization ID:      [OPENAI ORGANIZATION ID]        <- currently UNKNOWN
OpenAI Production Project:   [OPENAI PRODUCTION PROJECT ID]  <- currently UNKNOWN
```

---

## 4. The request — copy from here

> **SUBJECT:** Zero Data Retention Request for CrossBuy Production API Environment
>
> Hello OpenAI Team,
>
> We are preparing the production integration of CrossBuy with the OpenAI API.
>
> Zero Data Retention is a mandatory security and data-governance requirement for normal CrossBuy
> business request and response content.
>
> We need written confirmation that Zero Data Retention is enabled/granted for the production OpenAI
> environment that will be used by CrossBuy.
>
> **Organization:** `[OPENAI ORGANIZATION ID]`
> **Production Project:** `[OPENAI PRODUCTION PROJECT ID]`
>
> Please confirm:
>
> 1. Whether this organization/project is eligible for Zero Data Retention.
> 2. Whether ZDR is enabled/granted for this specific production environment.
> 3. Whether ZDR applies at organization level, project level, endpoint level, or another scope.
> 4. Which OpenAI API endpoints/features are compatible with ZDR.
> 5. Which endpoints/features, if any, cannot operate under ZDR.
> 6. Whether any application state is retained when using the ZDR-compatible API modes.
> 7. Whether any exceptional retention may still occur for: security; abuse monitoring; safety; legal
>    obligations; regulatory requirements.
> 8. The maximum duration and scope of any such exceptional retention.
> 9. Whether any contractual/commercial/account-tier requirements apply to enabling or maintaining ZDR.
> 10. Whether any configuration or feature choice can silently cause a request to fall outside the ZDR
>     arrangement.
> 11. Whether OpenAI notifies the customer if the ZDR arrangement changes, lapses or is withdrawn, and
>     through which channel.
> 12. Whether the ZDR arrangement has an expiry or renewal condition, and if so what it is.
>
> Please provide written confirmation identifying the organization and production project, or otherwise
> identifying a scope sufficient to prove that the ZDR grant applies to the CrossBuy production
> environment.
>
> Please do not include API keys, credentials or authentication secrets in the response.
>
> Thank you.

**Questions 11 and 12 are additions by CrossBuy Engineering**, and they are here for a specific reason:
CrossBuy's governance model treats an approval with no expiry as invalid and automatically suspends
external processing on expiry or revocation. If OpenAI's ZDR arrangement can lapse or be withdrawn
without notice, CrossBuy needs to know that in order to set a realistic review date — otherwise our
record would claim a grant that had quietly ended.

---

## 5. Evidence acceptance checklist

The response is accepted as evidence only when **every** box can be ticked. Do not tick a box in
anticipation.

```
[ ] Provider is OpenAI
[ ] ZDR explicitly granted / enabled — not merely "available" or "eligible"
[ ] CrossBuy production scope identifiable
[ ] Organization identifiable
[ ] Production Project identifiable, OR the confirmation clearly establishes equivalent scope
[ ] Normal request/response retention behaviour stated
[ ] ZDR-compatible endpoint scope stated
[ ] Incompatible / stateful features identified
[ ] Exceptional retention disclosed (kind, trigger, duration)
[ ] Commercial / contract conditions disclosed
[ ] Evidence reference available (ticket number, email reference, contract clause)
[ ] Evidence date available
[ ] Reviewed by a responsible, named CrossBuy owner
```

**Eligibility is not a grant.** A reply saying "your organisation is eligible for ZDR" satisfies box 1
and fails box 2. That distinction is the whole purpose of this request.

## 6. What must NOT be sent — in either direction

**Never send to OpenAI, and never accept back:**

API keys · secret tokens · passwords · session cookies · any authentication material.

If console evidence (a screenshot of the ZDR setting, for instance) contains any of these, **do not
commit or circulate it.** Redact it, or supply a written reference to it instead.

| Value | Classification |
|---|---|
| OpenAI Organization ID | **Not a secret** — an identifier. Safe in governance evidence |
| OpenAI Project ID | **Not a credential** — an identifier. Safe in governance evidence |
| API key | **SECRET** — never governance evidence, never in this document, never in email |
| Token / password / session cookie | **SECRET** — same |

The evidence package contains **identifiers and references only**.

---

## 7. Return-information template

Fill in and return this block. Leave anything unknown as `[TO BE PROVIDED]` — a blank is safer than a
guess, and CrossBuy's evaluator treats unknown as blocking rather than as satisfied.

```
OPENAI ZDR VERIFICATION

Provider:                                OpenAI API

CrossBuy Legal Entity:                   [TO BE PROVIDED]
OpenAI Organization ID:                  [TO BE PROVIDED]
OpenAI Production Project ID:            [TO BE PROVIDED]
Business Owner:                          [TO BE PROVIDED]
Infrastructure Owner:                    [TO BE PROVIDED]

Development Project:                     [TO BE PROVIDED]
Production Project:                      [TO BE PROVIDED]
Development / Production Separation:     [PROVEN / NOT PROVEN]

ZDR Status:                              [GRANTED / NOT GRANTED / UNKNOWN]
ZDR Scope:                               [TO BE PROVIDED]
Compatible API Endpoints:                [TO BE PROVIDED]
Incompatible / Stateful Features:        [TO BE PROVIDED]
Normal Retention:                        [TO BE PROVIDED]
Exceptional Retention:                   [TO BE PROVIDED]
Commercial / Contract Conditions:        [TO BE PROVIDED]
Change / Withdrawal Notification:        [TO BE PROVIDED]
ZDR Expiry / Renewal Condition:          [TO BE PROVIDED]

Evidence Type:                           [TO BE PROVIDED]
Evidence Reference:                      [TO BE PROVIDED]
Evidence Date:                           [TO BE PROVIDED]
Verified By:                             [TO BE PROVIDED]
Evidence Level:                          [OwnerAttested / ContractProven]

IMPORTANT — DO NOT INCLUDE:
  API KEY · SECRET · TOKEN · PASSWORD · SESSION COOKIE · AUTHENTICATION MATERIAL
```

**Evidence Level must be `OwnerAttested` or `ContractProven`.** CrossBuy's evaluator rejects anything
weaker, including a link to OpenAI's public ZDR documentation — that records the product's capability,
not a grant to us.

---

## 8. What happens to the answer

| Answer | Recorded as | Resulting provider state |
|---|---|---|
| **Granted**, evidence sufficient | `ZeroRetentionGranted = Yes` + evidence entry | ZDR gate passes; **still not approved** — six other facts, three signatures and three dates remain outstanding |
| **Not granted / cannot be granted** | `ZeroRetentionGranted = No` + evidence entry | **`Rejected`** → re-open Owner Decision 8 (provider candidate). CrossBuy does **not** weaken its retention requirement to make a provider pass |
| **No answer, or eligibility only** | stays `Unknown` | **`UnderAssessment`** — unchanged; external AI stays blocked |

In all three cases CrossBuy's local AI features continue to run unaffected, on our own server.
