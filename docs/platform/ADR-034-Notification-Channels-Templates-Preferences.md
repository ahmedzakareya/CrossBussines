# ADR-034 — Notification Channels, Templates and Preferences

**Status:** Accepted
**Related:** ADR-003 / ADR-007 (dispatch state, SQL locking), ADR-006 (notification projection), ADR-030,
Slice-003 (`comm_outbox_slice_003.sql`), CPS-001 §5 (modules 13, 16–22)

---

## Context

The platform must notify people about collaboration activity across in-app, email, push and (future) WhatsApp, with
per-recipient preferences and reusable templates — without becoming a second uncontrolled notification path. The
product already has: a legacy `NotificationService` with twelve producers, the kernel's `NotificationProjection`
consumer, and a `CommMessages` email outbox owned by another work stream. CLAUDE.md already records "a second
notification path bypasses the outbox" as an open conflict (HM-D46).

## Decision

### §1 — Four separated concerns

```
DECIDE   which template, which audience, which tokens          CommNotificationRequest
RESOLVE  who, may they, do they want it, on which channels     ICommPreferenceResolver + ICommAccessPolicy
RENDER   the text, per culture                                 ICommTemplateRenderer
DELIVER  one row per (notification, channel), retryable        ICommNotificationDispatcher
```

A caller supplies only the first. ADR-006 proved the value of this split for the kernel — its mapper is
"deliberately a pure function of the envelope: no database, no permissions, no idempotency, no delivery" — and the
consequence here is the same: a new notification comes with no new permission code and no new retry code.

### §2 — Templates are code-declared, not database rows

- A row lets an operator delete a template a producer still references, turning a notification into a silent no-op
  at runtime. A frozen key cannot be deleted by accident.
- The token list is a compile-time contract: a producer that forgets a token fails a test, not a customer's inbox.
- The **text** is still overridable per deployment and culture, through `ICommTemplateTextProvider`.

Seven templates ship. Each declares its category, its `NotificationTypes` catalog key (reused, so an in-app render
gets the icon and priority the rest of the product already uses), its default channels, its required tokens, and
bilingual default text.

### §3 — Localization without editing a shared resx

`ICommTemplateTextProvider` is a **resource-key contract**: the default implementation returns the built-in
bilingual text, and a deployment that has added the keys registers a provider that reads them and falls back for
anything missing. Fallback is a *parameter*, so a provider physically cannot return null and leave a notification
with an empty title.

This exists because CLAUDE.md requires user-facing strings to go through Resources **and** requires this work stream
not to whole-file-edit `SharedResources.*.resx`, which another team has reordered. The 28 keys a fully localized
deployment needs are **published** by `RequiredResourceKeys()` — a deliverable rather than a grep exercise
(CPS-001 gap G8).

### §4 — Rendering rules

- A **missing required token** is refused at DECIDE time (`template_token_missing`) rather than rendering
  `{actor}` literally in somebody's inbox.
- An **unknown/optional** token renders as **empty**, not as its own braces: `{reason}` with no reason supplied
  should read as an absent clause, and a literal `{reason}` looks like a bug to a recipient who cannot know it was
  optional.
- A token **value is never re-scanned**. An employee whose display name contains `{entity}` would otherwise have it
  substituted — a small injection into other people's notifications. Hence a hand-rolled scan rather than
  `string.Replace` per token.
- Rendered text is **stored in both languages**. Re-rendering on read would show today's template wording for a
  notification sent last year, and would require keeping the token values alive forever to do it.

### §5 — The channel plan is an INTERSECTION, never a union

```
template.DefaultChannels  ∩  options.EnabledChannels  ∩  recipient preference ≠ Off
```

No gate can **add** a channel. That is what makes "turn Email off for this deployment" an absolute statement rather
than a default a stored preference can override.

**Absence of a preference row is not "off"** — it means "use the deployment default". That distinction is the
difference between a newly enabled channel reaching everybody and reaching only the handful of people who once
opened a preferences screen. The alternative (a row per employee × category × channel) would need a backfill for
every new employee and every new category.

Categories are deliberately **coarse** (Mentions, Comments, Reactions, Participation, Moderation): a recipient facing
twenty decisions makes none, and per-template opt-out means every new template silently arrives switched on.

**Digest is resolved honestly.** It behaves as Immediate for InApp — an in-app notification waits in an inbox, which
*is* a digest — and is **excluded** for other channels, because no batching worker exists. Treating "send me a daily
summary" as "send me each one immediately" is the opposite of what the recipient asked for. Reported per channel
(CPS-001 gap G5).

**Preferences are personal, with no administrative override.** An administrator who can switch somebody else's
notifications off can silence a mention they were meant to see, and there is no product reason to allow it.

### §6 — An unwired channel parks its rows as `Skipped`, with a reason

The vocabulary declares Email, Push and WhatsApp; only InApp has an adapter. A delivery row for a channel with no
registered adapter is **`Skipped` with a reason** — never left `Pending` forever, and never retried five times for a
permanent condition.

