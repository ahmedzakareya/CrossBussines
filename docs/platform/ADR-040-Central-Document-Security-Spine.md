# ADR-040 — Central Document Platform: the shared security spine

**Status:** Accepted · **Owner:** Platform (TAB-1) · **Consumer:** Document domain (TAB-3) · **Date:** 2026-08-30

This ADR is the handoff. It records the security decisions the document platform is built on, so that
building `PlatformDocuments` does not mean reopening any of them.

---

## 1. The defect this exists to close

`PrivateFileGate` closed the *anonymous* hole and is honest about where it stops: it declares a
`RequireCompanyMatch` policy and applies it **to no prefix**. That is not an oversight. The gate is
`UsePrivateFileGate` at `Program.cs:739`, immediately before `UseStaticFiles`; `UseSession()` is at
line 803. The middleware runs in a position where **no `BusinessContext` can be resolved** — reaching
for the session there would either throw or deny every request.

So today, for every protected prefix, **knowing the URL and being signed in as anyone is enough**.
GUID filenames make the store un-enumerable; they do not make it authorized. A URL that leaks through
a forwarded mail, a proxy log or a shared browser grants permanent access with no way to revoke it.

**The rule:** a path is not an authorization. Authorization derives from the authenticated actor, the
`BusinessContext`, the owning company, the owning business record and the document's confidentiality —
never from GUID unpredictability, and never from the path alone.

The consequence for design: the decision **cannot** live in that middleware. It has to be taken per
document, where a `BusinessContext` exists.

---

## 2. Protected prefix matrix (as committed)

| Prefix | Current policy | Business documents? | Company derivable | Owning entity derivable | Target policy | Action |
|---|---|---|---|---|---|---|
| `/uploads/hr-docs` | RequireAuthenticated | **Yes** | Yes — `EmployeeDocument.CompanyID`, `HrDocumentAttachment.CompanyID` | Yes — `EmployeeID`, or `OwnerKind`/`OwnerID` → document/contract → employee | Resolver-gated download | **Onboard first** — resolver landed |
| `/uploads/applicants` | RequireAuthenticated | Yes | Via `ApplicationID` → `JobApplication.CompanyID` | Yes — `ApplicationDocument.ApplicationID` | Resolver-gated download | Needs a `JobApplication` registry code |
| `/uploads/library` | RequireAuthenticated | Yes | Yes — `LibraryItem` is company-scoped | Yes — the library item itself | Already company-bounded in the controller | No change |
| `/uploads/chat` | RequireAuthenticated | Attachments | Via thread participants | Yes — Communication owns it | Communication's own gate | Out of scope |
| `/uploads/comm` | RequireAuthenticated | Attachments | Via thread/event | Yes | Communication's own gate | Out of scope |
| `/uploads/employees` | RequireAuthenticated | Photographs (PII) | Yes — `Employee.EmpCompanyID` | Yes | Resolver-gated | Candidate, low severity |
| `/Files/{companyId}` | RequireAuthenticated | **Mixed** | Partly | **No, for one class** | See §3 | Split required |

Everything not listed stays `Public` and untouched — the policy is an allow-list of private prefixes,
not a filter over all static content.

---

## 3. `/Files/{companyId}` — classified, not solved

The path holds two different things:

1. **Company-owned business files.** Company id is in the route and on the row. Strict company matching
   is correct for these.
2. **Hierarchical organization files** (org-chart logos). `Hierarchical` **carries no `CompanyID`** — it
   is the single shared org-resolution substrate, which is why Batch B's global query filters skip it,
   and it has already been ruled *outside* HR ownership (`AddHierarchicalItem` is Organization
   Administration, one of the three cross-owner gaps).

Enforcing a company match across the whole prefix today would blank the org chart on a certified
screen. **The rejected shortcuts stay rejected:** no assigning Hierarchicals to company 1, no invented
`CompanyID`, no weakened company matching, no making `/Files` public.

**Decision:** the legacy exception is separated rather than the secure rule weakened — but the split
cannot be *implemented* here, because deciding the tenancy of a Hierarchical file is Organization
Administration's call, not the document platform's. This is the one remaining P0 blocker, recorded in
§9.

---

## 4. The contract

```csharp
Task<DocumentAccessDecision> AuthorizeAsync(
    BusinessContext? context,
    DocumentOwnerRef owner,          // (EntityRegistry code, entity id)
    DocumentAction action,           // View | Download | Upload | Replace | Delete | Manage
    int documentCompanyId,
    string? confidentiality = null,
    CancellationToken cancellationToken = default);
```

One question: *may this `BusinessContext` perform this action on documents belonging to this record?*

The centre contains **no** `if (entityType == "Employee")`. It never names a module, an entity family
or a module's action vocabulary.

---

## 5. Module authority adapter

Each owning module supplies one `IDocumentOwnerResolver`:

