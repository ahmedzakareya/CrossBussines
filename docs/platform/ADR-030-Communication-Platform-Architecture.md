# ADR-030 — Communication Platform Architecture

**Status:** Accepted (architecture phase)
**Related:** ADR-001 (transactional events), ADR-002 (entity registry), ADR-004 (visibility), ADR-006
(notification projection), ADR-031…036, CPS-001

---

## Context

The product needs one communication layer — comments, threads, mentions, followers, reactions, attachments,
notifications, timeline, audit — usable by **every** business entity. Three things already exist and must not be
duplicated or disturbed:

1. **The Platform Kernel** owns durable business events, an entity registry, a timeline read path and a
   notification projection.
2. **A Comm feature module** (another work stream) owns email (`CommMessages`), announcements, chat and
   `DocComments`.
3. **The permission engine** (`IPlatformPermissionProvider` + module access services) owns authorization.

The instruction for this phase is architecture only: no UI, no production integration, and no modification to
Authorization, Accounting, Inventory, CRM, Bootstrap Policies, Stage 2A, Security or existing production logic.

## Decision

Build a **separate platform layer** in `BL/Communication` + `Models/Communication` +
`Models/Context/Communication`, keyed on a universal entity reference, that **consumes** the kernel and the
permission engine and **replaces neither**.

### §1 — Universal entity reference
Every table and every service is keyed by `CommEntityRef` = (canonical registry code, id). See ADR-031.

### §2 — Authorization is consumed, never replaced
This platform registers no permission provider, no module access service and no role. Entity-level `View` is asked
of the existing provider **first**. See ADR-033.

### §3 — Its own visibility set
`CommVisibility` = Public | Internal | Confidential | Restricted, with a downward mapping onto the kernel's frozen
set. The kernel has no external tier and one may not be added from outside the kernel. See §7 and ADR-033.

### §4 — Threads are the unit
Comments never attach directly to a business record — always to a thread on that record — because participation,
locking, a visibility ceiling and per-thread read state have nowhere else to live. A record may carry several
threads, distinguished by `(Kind, ThreadKey)`, which is how "discussion threads" exist without a second table.

### §5 — One line in `CrossDbContext`
`Communication.CommunicationModel.Configure(builder)` — and **no** `DbSet<>` properties. The types enter the model
there; services use `db.Set<T>()`. Two other work streams edit that file concurrently, so the footprint is one line
and every name, key, length and index lives in a file this work stream owns.

### §6 — One transaction per operation
`CommTransaction` **enrols, never owns** when a caller already has a transaction; it opens one otherwise.
Collaborating services enrol and never save. One `SaveChanges` persists the business row **and** its audit row, so
a comment with no audit row is impossible.

### §7 — Communication events always; kernel forwarding optional and OFF by default
Every mutation ends in one `ICommEventPublisher.PublishAsync`, which **always** appends a durable communication
event to `CommAuditEntries` and **optionally** forwards a translated copy to `IBusinessEventService`.

The audit row is appended **first**, and the order is not negotiable: if the bridge then throws, the whole
transaction rolls back and neither row exists — correct. Bridging first would allow a kernel event with no matching
audit row, i.e. a fact in the shared log this platform cannot explain.

Forwarding is off by default for three reasons, each already paid for in this repository:

- `RecordAsync` has **no swallowing catch**. A missing `BusinessEvents` table is SQL-208 inside the caller's
  transaction. CLAUDE.md records this as a live coupling for the sale/purchase path (HM-D44/D45) and for reversal
  (HM-D53). Wiring it into commenting would extend that blast radius to every screen.
- The kernel's event grammar is `<RegisteredEntityCode>.<Action>`. A communication fact is about a *comment*, which
  is not (and should not be) a registry code, so `Comment.Added` would fail `BusinessEventTypes.Validate`. Hence the
  translation table in `CommEventTypes.KernelActionFor`.
- CLAUDE.md records a standing decision, taken in another track for the same reason: adopting the event platform
  binds work to a queue, dispatcher and consumer set that are uncommitted in git.

Turning it on takes **two** deliberate steps — the `BridgeToBusinessEvents` flag *and*
`services.UseBusinessEventBridge()`. Two switches for one behaviour is the difference between a deployment choosing
to couple its commenting to the kernel's schema and a configuration flag doing it silently.

`KernelActionFor` returns null — *not bridgeable* — for reactions (a signal, not a business fact; thousands a day
into the largest table in the database), participation (a personal preference, not a fact about the record),
permission grants (a security artefact — `EntityRegistry`'s own `PlatformRoleAssignment` comment sets the precedent
that these are read through an audit path, not rendered on a record timeline) and notifications (an outcome of an
event, never itself an event; bridging one would feed back through the notification projection consumer).

### §8 — Attachments are references; this platform stores no bytes
`StorageKey` is an opaque handle into whatever file store the deployment already has. This platform never resolves
it, never streams it, never deletes from the store, and never puts it in an event or audit payload (a `StorageKey`
is capability-bearing). The comment is authorized here; the bytes are authorized by the store.

### §9 — Markdown stored unrendered; HTML refused
Accepting authored HTML means owning a sanitizer forever, and a sanitizer bug in a comment body is stored XSS on
every screen that renders a timeline. Storing *rendered* output is equally rejected: it freezes the sanitizer's
rules into the data, so a bug found next year would be in every historical row. `ICommBodyPolicy` returns a
structural report instead of rendering. Size limits are in **UTF-8 bytes**, because a character cap would silently
halve the real limit for Arabic authors.

### §10 — Everything behind one registration call, uncalled
`AddCommunicationPlatform` wires the whole platform. `Program.cs` does not call it. No hosted service is registered.
The DI graph is nonetheless proven by `CommunicationDiWiringTests` against the real container with
`ValidateOnBuild` + `ValidateScopes`.

### §11 — Four failure types
`CommEntityNotSupportedException` (400), `CommValidationException` (400, machine code), `CommAccessDeniedException`
(403 — the reason is for the **audit row** only, because a denial that explains itself leaks facts about content
the caller cannot read), `CommNotFoundException` (404 — and absent / other-company / soft-deleted answer
identically, so an id cannot be an existence oracle).

## Consequences

**Positive.** Onboarding an entity is configuration. The running application is unchanged until one reviewed line
is added. The platform works with no kernel slice deployed. Authorization cannot be widened by construction.

**Negative / accepted.** A second comment store coexists with `DocComments` until its owner decides (CPS-001 §10).
Delivery needs a worker before email or push can leave the queue. The kernel bridge is not exercised against a live
kernel schema in this phase — `RecordingBusinessEventBridge` asserts the *translation*, not the write.
