# CPS-001 — Communication Platform Specification

**Product:** CrossBusiness Platform
**Layer:** Communication Platform (the unified communication layer across the whole product)
**Phase:** Architecture only — no UI, no production integration
**Status:** Delivered. 250 unit tests green. `AddCommunicationPlatform` is **not called** from `Program.cs`.

---

## 1. What this phase delivers, and what it deliberately does not

| Delivered | Not delivered (out of scope by instruction) |
|---|---|
| Architecture, interfaces, DTOs, domain models, events, extension points | Any UI, view, partial, controller or endpoint |
| 14 tables + idempotent deployment SQL | Any call site in `Program.cs` |
| 24 services behind interfaces, all replaceable | Any hosted service / background worker |
| 250 unit tests, including a real DI-graph test | Any change to Authorization, Accounting, Inventory, CRM, Bootstrap Policies, Stage 2A, Security or existing production logic |
| 7 ADRs + this specification | Any migration of the existing `DocComments` feature |

**The single most important property of this delivery:** with `AddCommunicationPlatform` uncalled, the running
application is byte-for-byte unchanged. No service resolves, no request path differs, no worker starts. Switching
the platform on is one reviewed line in `Program.cs`, at a time chosen by whoever owns production wiring.

### 1.1 Footprint on files this work stream does not own

Exactly **one line**, in `Models/Context/CrossDbContext.cs`:

```csharp
Communication.CommunicationModel.Configure(builder);
```

Fourteen `DbSet<>` properties were deliberately **not** added. The entity types enter the model through that one
call, which is all EF needs; services reach them via `db.Set<T>()` (see `BL/Communication/CommDb.cs`). Two other
work streams are editing `CrossDbContext` concurrently, so a merge conflict here is a one-line conflict rather
than a fourteen-property one. Everything else — every table name, key, length, index — lives in
`Models/Context/Communication/CommunicationModel.cs`, which this work stream owns outright.

---

## 2. The architecture in one page

The requirement is: *every business entity must support Timeline, Comments, Mentions, Attachments and Activity
History without duplicating code.* That is achieved with one idea and one gate.

**The idea — a universal entity reference.** `CommEntityRef` = (canonical `IEntityRegistry` code, record id).
Every table in this platform is keyed by it, and every service takes it. There is therefore no per-module comment
service, no per-module timeline, and no per-module notification producer.

**The gate — `ICommEntitySurface`.** One place answers *may this entity carry this capability?* Onboarding
Accounting, Inventory, CRM, HR, Tasks, Projects, Manufacturing and Support comments is **configuration plus a
registry code**, not code.

```
                        ┌───────────────────────────────────────────────┐
   any business record  │  CommEntityRef  ("SalesInvoice#1001")         │
                        └───────────────────────┬───────────────────────┘
                                                │
                        ┌───────────────────────▼───────────────────────┐
                        │  ICommEntitySurface   — may it? (ADR-031)     │
                        └───────────────────────┬───────────────────────┘
                                                │
   ┌────────────────────────────────────────────┼────────────────────────────────────────────┐
   │                                            │                                            │
┌──▼───────────────┐  ┌──────────────────┐  ┌───▼──────────────┐  ┌──────────────────┐  ┌───▼──────────────┐
│ Threads          │  │ Comments         │  │ Mentions         │  │ Participation    │  │ Reactions        │
│ + permissions    │  │ + revisions      │  │ + recipients     │  │ followers /      │  │ + attachments    │
│ (23, 24)         │  │ (1, 8, 9, 10,    │  │ (3, 13, 15)      │  │ watchers (4, 5)  │  │ (6, 7, 11)       │
│                  │  │  28, 29, 30)     │  │                  │  │                  │  │                  │
└──┬───────────────┘  └────────┬─────────┘  └────────┬─────────┘  └────────┬─────────┘  └────────┬─────────┘
   │                           │                     │                     │                     │
   └───────────────────────────┴──────────┬──────────┴─────────────────────┴─────────────────────┘
                                          │  every mutation ends in ONE call
                        ┌─────────────────▼─────────────────┐
                        │  ICommEventPublisher (26)          │
                        │   ALWAYS → CommAuditEntries (31)   │
                        │   OPTIONAL → kernel BusinessEvents │  ← OFF by default (27, ADR-030 §7)
                        └─────────────────┬─────────────────┘
                                          │
        ┌─────────────────────────────────┴──────────────────────────────────┐
        │                                                                    │
┌───────▼─────────────────────────┐                      ┌──────────────────▼──────────────────┐
│ ICommTimelineAggregator (12,32, │                      │ ICommNotificationService (13,16-22) │
│ 35)  merges N sources:          │                      │  DECIDE → RESOLVE → RENDER          │
│  • kernel events (via the       │                      │  then ICommNotificationDispatcher   │
│    kernel's OWN service)        │                      │  DELIVER per channel, retryable     │
│  • comments  • mentions         │                      │  InApp | Email | Push | WhatsApp    │
│  • comm audit                   │                      │                                     │
└─────────────────────────────────┘                      └─────────────────────────────────────┘
```