```csharp
string EntityType { get; }
Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken ct);   // null = does not exist
PermissionTarget TargetFor(int entityId, int companyId);
string ModuleActionFor(DocumentAction action);                        // the module's OWN vocabulary
```

`ModuleActionFor` is the piece that matters. HR says `employee-view`; Inventory says `read`. Keeping
the mapping with the module is what stops the resolver growing a switch over module action strings.
`EmployeeDocumentOwnerResolver` is the reference implementation and maps read→`employee-view` (not
`read`, which only means "HR screens exist for you"), write→`employee-manage`, manage→`confidential-view`,
with an unmapped verb falling to the **narrowest** action rather than the widest.

Registration is `AddScoped<IDocumentOwnerResolver, …>`; the resolver takes them all. Onboarding a family
is one registration, never an edit to the resolver.

---

## 6. Company ownership — the check that makes the column meaningful

A document row carries its own `CompanyID`. Checking it against the caller **looks** like tenant
isolation and is not sufficient:

> `PlatformDocument.CompanyID = A`, caller in company A with real authority — but
> `EntityType/EntityId` points at company **B**'s record.

Every company check passes and a foreign personnel file is handed over, while every tenant-isolation
assertion in the suite stays green. So the owning record's company is resolved **independently** and
must agree; disagreement is `relation_mismatch`, refused as malformed rather than resolved either way.

Order: context resolved → employee identity → document company → known family → **owner resolver
registered** → owning company agrees → owning module decides → confidentiality. A family with no
resolver is **denied**, not assumed safe: failing open on unclassified families would be worse than the
hole this replaces.

---

## 7. Confidentiality — vocabulary reused, dependency not

`Internal | Confidential | Restricted | System` — Communication's existing, proven classification.
A second document enum would give one deployment two meanings for "Confidential" and force a
translation table between them.

They are plain platform constants: a document never reaches into `CommThread` internals. Above
`Internal`, the caller must additionally hold the owning module's **manage-tier** authority over the
same record — a second `CanAsync`, not a role name the platform knows.

---

## 8. Storage and the secure-download flow

`IDocumentStorage` + opaque `StorageKey` (32 hex chars — no separator, no drive qualifier, no dot
segment, so traversal is impossible *by construction* rather than by sanitising). Keys are generated,
never accepted from a request; `TryParse` rehydrates only from a database column.

`LocalDocumentStorage` writes under `App_Data/documents`, **outside `wwwroot`** — deliberately. Anything
under the web root is reachable by `UseStaticFiles`, and a static pipeline cannot ask who is calling. A
file stored here has **no URL at all**.

`StorageKey` is capability-sensitive: never publish it in a business event, an audit payload or a public
DTO. Nothing is migrated onto this contract in this batch.

The download endpoint TAB-3 builds should be:

> request → authenticate → resolve `BusinessContext` → load document → verify `CompanyID` → resolve
> owning `EntityType`/`EntityId` → `IDocumentAccessResolver.AuthorizeAsync` → verify confidentiality →
> resolve `StorageKey` → stream.

The browser never receives a physical path, and a client-supplied physical path is never trusted.
Refusals should be `NotFound`, matching the gate's existing reasoning: a `403` confirms the document
exists to someone not allowed to know that.

---

## 9. What TAB-3 must implement, and what is still blocked

**Extension points:**
1. `PlatformDocuments` / `PlatformDocumentVersions` / `PlatformDocumentTypes` schema — with `CompanyID`,
   `EntityType`, `EntityId`, `Confidentiality`, `StorageKey`.
2. The secure download/upload endpoints implementing §8.
3. One `IDocumentOwnerResolver` per family being onboarded.
4. `SupportsFiles` — flip it per family as part of onboarding, never in bulk. **Correction to the
   original text of this ADR, which said the flag was `false` on every entry:** `Task` and
   `CalendarEvent` already carried it when TAB-4 onboarded them for timeline/comments/files. `Employee`
   was enabled once its owner resolver, its `ScopeHr` routing and its action map were all in place.
   Everything else remains `false`.
5. Business events (`Uploaded`/`Replaced`/`Approved`/`Expired`/`Renewed`/`Archived`), versioning,
   metadata and approvals — all TAB-3's, all through the canonical engines.

**Blocked, needing an owner decision:**
- **`/Files/{companyId}` Hierarchical split** (§3) — Organization Administration's call.
- **`JobApplication` has no `EntityRegistry` code.** The tenancy chain is deterministic
  (`ApplicationDocument.ApplicationID → JobApplication.CompanyID`, and `ApplicationDocument`
  deliberately carries no duplicate tenant column), so the resolver is a few lines — but registering a
  new entity code is a registry/domain decision.

**Not blocked, verified clean:** `FileManagerController` binds every action to the caller's company and
`FileManagerPaths` already refuses traversal and rooted stored paths. Its residual — company from
`Employee.EmpCompanyID` rather than `BusinessContext`, and no module permission vocabulary — is product
work, not a P0.
