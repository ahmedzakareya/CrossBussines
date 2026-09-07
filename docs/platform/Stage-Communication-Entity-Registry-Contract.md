# Stage-Communication — Entity Registry Contract

**Platform:** CrossBusiness Communication & Collaboration Platform
**Owner tab:** THIRD TAB (Communication & Collaboration)
**Related:** ADR-030 (architecture), ADR-031 (universal collaboration surface), ADR-002 (entity registry), CPS-001
**Status:** contract defined — **no new entity registered by this document**

---

## 1. What this document is

The exact, complete set of facts a module must supply before any of its records may carry comments, mentions,
attachments, followers or a timeline. It exists so that onboarding Accounting, Inventory, CRM, HR, Projects,
Manufacturing, Tasks or Support is a **checklist**, not a negotiation — and so that the answer to *"why is this
entity not onboarded yet?"* is always a named missing fact rather than an opinion.

**Task and Support are deliberately NOT registered.** §7 states exactly what each must provide first.

---

## 2. The identity: `CommEntityRef`

Every collaboration artifact in this platform is addressed by one pair:

```
CommEntityRef = Registry Entity Code + Entity ID
```

```csharp
public sealed record CommEntityRef(string EntityCode, int EntityId)
{
    public bool IsWellFormed => !string.IsNullOrWhiteSpace(EntityCode) && EntityId > 0;
    public string Key => EntityCode + "#" + EntityId;    // "SalesInvoice#1001"
}
```

Two consequences that are the whole reason the platform is one platform and not eight:

* **There is no per-module comment table.** A comment on an invoice and a comment on a work order are the same
  row shape, distinguished by `EntityCode`.
* **A free-text entity type is impossible.** `EntityCode` is validated against `IEntityRegistry` on every path,
  which is what ADR-002 exists to enforce. An unregistered code fails closed — proven by
  `CommSecurityBoundaryTests.An_unregistered_entity_code_is_refused_by_every_capability`.

---

## 3. The gateway: `ICommEntitySurface`

No module talks to the registry directly. Every write and read path asks one question in one place:

> **May this entity carry this capability?**

The decision is three-way and its ORDER is the contract:

| Order | Signal | Effect | Why it is where it is |
| --- | --- | --- | --- |
| 1 | **Block list** (`BlockedEntityCodes`) | refuses outright | a deployment that finds a leak must pull one entity without a code change or a release |
| 2 | **Registry flag** (`SupportsComments` / `SupportsFiles` / `SupportsFollowers` / `SupportsTimeline`) | authoritative "wired today" | owned by the platform kernel |
| 3 | **Allow list** (`EnabledEntityCodes`) | **additive** on top of the flag | lets a deployment onboard now while the kernel owner flips the flag |

**Existence is never configurable.** An unregistered code is refused at every layer, allow list or not.

`GetOnboardingGap()` publishes every code enabled by configuration whose registry flag is still false — so the
divergence between "enabled here" and "flagged in the kernel" is *reported*, never hidden.

---

## 4. The registration contract

A module supplies **eleven** facts. Six exist in `EntityDefinition` today; five are defined by this platform and
currently live in configuration or are **not yet modelled** — marked accordingly, because pretending they exist
would be the kind of gap this contract is written to prevent.