### 2.1 Authorization: consumed, never replaced

This platform **registers no permission provider, no module access service and no role.** `CommAccessPolicy`
*consumes* the existing `IPlatformPermissionProvider`. The invariant that follows is structural, not aspirational:

> **A communication permission can never widen access to a business record.**

Entity-level `View` is asked of the existing provider first. Only then does thread state (participation, grants,
visibility) decide whether the caller may read or write *this conversation*. A thread grant can open a restricted
note to a colleague who can already open the invoice; it can never open the invoice. Proven by
`CommAccessPolicyTests.A_thread_grant_cannot_substitute_for_entity_view`.

### 2.2 One transaction, six tables

A single `AddAsync` writes `CommThreads` (counters), `CommComments`, `CommMentions`, `CommMentionRecipients`,
`CommNotifications` + `CommNotificationDeliveries`, and `CommAuditEntries`. They share one `CommTransaction`,
because a comment with no audit row — or mentions with no comment — is worse than a failed post: it is a lie in
an append-only log. The collaborating services **enrol and never save**; only `CommitAsync` persists.

`CommTransaction` **enrols, never owns** when a caller already has a transaction open — the same rule ADR-001
states for the kernel's `RecordAsync`, generalised, because this platform is sometimes the outermost caller.

---

## 3. Frozen vocabularies

Every vocabulary is a `static class` of `const string` with an `IsValid` gate, and every one is mirrored by a
`CHECK` constraint in `deploy/sql/communication_platform_slice_001.sql`. `CommunicationSchemaParityTests` asserts
the mirroring. This is the ADR-002 lesson applied pre-emptively: before the kernel there were three disagreeing
entity-type vocabularies in this codebase, and nothing could join them.

