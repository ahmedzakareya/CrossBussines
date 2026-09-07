# Stage 2B — Enterprise Platform Framework — Contracts

**Design only. Nothing implemented.** Fulfils P0-C3. Builds on accepted conclusion #7: registry + capability metadata
+ composition + event projections, **never a universal base entity**.

---

## 1. The problem these contracts must tolerate

Any "universal business object" design has to survive what the codebase actually is — **224 DbSets** with
inconsistent conventions:

| Reality | Evidence | Contract consequence |
|---|---|---|
| Company column name differs | `TaskItem.CompanyId` vs `CompanyID` everywhere else | Ownership must be **resolved by the registry**, never by a shared property or interface member |
| Branch column name differs | `BranchId` / `BranchID` | Same |
| Some entities have **no** company | `Hierarchical` (the org tree) | Registry must support `CompanyOwnership.None` and `Derived` |
| Company is sometimes indirect | a `ConversationMember` is scoped by its `Conversation` | `CompanyOwnership.ViaParent` with an explicit resolver |
| Legacy identifiers | int keys, some composite natural keys | Reference type carries a **string** id alongside the int |
| Module-specific lifecycle | reverse-never-delete (accounting) vs soft-delete `DeletedAt` (Comm modules) | Retention/archival is **per-object policy**, not a framework default |

**This is why inheritance is rejected.** A base entity would require a schema change to every table and a data
migration through code that the two-writer rule forbids touching.

---

## 2. Contract 1 — Business Object Registry

**Responsibility:** the single authority mapping an entity type to its frozen code, its ownership semantics, and its
opted-in capabilities. Extends the existing `EntityRegistry` (13 codes today) rather than replacing it.

```
BusinessObjectDescriptor
    EntityCode        string   frozen, e.g. "SalesInvoice"      (already exists)
    ClrType           Type
    Table             string
    KeyResolver       Func<object, BusinessObjectRef>
    CompanyOwnership  Direct | ViaParent | Derived | None
    CompanyResolver   Func<int id, CancellationToken, Task<int?>>     // authoritative
    BranchResolver    Func<int id, CancellationToken, Task<int?>>?    // null = not branch-scoped
    Capabilities      CapabilityFlags
    PermissionScope   string   e.g. EntityRegistry.ScopeAccounting
    PermissionAction  string   the action that means "may view this object"
```

* **Identifiers:** `EntityCode` + int id. Codes are **frozen** — the existing rule; renaming one breaks history.
* **Company/branch:** never read from a property. Always `CompanyResolver`, because the column name is not uniform and
  sometimes the company lives on a parent.
* **Authorization:** `PermissionScope` + `PermissionAction` are what capability reads route through
  `IPlatformPermissionProvider` — so a capability can never be more permissive than the owning module.
* **Read/write:** registry is **read-only at runtime**, built at startup, validated once (duplicate code ⇒ **boot
  failure**, matching the existing `PermissionScopeStartupValidator` precedent).
* **Failure:** an unregistered entity code ⇒ capability **refused**, never defaulted.
* **Caching:** immutable after startup; safe to hold as a singleton. **Must not capture a scoped service** — the
  Stage 1 defect that cost two startup failures.
* **Migration:** additive. Existing 13 codes keep their meaning.
* **Test:** every registered descriptor resolves a real table and a real company for a seeded row; every capability
  claimed is actually implemented.

## 3. Contract 2 — Capability Metadata

```
[Flags] CapabilityFlags = None | Timeline | Comments | Attachments | Tags | Followers |
    Relations | Tasks | Workflow | Approvals | Escalation | Notifications | Communication |
    Search | AiContext | Audit | Retention | PortalExposure | ApiExposure
```

Opt-in **per object**, stored in the descriptor (code) with a per-company **override table** so a tenant can disable a
capability without a deployment. Enabling a capability is an **administrative act** and raises
`BusinessObjectCapability.Changed` — because silently enabling portal exposure is a security event.

**Rule:** a capability may only be enabled if its provider is registered. `Search` without a
`ISearchDocumentProjection` is a configuration error, not a silent no-op — the failure mode Stage 1 met twice
(`EnsureCreated`, skipped tests).

## 4. Contract 3 — Business Object Reference

```
readonly record struct BusinessObjectRef(string EntityCode, int Id, string? NaturalKey = null)
```

The universal address. Every capability row stores this pair, never a raw FK — which is what lets one comments table
serve 224 entity types without a schema change per type. `NaturalKey` accommodates legacy composite keys.

## 5. Contract 4 — Object Relationship

```
IObjectRelationshipService
    LinkAsync(BusinessObjectRef from, BusinessObjectRef to, string relation, ct)
    UnlinkAsync(...)
    Task<IReadOnlyList<RelatedObject>> RelatedAsync(BusinessObjectRef of, ct)   // permission-TRIMMED
```

* Relations are **directional** with a named type (`"invoiced-by"`, `"blocks"`).
* `RelatedAsync` **trims** by asking `IPlatformPermissionProvider` per distinct target *type*, then filtering ids in
  **one** set-based predicate — the `TaskScopeQuery` pattern, **not** per-row `CanAsync`. Stage 1 proved this matters
  (1 evaluation for 500 rows).
* Both ends must be in the **same company** unless an audited bypass is present.
* **Failure:** an unresolvable target is omitted, not shown as a broken link.

## 6. Contracts 5–8 — Timeline, Comments, Attachments, Task Links

