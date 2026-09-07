# ADR-032 — Mention Resolution and Principal Expansion

**Status:** Accepted
**Related:** ADR-026 (shared platform RBAC), ADR-030, ADR-033, CPS-001 §5 (modules 3, 13, 15)

---

## Context

Mentions must support `@Employee`, `@Team`, `@Department`, and `@Role` in future. The requirement is explicit that
**every mention must create a Mention entity, a Notification, a Timeline entry and an Audit entry.**

Two of those target kinds have no table in this product. There is no `Team` entity and no `Department` entity —
there is a single user-maintained `Hierarchical` org tree, and `Employee.DepartmentID` points into it.

## Decision

### §1 — One resolver serves mentions AND permissions

`ICommPrincipalResolver` expands a principal for **both** mentions and thread-permission grants. That is a
correctness requirement, not an optimisation: if the two expanded "the sales department" differently, a note could
notify somebody it does not authorize, or authorize somebody it cannot notify. Sharing the expansion makes the two
answers the same by construction.

### §2 — `ICommPrincipalSource` per kind, injected as `IEnumerable`

The same shape the kernel uses for `ILegacyTimelineAdapter` and `IModulePermissionAdapter`. **Last registration wins
per kind**, so a deployment can replace a built-in source by registering its own afterwards — the standard override
idiom. Silently keeping the first would make an override look ignored.

### §3 — What each kind resolves to

| Kind | Resolution | Why this and not something else |
|---|---|---|
| `Employee` | the employee row, if active and in the caller's company | |
| `Team` | `IOrgHierarchy.DirectAndIndirectReportsAsync(manager)` | "Team" has no table, and inventing one would make a collaboration feature the owner of new master data. The org tree already expresses exactly this relationship, and `IOrgHierarchy` already walks it with a cycle guard **and** the company intersection. `@team:{id}` therefore addresses a **manager's** subtree. |
| `Department` | an org node **and every node beneath it**, then `Employee.DepartmentID IN (…)` | Walks **nodes**, not employees: a department mention must reach sub-departments too, and walking employees would miss anyone filed under a child node. Iterative with a visited set, because the tree is user-maintained and can contain a cycle. |
| `Role` | **nothing — declared, not wired.** See §5. | |

### §4 — Company intersection is applied twice, deliberately

`Hierarchical` carries **no `CompanyID`** — `IOrgHierarchy`'s own comment documents that a raw descendant walk can
return employees of another company, and that two existing callers do exactly that. Every source intersects with
`Employee.EmpCompanyID`; the resolver then repeats the intersection so a source written later — by us or by a
deployment — cannot leak another tenant's employees through it. `IsActive` is applied in the same pass: notifying a
terminated employee is noise, and granting them thread access is a hole.

The redundancy is cheap and it is the difference between a rule and a habit. Proven by
`A_department_grant_does_not_reach_another_companys_employee_in_the_same_node`.

### §5 — `@role` is declared, not wired, and refused with a reason

`Role` is in the vocabulary and in the SQL `CHECK` constraint, so its spelling is frozen now. Nothing resolves it:
`IPlatformRoleDirectory` answers *what does THIS principal hold* and exposes **no reverse "who holds this role"**
query. Building one means a new read over `PlatformRoleAssignments`, in a file this work stream does not own.

Consequences of that, all deliberate:

- A `@role:` token **parses** (so the service can refuse it with a specific reason) but writes **no mention row** —
  a row that resolves to nobody would show the author a mention chip implying somebody was told.
- A thread **grant** to a Role principal is **refused at write time**, because a stored grant that never matches
  looks, to whoever granted it, exactly like a working one.
- `EnableRoleMentions = true` still does nothing. The flag exists so the switch has a name before the provider
  lands, and the resolver's message names the missing **source** — the actionable fact.

### §6 — Group mentions are refused, never truncated

Over `MaxGroupMentionRecipients` (default 200), the mention is **refused**. Truncation is the worse failure: the
author believes the whole department was notified and half of it was not, and nothing in the UI can tell them
otherwise.

### §7 — The label and the count are frozen at authoring time

`CommMention.LabelAr/LabelEn` and `ResolvedRecipientCount` are stored, not recomputed on read. A department renamed
or grown next month was **not** the department that was mentioned, and re-resolving would rewrite history. An
author-supplied label loses to the resolved one — otherwise an author could write `@[CEO](employee:12)` over
somebody else's name.

### §8 — A mention is not a grant

Mentioning somebody in a Confidential note does not let them read it: the notification path drops a recipient who
cannot read the subject's visibility tier. The **mention row still exists**, so the audit records that the author
tried — which is the honest outcome. Without this, `@`-typing would be a privilege-escalation primitive.

### §9 — Mention grammar

Two accepted forms, both anchored on `@`:

```
@employee:12                     canonical — what a client sends when it knows the id
@[Ahmed Zakarya](employee:12)    labelled — deliberately shaped like a markdown link, so an UNRENDERED
                                 body still reads as a name rather than as an id
```

The kind is a **closed alternation**, not `\w+`, so a typo (`@empoyee:12`) is plain text rather than an unknown
kind that would have to be rejected and would break the whole comment. A malformed token is likewise dropped as
plain text: an author cannot always tell where the grammar ends and their sentence begins, and losing a comment to
a typo is the worse outcome.

**Tokens inside fenced or inline code are not mentions.** Documenting the mention syntax inside the product must not
notify employee 12. This is the case that makes the feature safe to write about.

### §10 — The four artefacts, in one transaction

`ICommMentionService.RecordAsync` enrols in the caller's transaction and does **not** save. All four artefacts share
one fate:

| Artefact | Where |
|---|---|
| Mention entity | `CommMentions` + `CommMentionRecipients` |
| Audit entry | `CommAuditEntries`, via `ICommEventPublisher` |
| Timeline entry | the **same** audit/event row, surfaced by the aggregator's `Mention` source |
| Notification | `CommNotifications` + `CommNotificationDeliveries` |

"Timeline entry" deliberately does **not** mean a fifth table. A timeline is a read over facts that already exist;
materialising a separate row would be a second copy of the same truth, free to disagree with it — which is what the
kernel avoided by making `ITimelineProjectionService` return "a PROJECTION, not events".

## Consequences

**Positive.** No new master data. Mentions and permissions cannot disagree about group membership. Cross-tenant
expansion is structurally excluded. Adding `@role` is one class and one registration.

**Negative / accepted.** `@team:` addressing a *manager id* is unusual and needs UI explanation. A department
mention loads the whole `Hierarchical` table to walk it — acceptable for an org tree, and the same thing
`IOrgHierarchy` already does. `@role` remains unavailable (CPS-001 gap G1).