| Vocabulary | Values | Note |
|---|---|---|
| `CommVisibility` | Public, Internal, Confidential, Restricted | Its own set — the kernel's is frozen and has no external tier. Maps **downward** onto the kernel's. |
| `CommThreadKind` | Discussion, Notes, Review | Internal and public notes are **not** separate kinds — see §4.2. |
| `CommBodyFormat` | Markdown, PlainText | **HTML absent by design** — see §4.3. |
| `CommParticipantRole` | Owner, Participant, Follower, Watcher | Follower ≠ Watcher — see §4.4. |
| `CommParticipationSource` | Author, Mention, Explicit, Auto | |
| `CommMentionTargetKind` | Employee, Team, Department, **Role** | Role is *declared-not-wired* — §9 gap G1. |
| `CommPermissionLevel` | Read, Comment, Moderate | |
| `CommPrincipalKind` | Employee, Team, Department, Role | Shares values with mention kinds on purpose. |
| `CommReactionKeys` | like, celebrate, insightful, thanks, question, concern | Frozen keys, **not** free-text emoji — §4.5. |
| `CommChannel` | InApp, **Email**, **Push**, **WhatsApp** | Only InApp has an adapter — §9 gaps G2–G4. |
| `CommDeliveryStatus` | Pending, Claimed, Sent, Failed, Skipped | Mirrors `BusinessEventDispatchStatus` + Skipped. |
| `CommPreferenceMode` | Immediate, Digest, Off | Digest is resolved honestly — §9 gap G5. |
| `CommPreviewKind` | Image, Pdf, Text, Office, Archive, None | Classification only, no rendering. |
| `CommNotificationCategories` | Mentions, Comments, Reactions, Participation, Moderation | Deliberately coarse — §4.6. |
| `CommAuditActions` | 23 values | **No `CHECK` constraint** — deliberate exception, §9 gap G7. |
| `CommEventTypes` | 17 values | `<Aggregate>.<Action>`, distinct from the kernel's grammar. |

---

## 4. Decisions worth defending

### 4.1 Communication owns its own visibility set
The kernel's is frozen at Internal | Confidential | Restricted | System (ADR-004) and has **no tier for "a
customer may read this"** — it never needed one, because a business event is always staff-facing. Communication
does need one: an internal note and a public note are the *same feature* with different audiences, and modelling
them as two tables would duplicate every comment behaviour (revisions, mentions, attachments, reactions) twice.
Adding `Public` to the kernel's frozen set from outside the kernel is the kind of cross-team edit CLAUDE.md
forbids, so `CommVisibility.ToBusinessEventVisibility` maps `Public → Internal` when bridging. That is a
**narrowing**, which is the safe direction.

### 4.2 Internal and public notes are one table
Separated by `Visibility`, not by `Kind` or by table. See 4.1.

### 4.3 HTML is refused; Markdown is stored unrendered
Accepting authored HTML means owning a sanitizer forever, and a sanitizer bug in a comment body is stored XSS on
every screen that renders a timeline. Markdown is a superset of everything the requirement asks for (rich text,
images, links, code blocks, tables) and is safe to *store*. The platform also never stores **rendered** output:
storing HTML freezes the sanitizer's rules into the data, so a bug found next year would be in every historical
row, unfixable without rewriting content. Storing the source makes a rendering fix a deploy.

`ICommBodyPolicy` instead returns a **structural report** (`CommBodyAnalysisDto`: link/image/code-block/table
counts, heading/list/quote flags, and the external hosts referenced) computed once at write time and stored. The
host list exists because a markdown image with a remote `src` fires a GET — carrying a referrer — the moment
anybody opens the record; a deployment that wants to allow-list hosts must first be able to see which appear.

### 4.4 Follower ≠ Watcher, and the distinction is load-bearing
*Follower* asked to be told → notified per activity. *Watcher* asked to keep an eye → **no** per-activity
notification. Without the split, auto-follow is unusable: auto-following a record's owner sounds helpful until a
busy invoice generates forty notifications for somebody who never opted in, at which point the whole feature gets
switched off. Automatic participation lands at Watcher; only an explicit act (Follow, being mentioned, commenting)
reaches Follower. One method decides it: `CommParticipantRole.NotifiedPerActivity`.

### 4.5 Reactions are frozen keys, not emoji
Free-text emoji looks generous and costs a reporting column: *"how many people flagged a concern on this
invoice"* is answerable over six keys and unanswerable over an open unicode range. A UI renders whatever glyph
it likes per key.

### 4.6 A mention is not a grant
Mentioning somebody in a Confidential note does **not** let them read it. `CommNotificationService` evaluates
each recipient's own visibility tier and drops those who may not read the subject — the mention row still exists,
so the audit records that the author tried. Without this, `@`-typing would be a privilege-escalation primitive.
Proven by `A_mention_in_a_confidential_note_does_not_notify_someone_who_cannot_read_it`.