| # | Fact | Where it lives today | Required | Notes |
| --- | --- | --- | --- | --- |
| 1 | **Entity code** | `EntityDefinition.Code` | ✅ exists | Frozen PascalCase. Persisted in every Comm table. Renaming one orphans history |
| 2 | **Display name** | `DisplayNameAr` + `DisplayNameEn` | ✅ exists | **Both** required — every user-facing string in this product is bilingual |
| 3 | **Company scope** | *implicit* | ⚠️ **contract, not a field** | The owning table MUST carry `CompanyID`, and the module MUST be able to answer "which company owns record N" |
| 4 | **Supported capabilities** | `SupportsComments`, `SupportsFiles`, `SupportsFollowers`, `SupportsTimeline` | ✅ exists | Opt-in per capability. Absent = refused |
| 5 | **Permission target** | `PermissionScope` | ✅ exists | The module scope `IPlatformPermissionProvider` routes to. **This is the gate** |
| 6 | **Timeline eligibility** | `SupportsTimeline` | ✅ exists | See §5 |
| 7 | **Attachment eligibility** | `SupportsFiles` | ✅ exists | See §5 |
| 8 | **Mention eligibility** | *derived from* `SupportsComments` | ⚠️ **derived, not separate** | See §5 — this is a real modelling gap |
| 9 | **Privacy level** | ❌ **not modelled** | ⚠️ gap | See §6.1 |
| 10 | **Retention policy** | ❌ **not modelled** | ⚠️ gap | See §6.2 |
| 11 | **Audit requirements** | partially — `CommAuditEntries` is unconditional | ⚠️ partial | See §6.3 |

### 4.1 The six facts that exist — required shape

```csharp
new EntityDefinition
{
    Code            = "ManufWorkOrder",          // 1. frozen, PascalCase, globally unique
    DisplayNameAr   = "أمر تشغيل",                // 2a. required
    DisplayNameEn   = "Work order",              // 2b. required
    Module          = "Manufacturing",           //     owning functional module
    PermissionScope = EntityRegistry.ScopeManufacturing,  // 5. THE GATE
    RouteTemplate   = "/Manufacturing/WorkOrder/{id}",    //     deep link, or null

    SupportsComments   = true,   // 4/8. comments + mentions
    SupportsFiles      = true,   // 4/7. attachments
    SupportsFollowers  = true,   // 4.   participation
    SupportsTimeline   = true,   // 4/6. aggregated timeline

    Icon = "ki-outline ki-gear", Color = "primary",
}
```

### 4.2 Current registry state — measured, not assumed

Twelve codes are registered. **Three** carry `SupportsComments`:

| Code | Module | `SupportsComments` | Onboarded in Comm config |
| --- | --- | --- | --- |
| `SalesInvoice` | Accounting | ✅ | ✅ |
| `PurchaseInvoice` | Accounting | ✅ | ✅ |
| `Quotation` | Inventory | ✅ | ✅ |
| `JournalEntry` | Accounting | ❌ | ❌ |
| `Customer` | Crm | ❌ | ✅ *(allow-list only → appears in `GetOnboardingGap()`)* |
| `Supplier` | Crm | ❌ | ❌ |
| `ManufWorkOrder` | Manufacturing | ❌ | ❌ |
| `PosOrder` | Pos | ❌ | ✅ *(allow-list only → gap)* |
| `Employee` | Hr | ❌ | ❌ |
| `Project` | Projects | ❌ | ❌ |
| `Item` | Inventory | ❌ | ❌ |
| `PlatformRoleAssignment` | Platform | ❌ | ❌ |

**`Task` and `Support` do not appear because they are not registered at all** — see §7.

---

## 5. Capability eligibility — the three that are not independent

The gate asks for timeline, attachment and mention eligibility as separate facts. Two are separate today; one is
not, and saying so is more useful than implying otherwise.

| Capability | Independent today? | Consequence |
| --- | --- | --- |
| **Timeline** | ✅ `SupportsTimeline` | An entity can have a timeline with no comments (audit-only) |
| **Attachments** | ✅ `SupportsFiles` | An entity can take comments but refuse files — the right default for a record whose documents live elsewhere |
| **Mentions** | ❌ **derived from `SupportsComments`** | A mention is carried *by* a comment, so there is no way to allow comments and forbid mentions |