This is the inverse of the kernel's rule for consumers ("a name in this list with no `IBusinessEventConsumer`
registered accumulates dispatch rows nothing drains"), applied to channels: the vocabulary may name a channel before
an adapter exists, and the dispatcher parks its rows honestly.

`CommChannelResult` therefore has **two** negative cases. `Skipped` is permanent ("this recipient has no push
token"); `Failed` is retryable ("the push service timed out"). Sharing one status would burn five retries on every
permanent condition.

**Email deliberately does not bridge to `CommMessages`.** That is another team's queue, drained by their hosted
service, with its own status vocabulary and retry policy. Writing into it would make this platform a second producer
— the HM-D46 conflict. Bridging is a decision for the phase that owns production wiring, taken **with** that team
(CPS-001 gap G2).

### §7 — In-app has its own inbox, not the legacy `Notifications` table

`CommNotifications` is the in-app notification. The legacy table is written by the kernel's projection consumer and
twelve legacy producers; a second writer with a different dedup convention is precisely the conflict above. A bridge
to it is an extension point with no registered implementation.

### §8 — Two tables: the decision and the attempt

`CommNotification` is the **decision**; `CommNotificationDelivery` is the **attempt**, one row per channel.

Collapsing them into one row with a status column is the mistake this avoids: a notification delivered in-app but
failing over email has no single status, and the retry that fixes email must not resend the in-app one. This is the
per-consumer split ADR-003 chose for `BusinessEventDispatch`, for the same reason — and it is what lets the
dispatcher claim by `Status` alone.

### §9 — Idempotency is real, not an unread-noise guard

`UX_CommNotifications_DedupKey` on `(CompanyID, DedupKey)`, with the key derived from the producing row's identity
(`comment:{id}:mention` + `emp:{recipient}`). A retried transaction produces **zero** extra rows, whether or not the
first has been read. The legacy `NotificationService.dedupKey` is documented as an unread-noise guard; this is
stronger, and deliberately so.

An overlong key is **hashed**, not truncated: a silently truncated key would collide with a *different*
notification, turning idempotency into data loss.

### §10 — Recipients are authorized before a row exists

A notification is a read of the content by another name. `CanReceiveAsync` evaluates the **recipient's** own
visibility tier — not the actor's — and drops those who may not read the subject. See ADR-032 §8 and CPS-001 gap
G11 (the synthesised recipient context carries no roles, so the error is toward withholding).

### §11 — Delivery is a callable drain, not a hosted service

A hosted service is a singleton that may never inject a scoped service, must take `IServiceScopeFactory`, and must
bind an explicit company scope — three rules that each cost a real defect here. This phase ships none;
`DispatchPendingAsync` already has the worker-facing shape.

**The claim rule, verbatim from ADR-003/ADR-007:** claim by `Status` only — **never** a cursor, **never**
`MAX(Id)`, **never** `Id > lastSeen`. Ids are assigned at INSERT and become visible at COMMIT, so a cursor skips a
late-committing lower id permanently. Three states are eligible: `Pending`, `Failed` under the attempt cap, and
`Claimed` older than the stale clock (a worker died holding it).

Ordering is `OrderBy(Id)` so a row cannot be starved by newer traffic — an **ordering**, not a cursor: nothing is
remembered between batches.

The claim is stamped in its **own** `SaveChanges` before any adapter is called, so a crash mid-batch leaves rows
visibly `Claimed` and reclaimable by the stale clock, rather than `Pending` and delivered twice.

**Concurrency, stated honestly:** `EfCommDeliveryClaimStore` is read-then-stamp — correct for one worker, safe but
lossy in throughput for several. The production shape is a SQL-Server store mirroring `SqlEventDispatchStore`'s
`UPDATE TOP(n) … OUTPUT … WITH (ROWLOCK, READPAST, UPDLOCK)`, which cannot be written in provider-neutral EF and
cannot be exercised by the SQLite test host. `ICommDeliveryClaimStore` is that seam (CPS-001 gap G10).

A **throwing** adapter is a `Failed` row, not a crashed batch: the remaining claimed rows still get their attempt.
Rows that reach the attempt cap are counted as **abandoned** and logged at Warning — notifications nobody will ever
receive, and silently leaving them is how a broken channel goes unnoticed.

### §12 — Push is contracts only, as required

`CommPushPayload`, `CommPushToken` and `ICommPushTokenStore` freeze the shape a future adapter receives. No
implementation is registered anywhere, so an unwired push channel is harmless rather than a queue of `Failed` rows.

## Consequences

**Positive.** A new notification is a template plus a `QueueAsync` call. Preferences narrow and never widen. The
queue is inspectable, retryable and idempotent. No second writer to anybody else's table.

**Negative / accepted.** Nothing leaves the queue for email/push/WhatsApp until adapters and a worker exist (gaps
G2–G4). Digest is partial (G5). Multi-worker claim contention (G10). Recipient-side elevation is not reconstructed
(G11).
