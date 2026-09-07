# ADR-029 — Tasks and Communication permission models

**Status:** Accepted (Stage 1 Batch C) · **Date:** 2026-08-04 · **Depends on:** ADR-026 (shared RBAC).

Two modules in one ADR because they were assessed together and share infrastructure — but they have **separate
vocabularies**, because their business behaviour differs. Collapsing them into one would have been the mistake the
brief warned against.

---

# Part 1 — Tasks

## 1.1 Starting position

`TasksController`'s 13 mutating actions carried **ZERO authorization**: any signed-in employee could read, edit,
reassign and complete **every task in the company**.

## 1.2 What the data supports — and what it does not

**Anchors that exist:** `AssigneeEmployeeId`, `CreatedByEmployeeId`, `EntityType`/`EntityId` (the linked object),
`CompanyId`, and the company-intersected manager hierarchy.

**Anchors that do NOT exist, and were therefore not invented:** no team, no followers, no visibility tier, no
`BranchId`, and no workflow-task marker. `IsScheduled`/`TaskAutoRule`/`MatchedAt` distinguish *auto-generated* from
manual — not workflow from ordinary.

## 1.3 Vocabulary — eight actions

`read` · `create` · `edit` · `assign` · `reassign` · `complete` · `reopen` · `manage`

**Dropped:** `delete` (`TasksController` has no delete action) and `confidential-view` (`TaskItem` has no visibility
tier to confer it).

Roles: `TasksAdministrator` (company-wide), `TasksSupervisor` (may reassign), `TasksViewer` (read-only).

## 1.4 Record-level rules

1. **Module `read` alone is not enough for a specific task** — this is the rule the brief singles out. A caller must
   be the assignee, the creator, or a manager of the assignee.
2. **`reassign` is deliberately stronger than `edit`.** An assignee may work a task but not hand it on; the creator
   or a manager may.
3. **`complete`/`reopen` require access to THAT task.**
4. **The linked entity is checked through `IPlatformPermissionProvider`.** A task about a `SalesInvoice` must not
   reveal that invoice to someone who may not see it — **even to the task's own assignee**. Tested in both
   directions, so the denial is provably the linked-entity rule and not a broken lookup.
5. **The task's company comes from the ROW.** `TaskItem.CompanyId` — **not `CompanyID`** like every other entity. A
   copied predicate will not compile, which is the good outcome; a test asserts the property name so a rename
   cannot silently drop the company filter.
6. Under bootstrap-open a **specific** task still requires a relationship. This is the one place compatibility is
   deliberately narrower than "behave exactly as today": preserving "every employee sees every task" for a named
   task would make the module's first record-level rule do nothing.

## 1.5 `AccessScope` — implemented for Tasks

`ResolveScopeAsync` returns `None | Own | Team | Company`, with `Team` carrying **company-intersected** principal
ids. `CanAsync` and `ResolveScopeAsync` are **agreement-tested** across bootstrap-open, administrator and viewer, for
three tasks (mine / my report's / a stranger's) — because two shapes of one rule is exactly how ~1,100 hand-written
`CompanyID` predicates diverged.

`CrossCompany` is never produced by a module role; it exists so a consumer can *represent* what an authorized
bypass holder sees.

## 1.6 Proof endpoint

`TasksController.ConfirmMatch(int taskId, int entityId)` — linking a task to a movement EDITS it.

---

# Part 2 — Communication

## 2.1 The one module that already had a record-level rule

`ChatService.IsMemberAsync` checked `ConversationMember` before returning a conversation, which is why the chat
endpoints were not a leak. That rule is **canonicalised, not replaced**: `ConversationMember` is untouched, and the
service is now the single place the rule lives so the controller, the hub and any future consumer answer the same
way — and so a conversation belonging to another company is refused on the **conversation row** before membership is
consulted.

The parts that had **no** rule and now do: announcements, the email outbox, and group management.

## 2.2 Vocabulary — six actions

`read` · `send` · `create-group` · `manage-group` · `announcement-send` · `outbox-manage`

**Dropped:** `moderate` (no moderation feature exists), `participant-manage` (folded into `manage-group`, which is
what `ConversationMember.Role = Owner` actually expresses), `confidential-view` (no communication entity carries a
visibility tier). **Added:** `outbox-manage` — see §2.4.

Roles: `CommunicationAdministrator`, `AnnouncementPublisher`, `OutboxOperator`.

## 2.3 Record-level rules

1. **Direct AND group reads require membership.** Belonging to the company is not enough — and this holds **under
   bootstrap-open too**: a private message is not "open by default" for compatibility.
2. **`manage-group` requires `Owner`** on that conversation, or the module's administrative role. A plain member
   cannot rename the group or change its membership.
3. **Cross-company is rejected on the conversation row**, so a stray member row for another company's conversation
   grants nothing. Tested.
4. **Announcements** follow the existing audience rule — `Scope = Company | Branch` + `BranchID`. A branch-scoped
   publisher grant may only publish to its own branch; a company-wide grant may publish anywhere in the company.
5. **No company-wide conversation read exists, not even for an administrator.** An administrative right over groups
   is not a right to read private messages, so `ResolveConversationScopeAsync` returns `Own` — never `Company`.

## 2.4 The email outbox — why it needed a new action

`CommMessage` carries `CompanyID`, `ToAddress`, `Subject`, `Body`, `Status`, `Attempts`, `DeletedAt` — and **no
owner, sender or participant column at all**. There is therefore no membership rule to derive, so the only honest
gate is company scope plus an explicit administrative right.

**`outbox-manage` is never bootstrap-open.** Exposing every queued email body to every employee by default would be
a new exposure created by the batch meant to close one. Tested.

## 2.5 Not implemented

Moderation, external collaboration, customer portals, and the Communication Platform. **SignalR hub authorization is
NOT changed in Batch C** — `IsConversationParticipantAsync` is exposed for the hubs to adopt, but no hub was
modified, so the C6/C10 SignalR requirement is reported **Partial** in the delivery report with the exact missing
work.

## 2.6 Proof endpoint

`ChatController.Messages(int c, int? before)` — a non-participant now receives **403**, not an empty thread, because
an empty thread reads as "no messages" rather than "not yours".