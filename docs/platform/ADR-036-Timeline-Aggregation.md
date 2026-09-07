# ADR-036 — Timeline Aggregation

**Status:** Accepted
**Related:** ADR-002 (entity registry), ADR-004 (visibility), ADR-005 (legacy timeline adapters), PKS-001,
ADR-030, ADR-031, CPS-001 §5 (modules 12, 32, 35)

---

## Context

Requirement: an Activity Timeline, a Timeline Aggregator and an Entity Timeline for every business entity.

A timeline already exists. `ITimelineProjectionService` reads `BusinessEvents`, merges legacy adapters, and applies
**four** filters — company, branch, module permission and per-row event visibility. Its own contract note says it
returns "a PROJECTION, not events, so introducing a persisted projection table later changes nothing above this
interface."

The Communication Platform adds facts that are *not* business events: comments, mentions, attachments, moderation
acts. Those must appear on the same record history.

## Decision

### §1 — This platform does NOT read `BusinessEvents`

That is the single most important property of the design.

Reading it directly would duplicate all four kernel filters, and the copy would **drift**: the day the kernel adds a
fifth filter, this platform would quietly keep showing what the kernel had decided to hide. Worse, it would put a
second, disagreeing timeline in the product — the exact failure ADR-002 was written about (three vocabularies for one
concept).

So the kernel is **one source among several**, reached through its own service, and its filters keep applying.

### §2 — `ICommTimelineSource`, injected as `IEnumerable`

The same extension shape the kernel uses for `ILegacyTimelineAdapter`. Four sources ship:

| Source | Reads | Notes |
|---|---|---|
| `BusinessEvent` | `ITimelineProjectionService` | the kernel's own service, never its table |
| `Comment` | `CommComments` | carries the body, already authorized (see §5) |
| `Mention` | `CommMentions` ⋈ `CommComments` | its own row so "who was pulled into this record" is answerable without reading every body |
| `CommAudit` | `CommAuditEntries` | a **short allow-list** only (see §6) |

**Last registration wins per source kind**, so a deployment replaces a built-in contributor by registering its own
afterwards. Two contributors for one kind would double every item.

### §3 — No projection table

A timeline is a **read** over facts that already exist. Materialising rows would be a second copy of the same truth,
free to disagree with it. This is the line the kernel already holds, and this platform holds it too.

A consequence worth stating: "every mention must create a Timeline Entry" is satisfied **without a fifth table** —
the mention's own rows plus its audit row *are* the timeline entry, surfaced by the `Mention` source.

### §4 — One broken source must not blank a record's history

Each source is awaited independently. A throw is caught, reported in `SourceReports`, and the other sources still
render. A failing contributor producing an empty timeline looks exactly like "this record has no history" — the same
"passes by examining zero rows" failure CLAUDE.md records for unbound workers, in a different subsystem.

`SourceReports` is returned **always**, contributing or not, with a note: `not applicable`, `denied`, `empty`, or
`error: …`. A merged timeline that silently returns nothing from one source is undiagnosable.

A caller **denied the entity** gets an empty result plus a reason, not an exception — a record timeline is a widget
on a page, and a caller who may not see it should get an empty widget while the reason goes in the report.

### §5 — The kernel source gates on the REGISTRY, not on `ICommEntitySurface`

`ITimelineProjectionService` **throws** for an entity whose `SupportsTimeline` is false, and only four codes qualify
today — so asking first is the difference between "this source does not apply" and an exception on most records in
the product.

The guard reads `IEntityRegistry` **directly**. `ICommEntitySurface` deliberately treats
`CommunicationPlatform:EnabledEntityCodes` as an additive onboarding path for *this platform's own* tables
(ADR-031); it has no authority over the kernel.

This was a real defect in this phase, caught by `CommTimelineAndDispatchTests`: routing the guard through the surface
made a configured-but-unregistered entity look eligible, so the kernel threw and the aggregator reported `error:` —
a working timeline reported as broken.

The kernel's `PlatformAccessDeniedException` is likewise caught and **reported**, not rethrown: the caller may still
be entitled to the comment stream.

### §6 — Only a short allow-list of audit actions reaches the timeline

`ThreadLocked`, `ThreadUnlocked`, `CommentDeleted`, `CommentRestored`.

Most audit rows are for an *auditor*, not a record screen. These four are the moderation acts a reader of the record
needs in order to understand a gap in the conversation. Reactions, participation and read state are audited but never
surfaced here.

### §7 — Visibility is filtered before an item exists

Each source applies the caller's readable-visibility set **in SQL**, then the row-level own-author pass for
`Restricted` (ADR-033 §4). A body therefore appears in a `CommTimelineItem` only when it is authorized by
construction.

The `Mention` source **joins its comment** to inherit that comment's visibility — not a convenience: showing
"X mentioned the finance department" from a Confidential note would leak that the note exists, who wrote it and who
it concerns, without showing a word of it.

The `CommAudit` source cannot apply an own-author pass — an audit row records what a *moderator* did to somebody
else's comment, so "is it mine" is the wrong question. A Restricted moderation act is visible only to a caller who
may read Restricted at all.

### §8 — Kernel visibility values pass through untranslated

A kernel item keeps the kernel's vocabulary. Mapping it into `CommVisibility` would claim a tier the kernel never
assigned, and the two sets are deliberately different (the kernel has `System`; this platform has `Public`).

### §9 — Paging is a timestamp cursor with a deterministic tie-break

`(Before, BeforeKey)`, not an offset and not an id.

An id is meaningless across sources — two sources number their rows independently. And a comment and its mention are
written in **one transaction** and can share a timestamp to the tick, so without a deterministic second key
(`ItemKey` = `"<Source>:<rowId>"`) the two would swap places between pages and the cursor would skip or repeat one.

Each source is asked for a **full page**, not a share of one; the merged result is then cut. Asking each for `take/N`
would drop items from a busy source whenever a quiet one had nothing — a merge must choose from a full candidate set
to be correct.

The cursor is re-applied **after** the merge as well as inside each source, so a third-party contributor that ignores
`before` cannot leak already-seen items into a later page.

### §10 — A separate item type from the kernel's

`CommTimelineItem` is shaped like `TimelineItemViewModel` (bilingual title/description, actor, icon, colour, url) so
one UI component can render both streams — but it is a **separate type**, because inheriting or reusing the kernel's
would couple this platform's read contract to a file another team owns.

Descriptions are **excerpts**, not bodies: a timeline is a scan, and a 32 KB comment would make one row fill the
screen. The full body is a separate field on the same item.

## Consequences

**Positive.** One timeline per record, assembled from independent contributors, with the kernel's filters intact. A
new module adds a source and nothing else changes. No second copy of any fact. A broken contributor is visible rather
than silent.

**Negative / accepted.** Merging in memory means the page size bounds per-source reads rather than the total. The
kernel source contributes for only four entity codes until the registry onboards more. `SourceReports` is a contract
consumers must actually read to benefit from §4.