### 4.7 The timeline never reads `BusinessEvents`
The kernel already owns one timeline and applies four filters (company, branch, module permission, event
visibility). Reading `BusinessEvents` directly here would duplicate all four, and the copy would drift: the day
the kernel adds a fifth filter, this platform would keep showing what the kernel had decided to hide. So the
kernel is **one source among several**, reached through `ITimelineProjectionService`, and its filters keep
applying. There is also **no projection table** — a timeline is a read over facts that already exist;
materialising rows would be a second copy of the same truth, free to disagree with it.

A broken contributor is **reported and skipped**, never allowed to blank a record's history — one failing source
producing an empty timeline looks exactly like "this record has no history".

### 4.8 The audit trail is this platform's own table
Not `BusinessEvents`, for three reasons: **availability** (a kernel slice may not be deployed, and `RecordAsync`
throws), **completeness** (the kernel's log is filtered on read by visibility and permission — correct for a
timeline, wrong for an audit trail: an auditor asking *who deleted the note* must get an answer even when the
note was Restricted), and **granularity** (reactions, participation and permission grants are deliberately not
bridged, but they *are* audited — audit is the superset).

`CommAuditEntries` is the one **append-only** table: no `DeletedAt`, no `UpdatedAt`, no mutating service method,
no FK to anything (an audit row must outlive everything it describes). `CommAuditWriter` **enforces** that a
detail payload contains no `body` and no `storageKey` — an audit row is read by more people than the content is,
and a capability-bearing file handle in one is a quiet privilege escalation.

### 4.9 Delivery is a callable drain, not a worker
A hosted service is a singleton that may never inject a scoped service, must take `IServiceScopeFactory`, and
must bind an explicit company scope — three rules that each cost a real defect in this repository. This phase
ships none. `ICommNotificationDispatcher.DispatchPendingAsync` has the worker-facing shape already correct, so
wrapping it is a small reviewable step for the phase that owns production wiring.

The claim rule is taken verbatim from ADR-003/ADR-007: **claim by `Status` only — never a cursor, never
`MAX(Id)`, never `Id > lastSeen`.** Ids are assigned at INSERT and become visible at COMMIT, so a cursor skips a
late-committing lower id permanently.

---

## 5. Requirement traceability — all 43 modules

