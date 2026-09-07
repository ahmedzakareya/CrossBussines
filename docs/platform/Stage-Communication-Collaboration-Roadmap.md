# Stage-Communication — Collaboration Roadmap

**Platform:** CrossBusiness Communication & Collaboration Platform
**Owner tab:** THIRD TAB (Communication & Collaboration)
**Related:** ADR-030…036, CPS-001, Stage-Communication-Entity-Registry-Contract
**Status:** roadmap — **nothing in this document is implemented**

---

## 1. Scope of this document

The delivered platform covers conversation, comments, mentions, attachments, participation, notifications,
timeline and audit. This document extends the roadmap **beyond** that, for fifteen named capabilities.

It is a design document. No code, schema or registration follows from it until each item is separately approved.

### 1.1 Explicitly out of scope — and why the boundary is where it is

| Excluded | Reason it is excluded, not merely deferred |
| --- | --- |
| Live chat | A different product with a different persistence model (ephemeral, unread-per-device, presence). `ChatService` already exists separately |
| Typing indicators | Requires a live transport; meaningless without one |
| Presence | Requires a live transport plus a session heartbeat; also a privacy decision (who may see that you are online) |
| SignalR | The transport those three imply. Adding it makes this platform stateful and couples it to hosting topology |
| Voice / video | An entirely separate media stack |
| Task Management | A module, not a collaboration capability. See registry contract §7.1 |
| Calendar | Same — a module. Item 12 below stops at *suggesting* an event and hands off |

**The line:** this platform is **asynchronous, durable and record-anchored**. Every excluded item is
**synchronous, ephemeral or presence-bearing**. That is one coherent boundary, not seven separate refusals — and
it is why item 12 (calendar suggestion) is in scope while Calendar itself is not: producing a *suggestion* is
asynchronous and anchored to a comment; owning an event is not.

---

## 2. Sequencing

Ordered by **dependency**, not by desirability. Each wave is releasable on its own.

```
WAVE 1 — content fidelity        2 · 3 · 4 · 1
WAVE 2 — reading & attention     5 · 14 · 15
WAVE 3 — finding things          6 · 7
WAVE 4 — reach (needs access rules)   13 · 12 · 11
WAVE 5 — assistive (needs 1-3)   8 · 9 · 10
```

Rationale for the ordering, in one line each:

* AI summarisation (8/9) over unsanitised, unversioned text summarises the wrong thing → wave 5 depends on wave 1.
* Notification bundling (14) must exist before read receipts (5) generate more signal, or the noise arrives first.
* Advanced search (7) over rich text needs the sanitised representation from item 2.
* `@Role` / `@Customer` (11/13) are blocked on membership and external-principal work respectively — not on effort.

---

## 3. Wave 1 — content fidelity

### 3.1 Sanitized rich text *(item 2)*

**Today:** `CommBodyPolicy` enforces size, control characters and a `BodyFormat` of `PlainText` or `Markdown`.
Markdown is stored but never rendered by the platform.

**Design.** Store **two** columns: the author's source (`Body`, unchanged) and a sanitised render
(`BodySanitized`), produced once on write by a whitelist sanitiser.

* **Whitelist, never blacklist.** Permit `p, br, strong, em, u, s, ul, ol, li, code, pre, blockquote, a[href], h3, h4`.
  Everything else is stripped. A blacklist is a list of the attacks someone thought of.
* **`a[href]`** limited to `http`, `https`, `mailto` and internal `/` paths — `javascript:` and `data:` are the
  payloads that make rich text an XSS vector.
* **Sanitise on WRITE, store the result.** Sanitising on read means every render re-runs the sanitiser and one
  missed call site becomes a live vector. Storing the source too keeps edit history honest.
* Existing `PlainText` rows are unaffected; `BodySanitized` is null and readers fall back to `Body`.

**Schema:** additive — `CommComments.BodySanitized NVARCHAR(MAX) NULL`, `SanitizerVersion INT NULL`.
`SanitizerVersion` exists so a future sanitiser upgrade can re-render old rows without guessing which need it.

**Risk:** a sanitiser is a security boundary. It gets the same structural-test treatment as §Security in
`CommSecurityBoundaryTests`, plus a corpus of known payloads.

### 3.2 Edit history *(item 3)*

**Today: already delivered.** `CommCommentRevisions` is append-only and every edit writes a revision (ADR-035).

**What remains** is presentation, not storage: a diff view, and a decision about whether a *reader* may see prior
revisions or only a moderator. **Recommendation:** revisions visible to anyone who can read the comment — a
hidden edit history on a record-anchored comment is worse than none, because it implies immutability that is not
there.

### 3.3 Attachment versioning *(item 4)*

**Today:** `CommCommentAttachments` holds one row per file, addressed by `StorageKey`.

