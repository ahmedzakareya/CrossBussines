# ADR-035 — Comment History, Soft Delete and the Audit Trail

**Status:** Accepted
**Related:** ADR-001 (transactional events), ADR-004 (visibility), ADR-030, CPS-001 §5 (modules 28–31)

---

## Context

Comments need version history, edit history, soft delete and an audit trail. Two existing conventions collide here
and must be reconciled explicitly rather than by accident:

- **Ours:** "Reverse, never delete. Corrections are reversing entries, never row deletion — history stays whole."
- **The parallel team's feature modules:** every table carries a soft-delete `DeletedAt`.

CLAUDE.md notes both and says "they soft-delete, we reverse — different domains, no conflict". This ADR states why
that is true here.

## Decision

### §1 — Soft delete is correct for a comment; reversal is correct for a ledger

The reversal rule governs **financial** history: a journal entry or a stock movement, where a reversing entry is the
correction primitive because the *arithmetic* must remain provable. A comment has no ledger. Its correction
primitive is a **revision row plus a soft-delete marker** — both append-only, so history still stays whole.

`DeletedAt` therefore appears on threads, comments, attachments and participants. It is not a compromise with the
other convention; it is the same principle applied to a non-arithmetic domain.

### §2 — A revision holds the **previous** body

`CommCommentRevisions` stores the body as it was **before** the edit that created the row. Storing the new body would
duplicate the current comment and leave the original unrecoverable — the opposite of an edit history.
`UX_CommCommentRevisions_Comment (CommentId, RevisionNo)` is unique so a concurrent double-edit cannot write two
revision 3s and make the history unorderable.

A **visibility change is recorded as a revision too**. It is an edit that matters more than a wording change, and an
auditor needs to see it in the same place.

A **no-op edit writes nothing** — no revision, no event, no audit row. Otherwise an accidental double-submit inflates
the history with identical entries and makes the real edits harder to find.

### §3 — The author edit window

An author may correct their own wording for `AuthorEditWindowMinutes` (default 24 h). After that, editing is a
**moderator** act.

Without a window, a comment somebody relied on last month can be rewritten silently. The revision history would
record it, but nobody re-reads a history they have no reason to suspect. A moderator edit is at least attributable to
a role.

An author may **always retract** their own comment, window or not: withdrawing something you said is not the same act
as rewriting it, and the row survives as a soft delete either way.

### §4 — What a deleted comment looks like

| Reader | Sees the row | Sees the body |
|---|---|---|
| Ordinary reader | **no** — filtered out of the page entirely | — |
| Moderator | **yes**, marked `IsDeleted` | **no — blanked** |

The row's *existence* is what moderation needs; the text is recoverable from `CommCommentRevisions`, which is a
deliberate, separate, audited read. An ordinary reader must not see a gap where a row used to be, and a moderator
must.

Deleting a parent comment does **not** orphan its replies, because the row survives.

Only a **moderator** restores. An author who could restore their own deletion could use delete to hide a comment
from a review and bring it back afterwards.

### §5 — The audit trail is this platform's own table, not `BusinessEvents`

Three reasons:

1. **AVAILABILITY.** `BusinessEvents` ships in a kernel SQL slice, and `RecordAsync` throws with no swallowing catch
   when the table is missing or when there is no ambient transaction. If audit lived there, an undeployed slice would
   break commenting on every screen. `CommAuditEntries` deploys with this platform, so its audit is always available.
2. **COMPLETENESS.** The kernel's log is filtered **on read** by visibility and module permission — correct for a
   record *timeline*, wrong for an *audit trail*. An auditor asking "who deleted the note" must get an answer even
   when the note was Restricted. The two have different read rules and cannot be one table.
3. **GRANULARITY.** Reactions, participation changes and permission grants are deliberately **not** bridged to the
   kernel (ADR-030 §7) but they **are** audited. Audit is the superset.

### §6 — Append-only, and enforced structurally

`CommAuditEntries` has **no** `DeletedAt`, **no** `UpdatedAt`, no mutating service method, and **no foreign keys at
all** — an audit row must outlive everything it describes, and a hard FK to `Employees` would let a historical row
block a personnel-record cleanup. `CommunicationSchemaParityTests` asserts the absent columns and the absent FKs.

### §7 — The writer refuses content and capabilities in a detail payload

`CommAuditWriter` **rejects** a serialized detail containing `body`, `storageKey` or `thumbnailStorageKey`, and caps
the payload at 8 KB (an eighth of the kernel's event budget, because an audit detail is a handful of ids and counts).

An audit row is read by more people than the content is, and a capability-bearing file handle in one is a quiet
privilege escalation. "Don't put the body in the audit row" is the kind of rule that survives exactly as long as the
person who wrote it — so it is enforced, not documented. Proven by
`An_audit_detail_containing_a_storage_key_or_a_body_is_refused`.

### §8 — Enrol, never own

`Append` adds a row to the change tracker and does **not** save. The calling service's single `SaveChanges` persists
the business row and its audit row together. An audit writer that saved on its own would produce the failure it
exists to prevent: a rolled-back comment with a committed audit line claiming it was posted.

A **deliberate asymmetry with the kernel:** `RecordAsync` throws without an ambient transaction because its caller is
always a financial transaction that must fail with it. This writer does not, because its callers open their own
transaction (`CommTransaction`) and a comment has no financial caller to fail. The invariant is still
one-`SaveChanges`-per-operation, enforced by tests rather than by a throw.

### §9 — The action vocabulary is enforced in code, not by a `CHECK`

Every other vocabulary column in this platform carries a `CHECK` constraint. `CommAuditEntries.Action` does **not**,
and that is a considered exception: the log is append-only and permanent, and a constraint on a 23-value vocabulary
that will grow becomes a deployment-ordering hazard — a new action in code would fail every write until the next SQL
slice ran. `CommAuditWriter` validates against `CommAuditActions` and refuses an unknown value **before** it reaches
the column, so the vocabulary is enforced where it can fail safely (CPS-001 gap G7).

`CommAuditWriter.ActionForEvent` is **total** over `CommEventTypes`: an unmapped event throws rather than defaulting,
so adding an event without deciding how it is audited fails at the first call instead of producing untraceable rows.
Asserted for every event type by `Every_communication_event_type_maps_to_an_audit_action`.

### §10 — One hard delete, and only one

Removing a **reaction** deletes the row. It is the only hard delete in the platform, and it is deliberate: a
withdrawn reaction has no author's words in it, no decision rests on it, and a soft-deleted reaction would have to be
excluded from every count forever. The **audit row** records that it happened, which is the part that matters.

### §11 — Correlation

Every audit row carries `BusinessContext.CorrelationId`, so a comm audit row and a kernel event row from the same
request share an id and can be joined during an investigation. Indexed (filtered on non-null).

## Consequences

**Positive.** Full edit history with the original always recoverable. Deletion never orphans a reply. Audit survives
an undeployed kernel slice and is complete regardless of visibility. Content and file handles cannot leak into it.

**Negative / accepted.** Two "history" stores in the product (kernel events + comm audit), joined by correlation id
rather than by a foreign key. The action vocabulary is not enforced by the schema (G7). `CommAuditEntries` will be a
large table; the four indexes are chosen for the entity, thread, actor and correlation reads.