| # | Module | Where | Status |
|---|---|---|---|
| 1 | Comments Engine | `ICommCommentService`, `CommComments` | Complete |
| 2 | Discussion Threads | `ICommThreadService`; multiple threads per record via `(Kind, ThreadKey)` | Complete |
| 3 | @Mentions | `ICommMentionService`, `ICommBodyPolicy` grammar, `CommMentions` | Complete for Employee/Team/Department; Role = gap G1 |
| 4 | Followers | `ICommParticipationService`, role `Follower` | Complete |
| 5 | Watchers | same table, role `Watcher`, excluded from per-activity notification | Complete |
| 6 | Reactions | `ICommReactionService`, `CommReactions`, frozen keys | Complete |
| 7 | Attachments | `ICommAttachmentService`, `CommCommentAttachments`, by reference only | Complete |
| 8 | Internal Notes | `CommThreadKind.Notes` + `CommVisibility.Internal` | Complete |
| 9 | Public Notes | same, `CommVisibility.Public` | Complete |
| 10 | Rich Text Comments | `CommBodyFormat.Markdown` + `CommBodyAnalysisDto` | Complete (storage/analysis; rendering is UI) |
| 11 | File Preview | `ICommFilePreviewProvider`, `DefaultCommFilePreviewProvider` | Contract + classifier; thumbnail generation = gap G6 |
| 12 | Activity Timeline | `ICommTimelineAggregator` | Complete |
| 13 | Mention Notifications | `CommTemplateKeys.MentionedInComment` via `ICommNotificationService` | Complete to queue; delivery per channel |
| 14 | Read Status | `ICommReadStatusService`, `CommReadReceipts` watermark | Complete |
| 15 | Mention History | `ICommMentionService.GetHistoryAsync`, `CommMentionRecipients` | Complete |
| 16 | Notification Templates | `ICommTemplateCatalog` (7 templates), `ICommTemplateRenderer` | Complete; resx keys published, not yet added (G8) |
| 17 | Notification Channels | `ICommNotificationChannel`, `CommChannel` | Contract complete; adapters G2–G4 |
| 18 | Email Notifications | `CommChannel.Email` declared; `UnwiredCommNotificationChannel.Email()` | **Declared-not-wired — G2** |
| 19 | In-App Notifications | `InAppCommNotificationChannel` + own inbox read model | Complete |
| 20 | Future WhatsApp Integration | `CommChannel.WhatsApp` declared | **Declared-not-wired — G4 (as required: "future")** |
| 21 | Push Notification Contracts | `CommPushPayload`, `CommPushToken`, `ICommPushTokenStore` | **Contracts only, as required — G3** |
| 22 | Notification Preferences | `ICommPreferenceResolver`, `CommNotificationPreferences` | Complete; Digest partial — G5 |
| 23 | Conversation Permissions | `ICommAccessPolicy` + `CommThreadPermissions` | Complete |
| 24 | Thread Permissions | same | Complete |
| 25 | Communication DTOs | `Models/Communication/CommContracts.cs` etc. | Complete |
| 26 | Communication Events | `CommEvent`, `CommEventTypes`, `ICommEventPublisher` | Complete |
| 27 | Business Event Integration | `ICommBusinessEventBridge`, `PlatformBusinessEventBridge` | Complete, **off by default** by decision (ADR-030 §7) |
| 28 | Comment Version History | `CommCommentRevisions` | Complete |
| 29 | Soft Delete | `DeletedAt` on threads/comments/attachments/participants | Complete |
| 30 | Edit History | `RevisionCount`, `EditedAt/By`, `GetRevisionsAsync` | Complete |
| 31 | Audit Trail | `CommAuditEntries`, `ICommAuditWriter`, append-only | Complete |
| 32 | Timeline Aggregator | `ICommTimelineAggregator` + `ICommTimelineSource` (4 sources) | Complete |
| 33 | Universal Entity References | `CommEntityRef` | Complete |
| 34 | Entity Comments | `ICommCommentService` keyed by `CommEntityRef` | Complete |
| 35 | Entity Timeline | `ICommTimelineAggregator` keyed by `CommEntityRef` | Complete |
| 36 | Task Comments | configuration: add `Tasks`/`Task` code to `EnabledEntityCodes` | **Blocked on registry — G9** |
| 37 | CRM Comments | configuration: `Customer` (registered) | Enabled by config today |
| 38 | Support Comments | no Support/Ticket code in `IEntityRegistry` | **Blocked on registry — G9** |
| 39 | Accounting Comments | `SalesInvoice`, `PurchaseInvoice`, `JournalEntry` | Registry already says `SupportsComments` for the first two |
| 40 | Inventory Comments | `Quotation`, `Item` | `Quotation` registered; `Item` by config |
| 41 | Project Comments | `Project` (registered code) | Enabled by config today |
| 42 | HR Comments | `Employee` (registered code) | Enabled by config today |
| 43 | Manufacturing Comments | `ManufWorkOrder` (registered code) | Enabled by config today |

**Modules 36–43 need no code.** They are `CommunicationPlatform:EnabledEntityCodes` entries, which is the point of
ADR-031. The two blocked ones (36, 38) are blocked on a *registry code that does not exist*, not on this platform:
configuration may onboard a **registered** entity, never invent one — that is the ADR-002 rule the allow-list does
not override.

---

## 6. Extension points