**Recommendation (not implemented here):** if a deployment ever needs "comments yes, mentions no" — plausible for a
customer-facing record where an @mention would notify staff about a customer conversation — the cheapest correct
change is a **Comm-side option** (`MentionDisabledEntityCodes`), *not* a new registry flag. The registry is kernel
code owned by another work stream; adding a flag there is a cross-team edit, whereas the surface already reads
Comm options and can refuse `CommCapabilities.Mentions` independently. `ICommEntitySurface` already accepts
`Mentions` as a distinct capability string, so the seam exists — only the option is missing.

---

## 6. The three facts that are NOT modelled

These are named in the gate, and none exists as a field today. Each is stated with what it would take, so the
absence is a decision with a price rather than an oversight.

### 6.1 Privacy level

**What exists:** privacy is per-**comment**, not per-**entity**. `CommVisibility` = `Public | Internal |
Confidential | Restricted`, enforced by `CommAccessPolicy` against the caller's
`View` / `ViewConfidential` / `ViewRestricted` actions.

**What is missing:** an entity cannot declare a **ceiling** or a **floor**. Today a user may post a `Public`
comment on any onboarded entity, including one whose underlying record is itself confidential.

**What it would take:** two fields on the Comm-side entity policy (not the registry):

```
MaxVisibility      — the most open tier a comment on this entity may use (e.g. Employee ⇒ Internal)
DefaultVisibility  — the tier a new comment starts at (e.g. SalesInvoice ⇒ Internal, Employee ⇒ Confidential)
```

**Why it matters before HR onboarding:** an `Employee` record is the obvious case — a `Public` comment on a
personnel record is a privacy incident, and nothing currently prevents it. **This is the single most important
gap in this document and it must be closed before `Employee` is onboarded.**

### 6.2 Retention policy

**What exists:** nothing. Comments, revisions, mentions, notifications and audit rows are kept indefinitely.
`CommComments` soft-deletes (`DeletedAt`), `CommCommentRevisions` is append-only, `CommAuditEntries` is
append-only.

**What is missing:** a per-entity retention rule and anything to enforce it.

**What it would take:**

```
RetentionDays          — null = keep indefinitely (today's behaviour for everything)
RetentionAppliesTo     — Comments | Attachments | Notifications | Audit
LegalHold              — an entity whose rows may never be swept regardless of age
```

plus a sweeper. **Deliberately not built:** writing a deleter for a platform with no production data yet is how
retention bugs ship. It is also entangled with §6.3 — an audit row is often the thing a retention policy must
*not* delete.

### 6.3 Audit requirements

**What exists:** `CommAuditEntries` is written **unconditionally** for every state transition, including denials
(`A_denied_thread_creation_writes_an_audit_row`). Every row carries actor, entity, thread, correlation id and a
dedup key.

**What is missing:** per-entity *differentiation*. Every entity is audited identically. There is no way to say
"an `Employee` comment must record the reader as well as the writer" (access auditing), which some HR regimes
require.

**What it would take:**

```
AuditReads     — record who READ a thread, not only who wrote (expensive; off by default)
AuditRetention — see 6.2; audit usually outlives content
```

**Assessment:** unconditional write-auditing is the correct default and is stronger than most modules need. The
read-auditing gap is real but only binds for `Employee` / `Support`, both unregistered.

---

## 7. Task and Support — what they must provide before activation

**Neither is registered, and this document does not register them.** Both are named in the gate as future
entities; here is the exact precondition list.

### 7.1 `Task`

| # | Requirement | State |
| --- | --- | --- |
| 1 | An entity code `Task` in `EntityRegistry` with bilingual display names | ❌ absent |
| 2 | `PermissionScope = ScopeTasks` — the scope constant **already exists** | ⚠️ scope exists, code does not |
| 3 | `TasksAccessService` reachable through `IPlatformPermissionProvider` for `View` | ✅ exists and is registered as `IModuleAccessService` |
| 4 | The owning table carries `CompanyID` | ✅ `TaskItems.CompanyId` |
| 5 | `RouteTemplate` for the task detail screen | ⚠️ verify before registering |
| 6 | Capability flags: `SupportsComments`, `SupportsTimeline`, `SupportsFollowers` — `SupportsFiles` optional | ❌ to be decided by the Tasks owner |
| 7 | **Decision:** does a task comment differ from a task *description update*? | ❌ **open — see below** |
| 8 | Default visibility tier (§6.1) | ❌ blocked on 6.1 |