**Design.** A version chain, not a new table: `RootAttachmentId` (self-referencing, null = original),
`VersionNo`, `SupersededAt`. "Upload a new version" appends a row pointing at the same root; the timeline shows
one entry with a version badge.

**Deliberately not a blob store change.** `StorageKey` is already opaque and content-addressable by convention;
versioning is a metadata concern. Coupling it to a storage migration would make a small feature a large one.

**Open question for the owner:** does deleting a comment delete every version, or only the current one? Current
soft-delete semantics say the comment hides and attachments hide with it — versioning does not change that, but
it should be stated before build.

### 3.4 Pinned messages *(item 1)*

**Design.** `CommComments.PinnedAt`, `PinnedByEmployeeId`, plus a per-thread ordering column. Pinning requires
**Moderate**, not Comment — pinning is a curation act, and letting any commenter pin makes the pin worthless.

**Constraint:** at most N pins per thread (default 3, configurable). Unbounded pinning is unpinned.

**Why it is last in wave 1** despite being the simplest: it is the only one with no dependency, so it fills
whatever capacity the other three leave.

---

## 4. Wave 2 — reading and attention

### 4.1 Read receipts *(item 5)*

**Today:** `CommReadReceipts` exists and tracks per-thread read state.

**What remains:** per-**comment** granularity, and the product decision that governs it.

**The decision is not technical.** A read receipt tells A that B read something. In an internal ERP that is often
welcome ("did finance see my note?") and occasionally hostile ("why haven't you replied, I can see you read it").