| Interface | Purpose | Shipped implementations | How to extend |
|---|---|---|---|
| `ICommPrincipalSource` | expand a principal (mention target / permission grantee) to employees | Employee, Team, Department | register another for a kind; **last registration wins** |
| `ICommTimelineSource` | contribute to a record's merged timeline | kernel events, comments, mentions, comm audit | register another; last wins per source kind |
| `ICommNotificationChannel` | deliver over a transport | InApp | register a real Email/Push/WhatsApp adapter |
| `ICommBusinessEventBridge` | forward to the platform kernel | `NullCommBusinessEventBridge` (default), `PlatformBusinessEventBridge` | `services.UseBusinessEventBridge()` **and** set the flag |
| `ICommTemplateTextProvider` | localize template text | built-in bilingual defaults | register a resx-reading provider |
| `ICommFilePreviewProvider` | classify an attachment, supply a thumbnail | `DefaultCommFilePreviewProvider` | register a thumbnailing provider |
| `ICommDeliveryClaimStore` | claim a delivery batch | `EfCommDeliveryClaimStore` | register a SQL-Server store (see §9 G10) |
| `ICommEntitySurface` | decide capability per entity | `CommEntitySurface` | replace to drive onboarding from a table |
| `ICommPushTokenStore` | resolve a recipient's device tokens | **none** | required before a push adapter can work |

---

## 7. Configuration

```jsonc
"CommunicationPlatform": {
  "EnabledEntityCodes": [ "SalesInvoice", "PurchaseInvoice", "Quotation", "Customer",
                          "Project", "Employee", "ManufWorkOrder", "Item", "PosOrder" ],
  "BlockedEntityCodes": [],
  "MaxBodyBytes": 32768,            // BYTES of UTF-8, not characters — an Arabic body is ~2 bytes/char
  "MaxMentionsPerComment": 25,
  "MaxGroupMentionRecipients": 200, // over this a group mention is REFUSED, never truncated
  "MaxReplyDepth": 3,
  "AuthorEditWindowMinutes": 1440,
  "EnableTeamMentions": true,
  "EnableDepartmentMentions": true,
  "EnableRoleMentions": false,      // true still does nothing — no source is registered (G1)
  "EnabledChannels": [ "InApp" ],
  "DefaultPreferenceMode": "Immediate",
  "MaxDeliveryAttempts": 5,
  "StaleClaimMinutes": 10,
  "BridgeToBusinessEvents": false   // see the deployment precondition in §8
}
```

Every default is the **closed** one. A deployment that omits the section gets a working platform with a
conservative surface; a deployment that never calls `AddCommunicationPlatform` gets nothing at all.

---

## 8. Deployment

1. Apply `deploy/sql/communication_platform_slice_001.sql` **with `sqlcmd -I`** (QUOTED_IDENTIFIER ON). Several
   indexes are filtered, and SQL Server silently refuses to create a filtered index when that option is OFF —
   the footgun CLAUDE.md already records for `platform_business_events_slice_002.sql`.
2. The script has **no dependencies**: it references no kernel table and can be applied to a database where no
   platform slice has been applied at all. That is deliberate (§4.8).
3. It is **additive and idempotent**: 14 `CREATE TABLE`, indexes and `CHECK` constraints, all guarded. No
   `UPDATE`, `DELETE`, `INSERT`, `DROP` or `TRUNCATE`; no `ALTER` of a table this platform does not own; no FK
   leaving the platform. `CommunicationSchemaParityTests` asserts each of those claims against the file text.
4. Then, and only then, wire the code: `builder.Services.AddCommunicationPlatform(builder.Configuration);`

**Before enabling `BridgeToBusinessEvents` (both the flag *and* `UseBusinessEventBridge()`):** confirm
`platform_business_events.sql` **and** `platform_business_events_slice_002.sql` are applied on every database the
app runs against. `RecordAsync` has no swallowing catch — a missing table is SQL-208 inside the caller's
transaction, i.e. a failed comment on every screen. Coordinate with the kernel owner.