**Requirement 7 is the real blocker, and it is a product question, not a technical one.** Task management already
has its own activity/history concept. Onboarding `Task` to this platform without deciding which of the two owns
"what happened to this task" produces two timelines on one screen — the exact duplication ADR-031 exists to
prevent. **The Tasks owner must answer before registration**, and the honest options are: (a) Comm owns the
timeline and Tasks stops writing its own, (b) Tasks keeps its history and registers as
`SupportsComments` **only**, or (c) Tasks history becomes an `ICommTimelineSource` and merges.

*Note: the gate for this tab forbids implementing Task Management. Registering the entity code is not implementing
it — but requirement 7 cannot be answered by this tab either.*

### 7.2 `Support`

| # | Requirement | State |
| --- | --- | --- |
| 1 | An entity code `Support` (or `SupportTicket`) in `EntityRegistry` | ❌ absent |
| 2 | **A permission scope** — there is no `ScopeSupport` constant | ❌ **absent; must be added by the kernel owner** |
| 3 | A module access service answering `View` for a ticket | ❌ absent |
| 4 | The owning table carries `CompanyID` | ⚠️ verify — ticket tables predate the platform |
| 5 | **Decision:** are ticket comments *customer-visible*? | ❌ **open — the critical one** |
| 6 | Privacy ceiling/floor (§6.1) | ❌ blocked on 6.1 |
| 7 | Retention policy (§6.2) | ❌ likely mandatory for support data |

**Requirement 5 is a hard blocker.** Support is the first entity where a comment may be read by someone who is
**not an employee**. This platform's entire access model resolves an `EmployeeId` through
`IPlatformPermissionProvider`; a customer is not an employee and has no `BusinessContext`. Onboarding `Support`
with customer-visible comments is therefore **not a configuration change** — it needs an external-principal model
that does not exist today.

**Recommendation:** if Support is onboarded, do it **internal-only first** (`MaxVisibility = Internal`), and treat
customer-visible replies as a separate, later capability with its own ADR. `@Customer` / `@Supplier` mentions carry
the same constraint — see the roadmap, where they are gated on explicit access rules for exactly this reason.

---

## 8. The onboarding checklist

For any module, in order. Steps 1–3 are the kernel owner's; 4–7 are the module's; 8 is this platform's.

1. Add the entity code + bilingual names + `Module` + `RouteTemplate` to `EntityRegistry`.
2. Set `PermissionScope` to the module's scope constant (adding the constant if absent — Support).
3. Set the capability flags the module intends to support.
4. Confirm the owning table carries `CompanyID` and that record→company is answerable.
5. Confirm `IPlatformPermissionProvider` returns a real `View` decision for that scope.
6. Decide the default visibility tier — **blocked on §6.1 for any sensitive entity**.
7. Decide the retention policy — **blocked on §6.2**, or accept "indefinite" explicitly.
8. Add the code to `EnabledEntityCodes`; confirm `GetOnboardingGap()` is empty for it.

**A module is onboarded when `GetOnboardingGap()` no longer lists it** — i.e. configuration and the registry flag
agree. Until then it works, and the divergence is published.

---

## 9. What this contract deliberately does not allow

| Not allowed | Why |
| --- | --- |
| A free-text entity type | ADR-002 — three disagreeing vocabularies existed before the registry |
| Onboarding by editing a database row | The permission target would become editable with an `UPDATE` |
| A capability enabled without a permission scope | The gate would have nothing to ask |
| A Comm permission that grants entity access | The invariant this whole platform is arranged around |
| Registering `Task` or `Support` today | §7 — each has an unanswered blocking decision |