**Recommendation:** per-thread receipts visible to **moderators only**, plus an aggregate ("3 of 5 participants
have read this") visible to all. That answers the legitimate question without creating the surveillance one.
Per-comment, per-person receipts should require an explicit deployment opt-in.

### 4.2 Notification bundling *(item 14)*

**Today:** `CommPreferenceResolver` supports `Immediate` and per-category preferences; `CommNotifications` carries
a `DedupKey` which is an unread-noise guard, not batching.

**Design.** A digest window per (employee, category): instead of N notifications, one notification referencing N
events, emitted when the window closes.

* Window per **category**, not global — a mention should not wait behind a low-priority follow.
* The digest is **built at send time from live rows**, not accumulated in a buffer, so a process restart loses
  nothing and a comment deleted inside the window drops out of the digest.
* `Immediate` remains the default for `Mention` — being named is the one thing that should not wait.

**Dependency:** must land **before** read receipts, or the read-receipt signal multiplies a notification volume
that has no bundling yet.

### 4.3 Quiet hours and preferences *(item 15)*

**Design.** Per-employee: quiet window (local time), days, and a per-category override. During quiet hours,
notifications are **deferred**, never dropped — a dropped notification is a lost message and this platform's
audit trail would then disagree with what the user saw.

* Timezone is **per employee**, not per server. The same mistake the reporting scheduler had to avoid.
* An `Urgent` category bypasses quiet hours; which categories are urgent is deployment configuration, not code.
* Interacts with bundling: quiet hours are naturally a digest window, so 14 and 15 share one mechanism.

---

## 5. Wave 3 — finding things

### 5.1 Conversation collections *(item 6)*

**Design.** A user-defined grouping of threads across entities — "everything about the Q3 audit" spanning
invoices, work orders and projects.

* A collection is a **saved set**, not a folder: a thread may sit in several, and removing it from a collection
  does not delete it.
* Membership is explicit (add this thread) **or** a saved query (every thread on entity code X tagged Y) —
  the second is much more useful and much more expensive; ship explicit first.
* **Company-scoped and permission-filtered on read**, exactly like every other list: a collection must never
  become a way to see a thread the caller could not otherwise open.

### 5.2 Advanced search *(item 7)*

**Design.** Full-text across comment bodies, scoped by company and filtered by the same access policy as the
timeline.

* **Search must filter by permission AFTER matching, never before.** Filtering the index by permission at write
  time bakes a stale decision into the index; a permission revoked later would not take effect.
* Requires the sanitised representation (item 2) so the index holds text, not markup.
* SQL Server full-text is the obvious first implementation — no new infrastructure, and it honours the existing
  `CompanyID` filter naturally.

**Warning worth recording:** search is the classic place a well-built access model leaks, because result *counts*
and *snippets* are computed before filtering. The count must be post-filter even though that is slower.

---

## 6. Wave 4 — reach

These three are grouped because each extends **who** a message can reach, and each is blocked on an access
question rather than on implementation effort.

### 6.1 Convert comment to task *(item 10 — listed here, built in wave 5)*

Deliberately **hands off rather than owns**: this platform produces a *task creation request* carrying the source
`CommEntityRef`, the comment id and the suggested assignee; Task Management creates the task and links back.

**This tab may not implement Task Management**, so the deliverable here is the **contract**, not the action:

```
CommTaskSuggestion { SourceEntity, SourceCommentId, SuggestedAssigneeEmployeeId, Title, Body, DueHint }
```

The link back is an ordinary `CommEntityRef` to the created `Task` — which requires `Task` to be registered
(registry contract §7.1). **Blocked on that, not on this.**

### 6.2 Calendar suggestion *(item 12)*

Same shape and the same boundary: produce a `CommCalendarSuggestion` from a comment (detected date, participants,
subject). **This platform never creates an event.** Calendar owns creation; the suggestion is a durable,
record-anchored artifact, which is why it is in scope while Calendar is not.

### 6.3 `@Role` after reverse-membership support *(item 11)*

**Today:** `EnableRoleMentions = false`, and `Role` is declared in `CommMentionTargetKind` but **not wired** —
deliberately. `CommPrincipalResolver` has employee, team and department sources; there is no role source.

**Why it is blocked:** resolving `@Role` means answering *"who holds role R in company C"* — **reverse membership**.
The platform's role directory answers the forward question ("what does this employee hold"). The reverse query
does not exist, and inventing it inside Communication would put a second role-resolution path next to
`IPlatformRoleDirectory` — precisely the duplication ADR-026 forbids.

**Precondition:** `IPlatformRoleDirectory` (or its owner) exposes a reverse lookup. Then `@Role` is one
`ICommPrincipalSource`, and `MaxGroupMentionRecipients` already caps the blast radius.

### 6.4 `@Customer` and `@Supplier` — only with explicit access rules *(item 13)*

**The hardest item in this document, and the one most likely to be under-estimated.**

A customer is **not an employee**. This platform's entire access model resolves an `EmployeeId` through
`IPlatformPermissionProvider` against a `BusinessContext`. A customer has neither.

Mentioning a customer therefore means one of three things, and they are wildly different in cost:

| Interpretation | Cost | Risk |
| --- | --- | --- |
| **(a) A reference, not a notification** — renders as a link, notifies nobody | Low | Low. Honest and useful |
| **(b) Notifies the internal account owner** | Medium | Low — the recipient is still an employee |
| **(c) Notifies the customer** | **High** | **High** — needs an external-principal model, an external identity, and a rule for what a customer may read |

**Recommendation:** ship **(a)**, then **(b)**. **(c) requires its own ADR** and must not be reached by extending
a mention. The gate's own wording — *"only with explicit access rules"* — is exactly right, and this document
records that no such rules exist yet.

---

## 7. Wave 5 — assistive

All three depend on wave 1: summarising unsanitised, unversioned text summarises the wrong artifact.

### 7.1 AI thread summaries *(item 8)*

**Design.** On demand, not automatic. A summary is generated for a thread the caller **can already read**, from
the comments **they** can read, and is cached against `(ThreadId, LastCommentId, VisibilityTier)`.

**The access rule is the whole design.** A summary generated from all comments and shown to a caller who may read
only some is a data leak wearing a convenience feature — and it would be invisible, because the leaked content is
paraphrased. **The summary must be generated per visibility tier**, which is why the tier is in the cache key.

**Also:** a summary is **not** a comment. It is never stored in `CommComments`, never appears in the timeline as
authored content, and is always labelled as generated.

### 7.2 AI action-item extraction *(item 9)*

Same access rule. Extracted items are **suggestions**, never commitments: nothing is assigned, no task is created,
no notification is sent. An extracted item feeds item 10's suggestion contract, where a human confirms.

**Deliberate constraint:** extraction must be re-runnable and idempotent per `(ThreadId, LastCommentId)`, or two
runs produce two sets of near-duplicate action items — the failure mode every such feature ships with first.

---

## 8. Cross-cutting rules any of these must honour

Non-negotiable, and each already holds in the delivered platform:

1. **A communication permission never widens business-record access.** Every item above reads through
   `ICommAccessPolicy`; none introduces its own gate.
2. **Fail closed.** Unknown entity, unknown capability, unresolved company ⇒ refuse.
3. **No `CompanyID` fallback.** Every new table carries `CompanyID`; every new query filters on it.
4. **No Session/HttpContext** in any service — identity arrives as a resolved `BusinessContext`.
5. **Additive idempotent SQL**, no EF migrations, no backfill.
6. **Bilingual user-facing strings.**
7. **No hosted worker without an ADR** — bundling (14) and quiet hours (15) both imply a scheduler, and that is a
   platform-level decision under ADR-013, not a Communication one. **This is the largest hidden dependency in
   this roadmap and is called out here so it is not discovered during wave 2.**

---

## 9. What would change the sequencing

* **`Employee` onboarding is requested** → the privacy ceiling (registry contract §6.1) jumps ahead of everything.
* **Support onboarding is requested** → §6.4(c) external principals becomes the critical path, not a wave-4 item.
* **A regulator asks for retention** → registry contract §6.2 jumps to wave 1, and it needs the audit interaction
  resolved first.
* **A hosted worker is approved for another reason** → wave 2 becomes materially cheaper.