---

## 9. Open items and honest gaps

Each is a *stated boundary*, not a discovered defect. Numbered so the next phase can pick them up.

| # | Gap | Impact | What closes it |
|---|---|---|---|
| **G1** | `@role` mentions and Role principals are declared but unresolvable. `IPlatformRoleDirectory` answers *what does this principal hold* and exposes **no reverse "who holds this role"** query. | A role mention/grant is refused with a reason; nothing silently resolves to nobody. | An `ICommPrincipalSource` for `Role` + a reverse read over `PlatformRoleAssignments` (a new read in a file this work stream does not own). |
| **G2** | No email adapter. Bridging to the existing `CommMessages` outbox was **rejected for now**: it is another team's queue with its own status vocabulary and retry policy, and a second producer is the "second notification path bypasses the outbox" conflict CLAUDE.md records (HM-D46). | Email delivery rows park as `Skipped` with a reason. | An `ICommNotificationChannel` for Email, decided **with** the Comm module owner. |
| **G3** | Push is contracts-only, as the requirement asked. `ICommPushTokenStore` has no implementation. | Push rows park as `Skipped`. | A token store + a push adapter. |
| **G4** | WhatsApp is declared-not-wired, as the requirement asked ("Future"). | Same. | An adapter. |
| **G5** | `Digest` behaves as Immediate for InApp (the inbox *is* the digest) and is **excluded** for other channels, because no batching worker exists. Reported per channel rather than silently treated as Immediate. | A recipient asking for a daily email summary gets no email rather than one per event. | A digest batching worker. |
| **G6** | `ICommFilePreviewProvider` classifies but generates no thumbnails; `ThumbnailStorageKey` is always null. | A UI can decide *what kind* of preview is possible and whether inlining is safe. | A thumbnailing provider. |
| **G7** | `CommAuditEntries.Action` has **no `CHECK` constraint**, unlike every other vocabulary column. Deliberate: the log is append-only and permanent, and a constraint on a growing 23-value vocabulary becomes a deployment-ordering hazard — a new action in code would fail every write until the next SQL slice ran. `CommAuditWriter` refuses an unknown value before it reaches the column. | Enforcement is in code, not in the schema. | A constraint, if the vocabulary ever stabilises. |
| **G8** | Template text uses built-in bilingual defaults. The resx keys a localized deployment needs are **published** by `ICommTemplateCatalog.RequiredResourceKeys()` (28 keys, `Comm.Notification.*`) but not added to `SharedResources.*.resx` — that file is shared and reordered by another work stream, and this phase avoids whole-file edits to it. | Arabic and English are correct today; French falls back to the built-in text. | Add the 28 keys via the selective-commit plumbing + register a resx-reading `ICommTemplateTextProvider`. |
| **G9** | Modules 36 (Task) and 38 (Support) have **no `IEntityRegistry` code**. Configuration may onboard a registered entity, never invent one. | Those two comment surfaces cannot be enabled yet. | The registry owner adds `Task` / `SupportTicket` codes. |
| **G10** | `EfCommDeliveryClaimStore` claims via read-then-stamp. Correct for a single worker; **safe but lossy in throughput** for several (two workers may select the same row; one loses on save). The production shape is a SQL-Server store mirroring `SqlEventDispatchStore`'s `UPDATE TOP(n) … OUTPUT … WITH (ROWLOCK, READPAST, UPDLOCK)`, which cannot be expressed in provider-neutral EF and cannot be exercised by the SQLite test host. | No correctness loss; contention under concurrency. | Implement `ICommDeliveryClaimStore` for SQL Server + a gated concurrency test alongside `DispatchConcurrencyTests`. |
| **G11** | `CommNotificationService.CanReceiveAsync` synthesises a recipient `BusinessContext` **with no roles**, so role-derived elevation is not reconstructed: a recipient who genuinely holds `ViewConfidential` may still be skipped for a Confidential subject. | Errs toward **withholding** a notification, never leaking one. | Resolving another employee's session-free context — `IBusinessContextFactory` can do this only for the current request's identity today. |
| **G12** | Notification/entity labels use the entity **key** (`SalesInvoice#1001`) or the thread subject, not `IEntityRegistry.ResolveAsync`. Resolving would run a per-type query against a business table from inside a notification path. | A notification title reads `SalesInvoice#1001` rather than `INV-2026-0042`. | Call `ResolveAsync` at render time, accepting the extra read. |
| **G13** | The existing `DocComments` feature is **untouched** and not migrated. It still serves `_DocTimeline` on SalesInvoiceDetail / PurchaseInvoiceDetail / QuotationDetails. | Two comment stores coexist until somebody decides. | See §10. |

