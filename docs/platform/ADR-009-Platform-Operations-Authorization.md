# ADR-009 — Platform-operations screens are gated by composing existing rights, and opening one grants no payload

**Status:** Accepted, implemented in Stage 0 (Slice-003) Batch B.

## Context

The Business Event Monitor exposes, in one place, data that no existing screen does: event payloads across every
module, the actor behind each fact, correlation ids that link one operation's events together, and dispatch failure
text. It must not be reachable by an ordinary authenticated employee.

Nothing in the system fits. There are **four access services with three action vocabularies**
(`AccountingAccessService` `read/post/pay/manage`, `InventoryAccessService` `read/doc/purchase/manage`,
`CrmAccessService` `read/edit/manage`, and POS sync predicates), none of which has a concept of "platform operator".
There is no platform-operations role table, and creating one is Stage 1 RBAC work — it needs the single access
contract that gap B3 describes, or it becomes a fifth vocabulary.

A second, separable question: **does being allowed to open the screen mean being allowed to read every payload on
it?** Business events carry four visibility tiers (ADR-004), and `Restricted`/`System` events exist precisely because
some facts are not for general reading.

## Decision

### 1. Compose the two strongest existing signals; invent no third

`PlatformOpsAttribute` (an `IAsyncActionFilter`) allows a request when **either**:

- the user holds an ASP.NET Identity role in `Admin`, `Administrator`, `SuperAdmin`, `PlatformOps`; **or**
- `IAccountingAccessService.CanAsync("manage")` returns true — the ChiefAccountant tier, which is already the highest
  existing business right and already governs period close, year-end and role assignment.

Composing beats inventing here. A new role table would be a fifth vocabulary, and it would have to be seeded — so on
every existing install it would start empty, and an empty role table either locks everyone out or lets everyone in.
Neither is better than reusing a right that is already administered.

### 2. Elevation is a separate, narrower right

Three actions require the **admin role specifically** — the accounting tier is not enough:

- viewing across companies;
- seeing `Restricted`/`System` payloads;
- overriding `MaxAttempts` on a retry.

`PlatformOpsAttribute.IsElevated(HttpContext)` is the single predicate, and the controller passes it explicitly to the
service rather than the service inferring it.

### 3. Opening the screen grants no payload it should not

`Restricted` and `System` payloads are **masked** — absent from the response, not hidden by CSS — unless the caller is
elevated. The metadata still renders, so the row stays diagnosable without it.

This is stated as a decision because the tempting simplification is "if you can open the monitor you can see
everything on it". That would make the visibility tiers meaningless for anyone with operator access, and operator
access is deliberately broad (see §1).

### 4. A non-admin asking for elevation is refused, not downgraded

```csharp
if (elevatedOverride && !PlatformOpsAttribute.IsElevated(HttpContext))
    return Json(new { ok = false, outcome = "NotAuthorized", … });
```

Silently performing a normal retry instead would look like the override worked. The operator would believe the
ceiling had been raised, see the row go `Pending`, and be confused when it stops one attempt later.

### 5. Every rule lives in the service; the controller adds nothing

`BusinessEventMonitorController` is deliberately thin. Company isolation, payload masking, paging, filter validation
and retry eligibility are all in `IBusinessEventMonitorService`, so **there is no second place an authorization rule
could be forgotten**. `crossCompany` and `maySeeRestricted` are passed in as booleans derived from one predicate.

### 6. Company isolation answers identically to "not found"

A cross-company `GetDetailsAsync` returns `null`, exactly as a missing id does. A cross-company `RetryAsync` returns
`NotFound` with **byte-identical message text** to a genuinely missing row. A probe must not be able to tell "exists
elsewhere" from "does not exist"; a test asserts the two messages are equal.

## Consequences

**On an install with no accounting roles configured, this gate is open to any authenticated employee.**
`AccountingAccessService.CanAsync` returns `true` for every action when its role table is empty company-wide —
"open when unconfigured". This is a property of the existing access service, not of this attribute, and it is
**documented in the attribute's own summary comment, in the operations guide, and here** rather than hidden. It is
also the clearest argument for Stage 1's access-service work: this ADR inherits that weakness by choosing to compose
rather than invent, and that trade was made knowingly.

**Denial behaviour matches the existing attributes.** AJAX/JSON callers get `403`; page requests get a redirect to
Home with an Arabic `TempData["PlatformErr"]` message — the same shape as `AccPerm`/`InvPerm`, so the UX is
consistent.

**Diagnostics are behind the same gate.** `GET /BusinessEventMonitor/Runtime` returns instance ids, PIDs and machine
names. That is infrastructure detail, not public information, so it is not a public health endpoint.

**The monitor is not exposed through `PlatformTimelineController`.** The document timeline is an end-user widget with
a different audience and a different gate; merging them would have forced one authorization model onto both.

## Alternatives rejected

**A new `PlatformOpsRoles` table.** Rejected for this stage — a fifth vocabulary, needing a seeding story, on top of
the four access services gap B3 already flags for consolidation. Revisit after B3.

**`[Authorize(Roles = "Admin")]` alone.** Rejected: on this install Identity roles are sparsely used and the
accounting tier is the right that is actually administered. It would have made the screen unreachable for the people
who need it.

**Reusing `[AccPerm("manage")]` directly.** Rejected: it says *accounting* manage. Reading manufacturing and POS event
payloads is not an accounting operation, and encoding it as one would make the permission model less honest, not
more.

**Serving Restricted payloads and hiding them in the view.** Rejected outright — the data would be in the HTTP
response.

## Verification

`Slice3EventMonitorTests` (30) covers: non-elevated callers pinned to their own company; a hand-edited `CompanyId`
query value that cannot widen scope; elevated callers crossing companies **and** narrowing to one; details
indistinguishable from not-found across companies; `Restricted` and `System` payloads masked with the secret value
absent from the mask reason, and visible when elevated; `Internal`/`Confidential` visible without elevation; and
cross-company retry refused with a message byte-identical to the not-found message.