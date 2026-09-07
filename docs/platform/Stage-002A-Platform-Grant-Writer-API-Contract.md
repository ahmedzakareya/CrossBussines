# Stage 2A — Platform Grant Writer — API Contract

`PlatformGrantsApiController` — `/api/platform/grants`. **Not the Security Console:** five endpoints, thin, no
business logic. Every decision belongs to `IPlatformGrantWriter`; this controller only translates its typed outcome
into HTTP.

---

## 1. Endpoints

| Verb | Route | Purpose |
|---|---|---|
`GET` | `/api/platform/grants?companyId=&scope=&principalId=&role=&scopeBranchId=&includeInactive=` | direct grants (≤ 500) |
`GET` | `/api/platform/grants/{id}?companyId=` | one grant |
`POST` | `/api/platform/grants` | create |
`POST` | `/api/platform/grants/{id}/revoke` | revoke |
`POST` | `/api/platform/grants/{id}/validity` | update validity |

`companyId` is **required everywhere** and validated inside the writer against the resolved `BusinessContext`. It is
never defaulted from the context: a silent default is how a caller ends up reading a company it did not ask about, and
it removes the writer's chance to refuse a mismatch.

## 2. Status mapping

| Outcome | Status | Body |
|---|---|---|
`Success` (create) | **201** + `Location` | `{ success: true, grant }` |
`Success` (revoke/validity/get) | **200** | `{ success: true, grant }` |
`IdempotentReplay` | **200** | `{ success: true, replayed: true, grant }` |
`AlreadyRevoked` | **200** | `{ success: true, alreadyRevoked: true, grant }` |
`ValidationFailed` | **400** | `{ success: false, message, errors[] }` |
`Forbidden` | **403** | `{ success: false, message }` — **no detail** |
`NotFound` | **404** | `{ success: false, message }` |
`Duplicate` · `Conflict` · `Expired` | **409** | `{ success: false, message }` |
unauthenticated | **401** | `{ success: false, message }` |

**201 vs 200 on create:** a real creation is 201; an idempotent replay is 200, because nothing was created this time
and a caller counting 201s must not count a retry twice.

## 3. Information disclosure

* **403 carries no detail.** Which authority tier was missing, which company resolved, and whether the target exists
  are all facts an unauthorized caller must not learn.
* **A foreign-company grant is 404**, identical to one that never existed. The company predicate is part of the
  lookup, so the writer never learns whether the row exists elsewhere either.
* **An unauthorized `GET` list returns an empty 200, not 403.** A 403 on a company id confirms the company exists and
  that grants are administered in it. The writer logs the denial, so the refusal stays auditable without being
  disclosed. This is the one endpoint where the empty-result shape is deliberate rather than incidental.
* **Never an HTML redirect.** A `fetch()` caller reads a 302-to-login as success with an unparseable body — the exact
  defect `ApiPermAttribute` was created for.

## 4. Anti-forgery

Not applied. These are `[ApiController]` JSON endpoints under `/api/`, matching the project's existing API-controller
pattern (`AccountingApiController`), where authentication is cookie/JWT and the response shape is JSON. A11 asks for
anti-forgery "where the current application pattern requires it" — for this shape it does not. **When the Batch M UI
posts from a Razor page, that page's calls must carry the token**, and this is the note that says so.

## 5. Authorization, and why the controller holds none

`[Authorize]` alone is authentication, not authorization — the analyzer's CBA002 is right about that. The
authorization is **in the body**, in the writer, and it is exhaustive: every action refuses an unauthorized caller
before touching a row.

Re-expressing any part of it here would create a second answer to the same question, and the two would diverge the
first time either changed. A grant-administration check is not a single permission — it is an authority tier, a
company match, a scope match and a privilege ceiling resolved through four different access services.

This is why `IPlatformGrantWriter` was added to the analyzer's declared authority surface: it genuinely is one. See
the delivery report §3.

## 6. Request bodies

Separate from the writer's commands, so a caller cannot post a `GrantId` that contradicts the route.

```jsonc
// POST /api/platform/grants
{ "companyId": 1, "scope": "Hr", "principalType": "Employee", "principalId": 9002,
  "role": "PayrollOfficer", "scopeBranchId": null,
  "validFrom": null, "validTo": null, "reason": "...", "idempotencyKey": "optional" }

// POST /api/platform/grants/{id}/revoke        reason REQUIRED
{ "companyId": 1, "reason": "left the payroll team" }

// POST /api/platform/grants/{id}/validity      reason REQUIRED
{ "companyId": 1, "validFrom": null, "validTo": "2026-12-31T00:00:00Z", "reason": "secondment ends" }
```

`scope` accepts `Hr` · `Projects` · `Tasks` · `Communication`. **`Pos` is refused** — `BranchUserRoles` remains the
permanent exception. `principalType` accepts only `Employee`; every other known kind is refused because a grant nobody
can evaluate must not exist.