**Timeline (projection, not a table on the object).** Fed by `BusinessEvents` through the existing outbox, exactly as
notifications already are. `ITimelineProjection` writes one read-model row per event; the object owns nothing.
Post-commit, at-least-once, **idempotent by `(EventId, Consumer)`** — the existing dispatch rule; a cursor is
forbidden (ADR-003/007).

**Comments.** One `ObjectComments` table keyed by `BusinessObjectRef`. **Consolidates `DocComments` and project
comments.** Write requires the owning module's action; read is trimmed. Soft-delete `DeletedAt` (Comm convention) —
comments are discussion, not ledger, so reverse-never-delete does not apply.

**Attachments — the highest-value item in 2B.** One `ObjectAttachments` table + one storage abstraction, replacing
**three stores** (`FileManager`, HR documents, project files).

* Authorization is on the **parent object**, resolved through the registry. A file inherits its parent's rule; there is
  no independent file permission to get wrong.
* Downloads go through an authorizing endpoint. **Never a guessable static path** — an unauthenticated static file URL
  would defeat every check above it. This is listed as risk R-12.
* Storage is pluggable (disk now, blob later); the contract stores a key, not a path.
* Migration: **coexistence** — new writes to the unified store, old readers keep working, per-module opt-in.

**Task links.** `ITaskLinkProvider` binds `BusinessObjectRef` → `TaskItem` via the **existing**
`EntityType`/`EntityId`, which `TasksAccessService` already gates through `IPlatformPermissionProvider`. No new
mechanism — the linked-entity gate already exists and is tested.

## 7. Contracts 9–11 — Workflow, Approvals, Notifications

**Workflow binding.** `IWorkflowBinding` declares which transitions an object exposes and which require approval.
The engine stores state **outside** the business table (no status column added), so a module can adopt workflow without
a schema change.

**Approval binding.** Steps resolve approvers through **`IOrgHierarchy`** — already company-intersected and
cycle-guarded — and must reuse C.1's **`HierarchyDefect`** contract: a chain broken by a cross-company graft
**refuses**, and **must never auto-approve**. That defect is already understood and paid for; the engine inherits the
answer rather than rediscovering it.

**Escalation.** Time-based, evaluated by **one** hosted worker that binds an explicit company scope
(`WorkerScope.ForCompany`) — an unbound worker reads nothing and "passes" by examining zero rows, the Stage 1 lesson.
Takes `IServiceScopeFactory`, never a scoped service (a hosted service is a singleton).

**Notification binding.** Routes through the **existing** projection path
(event → `BusinessEventNotificationMapper` → `NotificationProjectionConsumer` → `NotifyAsync`). Direct `NotifyAsync`
stays legal for non-onboarded objects, per ADR-006.

## 8. Contracts 12–13 — Search and AI Context

`ISearchDocumentProjection` emits a permission-annotated document per object: `EntityCode`, id, **company**,
**branch**, owner/participant ids, and text. `IAiContextProvider` returns retrieval candidates.

**Non-negotiable for both:** the query is filtered by company **and** by the caller's `AccessScope` **before**
results or context are returned. Post-filtering a result set is not sufficient — a ranked list leaks existence even
when contents are hidden. Neither capability may ship without a trimming test, and Stage 9 is sequenced last for
exactly this reason (Risk R-24/R-25).

## 9. Contracts 14–17 — Portal, API, Retention, Trimming

**Portal exposure:** explicit per object **and** per field; default **deny**; enabling raises an event.
**External principals are out of scope until a portal exists** — `BusinessContext` assumes an employee today, and
pretending otherwise would be designing for an unbuilt requirement.

**API exposure:** explicit opt-in; APIs get the **API-safe guard** (`ApiPerm`) — 401/403 JSON, never an MVC redirect.
289 API actions exist today, so this is a live concern.

**Retention:** per-object policy with a hard rule — **an object whose module forbids deletion (accounting) may only be
archived, never purged**. Reverse-never-delete outranks any retention setting.

**Permission trimming:** one service, `IObjectPermissionTrimmer`, used by relations, search, AI and portals. **One
implementation, four consumers** — because four implementations would drift, which is how the three file stores
happened.

---

## 10. Transactional and failure rules (apply to every contract)

1. Capability writes participate in the caller's ambient transaction where one exists; **capability failure must never
   roll back a financial transaction** — attachment or timeline problems are not ledger problems.
2. `RecordAsync` stays **in-transaction, immediately before commit, no swallowing catch** (ADR-001) — the existing rule.
3. Projections are post-commit, at-least-once, idempotent.
4. Every capability read **fails closed**: unresolved company, unregistered code, or missing provider ⇒ refuse.
5. No capability may widen a module's own decision.

## 11. Test strategy

Per capability: contract test (provider honours the interface) · company semantics (all four ownership modes) ·
**trimming test before exposure** · idempotency for projections · **evaluation-count test proving trimming does not
grow with row count** (the `TaskScopeQuery` proof, generalised) · SQL Server test for anything transactional, with the
`[RequiredEvidence]` trait so it **cannot report skipped** (Stage 1's most expensive test defect).

## 12. Migration strategy

Additive tables only; **no business table altered**. Per-module opt-in — a module joins by registering a descriptor and
flags. Coexistence for comments and attachments: new writes unified, old readers intact, cut over per module with
parity tests. R1 (role consolidation) is sequenced **first** in 2B so capability authorization has one role source from
the start.

## 13. What is deliberately deferred

Tags, followers, retention execution, portal exposure and AI context are **designed here, not built in 2B**. Shipping
22 capabilities at once is the scope risk of this phase; **registry + attachments + approvals + timeline** is the
minimum that unblocks HR, WMS and Manufacturing, and that is the recommended 2B content.