---

## 10. The `DocComments` question (G13)

`Models/Context/Comm/DocComment` + `DocCommentService` are a working production feature owned by another work
stream. This phase deliberately did not migrate them: doing so would change a live feature outside this scope.

The overlap is real and should be resolved by whoever owns that feature. A migration path exists and is cheap,
because `DocComment.EntityType` already holds canonical registry codes (Slice-003 made that true):

```sql
-- SKETCH ONLY. Not part of slice-001, not run by it, and not endorsed here.
-- 1. one CommThread per distinct (CompanyID, EntityType, EntityId), Kind='Discussion', ThreadKey=''
-- 2. one CommComment per DocComment: Body, CreatedBy -> AuthorEmployeeId, Visibility='Internal',
--    BodyFormat='PlainText'  (DocComment bodies were never parsed as markdown, so claiming Markdown
--    would retroactively reinterpret every stored body)
-- 3. DocComment.DeletedAt -> CommComment.DeletedAt
-- 4. NO mention backfill: DocCommentService took mentionedIds as a transient argument and never stored
--    them, so the historical mention targets do not exist anywhere and cannot be reconstructed.
```

Until then, the honest description is: **two comment stores coexist**, serving different screens, with no data
flowing between them.

---

## 11. Verification

**250 unit tests, all passing**, in `CrossBuy.Tests/Communication/`:

| File | Tests | What it proves |
|---|---|---|
| `CommunicationDiWiringTests` | 33 | The **real container** builds with `ValidateOnBuild` + `ValidateScopes` and every service resolves. No hosted service. The bridge defaults to null. Only InApp is registered. |
| `CommBodyPolicyTests` | 34 | Byte-not-character limits, the mention grammar, code-fence exclusion, structure counts, excerpts. |
| `CommCommentServiceTests` | 28 | Thread identity, notes, replies, revisions holding the **previous** body, soft delete, idempotency, company isolation, paging. |
| `CommAccessPolicyTests` | 20 | The entity gate first; grants additive-only; the restricted set/flag split; cross-company; audited denials. |
| `CommMentionAndNotificationTests` | 28 | **The four artefacts.** Group expansion + company intersection. A mention is not a grant. Dedup. Templates, preferences, digest. |
| `CommTimelineAndDispatchTests` | 35 | Merge + tie-break, a throwing source, kernel not-applicable, claim-by-status, skip vs fail, reactions, read watermark, preview, audit enforcement, bridge translation. |
| `CommunicationSchemaParityTests` | 72 | Every table/index/unique/filter/vocabulary in the model appears in the SQL; the script is guarded, additive, and FK-contained. |

The DI test exists because CLAUDE.md records it as a permanent rule learned from an outage: *"A DI graph is not
verified by unit tests that construct services by hand. 112 green tests coexisted with an application that could
not boot."*

**Three real defects were found by these tests during this phase** and are documented in the delivery report:
a conflated visibility bound, a lock reported as an authorization failure, and the kernel timeline source gating
on the wrong flag.
