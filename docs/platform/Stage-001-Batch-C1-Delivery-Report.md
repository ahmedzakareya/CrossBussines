# Stage 1 — Batch C.1 (Completion Patch) — Delivery Report

**Scope:** the three items the Batch C brief deferred, and nothing else.
**Status:** **complete.** All three delivered, verified, and covered by tests.
**Suite:** **645 total — 599 passed, 0 failed, 46 skipped** (`dotnet test -c TestRun`, 47 s).
**Not started, by instruction:** Batch D1 (risk-ranked endpoint enforcement) and Batch D2 (Security Administration
Console). No endpoint was remediated in this patch. No Unified Inbox.

---

## 1. What was delivered

| Item | Subject | Verdict |
|---|---|---|
| **C.1-1** | `AccessScope` set-based query proof — one resolution, one predicate, no per-row `CanAsync` | Delivered, with the missing translator built |
| **C.1-2** | SignalR conversation authorization canonicalized on `CommunicationAccessService` | Delivered — **and it closed a real hole, not a cosmetic one** |
| **C.1-3** | The two legacy cross-company hierarchy walks | Delivered — **and it exposed a second defect the fix itself would have created** |

Two of the three turned out to be worse than Batch C described them. Both corrections are in §5.

---

## 2. C.1-1 — the set-based query proof

### 2.1 What was actually missing

Batch C shipped `ResolveScopeAsync` and an agreement test against `CanAsync`. What it did **not** ship was the
translation. The Own/Team/Company → predicate mapping existed **only as a comment** above `ResolveScopeAsync`.

That is not a documentation gap. A comment cannot be tested, and it invited every future consumer — a task list, a
dashboard count, the Unified Inbox — to write the translation again, in the shape that goes wrong:

```csharp
var all  = await db.TaskItems.ToListAsync();                            // the whole company, in memory
var mine = all.Where(t => access.CanAsync(ctx, "read", For(t)).Result)  // one evaluation PER ROW
              .ToList();
```

So C.1-1 required building the thing being proven: **`BL/Platform/TaskScopeQuery.cs`**, the one place an
`AccessScope` becomes a task query.

### 2.2 The predicates

| Breadth | Predicate | Notes |
|---|---|---|
| `None` | `Where(_ => false)` | An empty **result**, never an unfiltered query — see §2.4 |
| `Own` | `CompanyId == c && (AssigneeEmployeeId == me \|\| CreatedByEmployeeId == me)` | `me` comes from the resolved `BusinessContext` |
| `Team` | `CompanyId == c && (ids.Contains(Assignee) \|\| ids.Contains(CreatedBy))` | `ids` already company-intersected by `IOrgHierarchy` |
| `Company` | `CompanyId == c` | |
| `Branch` | **throws `NotSupportedException`** | See §2.3 |
| `CrossCompany` | **throws `NotSupportedException`** | Cross-company reads belong to `ICompanyIsolationBypass`, which is authorized, reasoned and audited |

`Team` tests the **creator** against the same team set, not against `me`. That is required for agreement with
`CanAsync`: a supervisor who created a task keeps access to it, and because the team set always contains the caller,
one `Contains` covers both "I created it" and "one of my reports created it".

### 2.3 Branch: reported as unsupported by the data, as the brief required

The brief said to prove Branch *"only if actually supported by `TaskItem` data."* **It is not.** `TaskItem` has no
`BranchId` column. A branch-scoped set could only be produced by joining through the *assignee's current branch* —
which is a different rule (where the person works **today**, not where the task belongs) and would silently
reassign visibility whenever someone transfers.

So `Branch` **throws** rather than being handled. Both alternatives are wrong: widening to `Company` grants more
than was asked, narrowing to `Own` grants less and hides the caller's mistake. `TasksAccessService` never returns
`Branch`, so the throw is unreachable in current code and exists to keep it that way.

### 2.4 Why `None` returns `Where(_ => false)` and not `source`

The failure mode this forecloses: returning `source` untouched for an empty scope turns *"sees nothing"* into
*"sees everything"* for any caller that just enumerates the result. Keeping the return type composable also means a
caller that goes on to `.Count()` or `.Skip()` still gets zero rather than the whole table.

A malformed scope — a `Company`/`Team`/`Own` breadth carrying **no** `CompanyId` — throws `InvalidOperationException`
rather than building an unscoped query. An unresolved company reads nothing; it never defaults to one.

### 2.5 The evaluation-count proof

The brief asked for *"a deterministic test proving permission-evaluation count does not grow with row count."*
Timing would be flaky and would not say what broke, so the count is measured directly: a counting
`IPlatformRoleDirectory` wraps the real one. Every permission evaluation — `CanAsync` or `ResolveScopeAsync` —
consults the role directory, so its call count *is* the number of questions asked of the permission system. A
counting `IOrgHierarchy` catches the second degradation mode (re-walking the org tree per row).

| Rows | Role-directory queries | Hierarchy walks |
|---|---|---|
| 10 | **1** | 1 |
| 500 | **1** | 1 |

50× the data, identical permission cost — and the absolute number is **one**, not merely a constant.

A **control test** gives that its meaning: `The_per_row_shape_this_replaces_really_does_cost_one_evaluation_per_row`
drives 12 rows through `CanAsync` and asserts the counter reads exactly **12**. Without it, "the count is 1" could be
true of a path that never authorized anything.

### 2.6 Filtering happens in the database

* `The_scope_predicate_is_translated_into_the_sql_where_clause` — asserts `WHERE` and `CompanyId` in
  `ToQueryString()`.
* `Paging_composes_on_top_of_the_scope_predicate_in_one_sql_statement` — 80 rows, `Skip(5).Take(10)`, and every row
  returned belongs to the caller. Had the predicate been applied in memory, `LIMIT` would have applied to the
  unfiltered set and page 1 would contain other people's rows.
* **SQL Server integration test** (`CrossBuy.Tests/SqlServer/TaskScopeQuerySqlTests.cs`, 4 tests) — the SQLite tests
  prove the right *set*; they cannot prove the predicate is *executed by the database*, and `Contains` translation is
  provider-specific. Runs on the disposable database `SqlServerFixture` creates and drops, gated on
  `CROSSBUY_TEST_SQL`, which **refuses a connection string naming a real CrossBuy database**. A 900-row table is
  filtered to 300 by a server-side `COUNT`, and paging composes onto the same predicate.

### 2.7 Consistency between the two shapes

`Every_row_gets_the_same_answer_from_CanAsync_and_from_the_resolved_set` walks **every row in the table** and asserts
both answers are identical, across three role configurations (bootstrap-open, configured company, supervisor). The
drift it catches is the expensive kind: a list screen showing rows the detail screen then refuses — or worse, rows
the detail screen would also have shown but should not.

**Stated limitation, not hidden.** `CanAsync` additionally applies the **linked-entity gate** (a task about a
`SalesInvoice` must not reveal that invoice to someone who may not see it), asked through
`IPlatformPermissionProvider`. That is cross-module and **cannot** be expressed as a task predicate. So the resolved
set is a **necessary** condition, and a consumer opening a specific task still calls `CanAsync`, which is where the
linked-entity rule applies. The agreement fixtures therefore use tasks with no linked entity.

Also proven: company isolation holds for an administrator (including a task in company 2 carrying the *same*
employee id — the id is not the anchor, the row's company is); a cross-company graft in `Hierarchical` does not widen
the resolved set; a `Worker` context resolves to `None` and the query returns nothing; `manage` resolves to `None`
for a non-administrator instead of falling back to "own tasks".

---

## 3. C.1-2 — SignalR conversation authorization

### 3.1 The hole — and the correction to Batch C's description of it

`ChatHub.JoinConversation(int conversationId)` called `Groups.AddToGroupAsync` with **no check of any kind**:

```csharp
public Task JoinConversation(int conversationId)
    => Groups.AddToGroupAsync(Context.ConnectionId, ConvGroup(conversationId));
```

`[Authorize]` proved the caller was **an** employee. That is authentication; it said nothing about **this**
conversation. Any signed-in employee — or any holder of a mobile JWT — could join the room of **any** conversation
id, including another company's, and from that moment received every message broadcast to it in real time.
`Typing` was equally open: it checked only that an employee id was cached on the connection, so a non-participant
could inject *"X is typing…"* into any conversation — a **write into a room they cannot read**.

> **Correction.** The Batch C report (§6.2) called this *"not a new hole; it is an un-canonicalised one,"* on the
> grounds that `ChatService` still performed its own membership check. That was wrong. `ChatService` guards the
> **REST history fetch**; the **hub group** had no gate at all, and group membership is what delivers live messages.
> The two are different paths. That is precisely why the hole was invisible to a reader of the controller — it
> looked protected while the realtime stream was not. It was a genuine unauthorized-read of message content.

### 3.2 The fix

No SignalR permission engine was created, per the brief. The hub asks the **same canonical service the controller
uses**:

```csharp
_access.CanAsync(ctx, CommunicationActions.Read, PermissionTarget.ForConversation(conversationId))
```

`CommunicationAccessService` already answers this correctly and was already tested: `Read => isMember` — membership
is required **even under bootstrap-open** (a private message is not open-by-default for compatibility); the
conversation **row's** `CompanyID` is compared, never a client value; and absent vs. another-company answer
**identically**, so an id cannot be probed. Owner behaviour is untouched (`Owner` still governs `manage-group`, and a
`CommunicationAdministrator` still does **not** get to read a conversation they are not a member of).

Identity resolution is **session-free**, so the mobile JWT connection behaves exactly as the cookie connection: the
employee comes from the authenticated principal's `NameIdentifier` claim, and the company comes from
`IBusinessContextFactory.ForEmployeeAsync` — the employee's **own row**. That method also returns `null` for an
employee who is inactive or carries no company, which is how a leaver with a still-valid cookie is refused.

Two details worth recording:

* **Rejected joins are silent to the caller and loud to operations.** No exception message, no conversation
  metadata, and above all no group membership — the only thing that would have leaked content. A `Warning` is logged
  naming the connection and the id, so a probing client is visible.
* **`Typing` is gated on the set built by `JoinConversation`**, held per connection in a `ConcurrentDictionary`
  (SignalR may dispatch two invocations of one connection concurrently). You can only type into a room you were
  authorized to join. Reusing the decision keeps a per-keystroke database round trip out of the hot path.
  `LeaveConversation` needs no authorization — dropping yourself from a group exposes nothing — but it removes the
  entry so a later `Typing` cannot ride a stale one.

**No zero-scope lockout risk:** `Conversations` and `ConversationMembers` are **not** among Batch B's pilot
globally-filtered entities, and the company predicate is explicit in the service, so the hub's DI scope state cannot
silently reduce a legitimate participant to zero rows.

### 3.3 Tests — all five cases the brief named, plus three

Driven against the **real hub** with a fake `HubCallerContext` and a recording `IGroupManager`, so what is asserted is
the thing that actually leaked: whether the connection ends up in the group.

| Case | Result |
|---|---|
| Participant joins | Added to the group |
| Non-participant (same company) | **Empty** |
| Cross-company employee — **with a forged membership row** | **Empty** (the conversation row's company decides) |
| Removed member (joined before, membership row deleted) | Joined before, **empty** after |
| Inactive employee with an intact membership row | **Empty** |
| Client-supplied id alone (`4242`, `0`, `-1`, `int.MaxValue`) | **Empty** |
| Employee with no `Employee` row at all | **Empty** — denied, not defaulted |
| `Typing` in a never-joined room | **Nothing broadcast**; a genuine participant still works |

`ChatService`'s own membership checks were left exactly where they are, as defence in depth.

`PosHub` and `NotificationsHub` were swept: neither joins a conversation group. `NotificationsHub` joins per-employee
and per-company groups derived from the resolved employee, `PosHub` a branch group from the account — no
client-supplied room id in either.

---

## 4. C.1-3 — the two legacy cross-company hierarchy walks

`Hierarchical` carries **no `CompanyID`** — it is one shared org tree, which is why Batch B's global filters
deliberately skip it. Per the brief, **no `CompanyID` was added to `Hierarchical`** and it **remains unfiltered at the
EF level**. The intersection is applied at the two call sites.

### 4.1 `CrmAccessService.TeamOwnerIdsAsync`

Any node grafted under a manager's subtree — another company's employee, or a shared position node with children from
two companies — became a "team member". `VisibleOwnerIdsAsync` then handed that id set to CRM as an **owner
whitelist**, so a `SalesManager` read leads, opportunities and accounts **owned by another company's employees**.
Nothing else in the CRM path re-checked the owner's company: the tree was the whole control.

The walk is **not reimplemented**. It now delegates to `IOrgHierarchy.DirectAndIndirectReportsAsync`, which performs
the same cycle-guarded traversal, intersects against `Employee.EmpCompanyID`, and **logs a Warning naming the count it
dropped** — so a mis-grafted tree becomes an operational signal instead of a silent widening.

The legacy company-less signature is preserved (37 `CrmPerm` call sites, `CrmController`, and two `DevSeedController`
assertions still use it) and resolves the company from the **manager's own `Employee` row**. A manager with no company
gets an **empty set**, never company 1.

### 4.2 `LeaveWorkflowService.ManagerChainAsync` — and the trap the fix nearly created

This climb had no company predicate either, and its consequence was worse than a read: the node above an employee's
position could be another company's employee, who then became a **real approver** — written into
`LeaveRequest.CurrentApproverEmployeeID`, notified, shown the request (name, dates, reason) on their approvals
screen, and able to **approve or reject it**.

A foreign node now **stops** the climb rather than being skipped over — continuing upward through it would keep
walking a subtree belonging to someone else's company, so everything above is equally untrustworthy.

**The defect the obvious fix would have introduced.** `CreateAsync` treats an empty chain as *"the requester is at or
above the top of the tree"* and **auto-approves** (`Status = 1`). So filtering the foreign manager out and returning
an empty list would have traded a visibility leak for an **unapproved leave silently granted** — strictly worse.

The two cases are therefore distinguished rather than conflated. `ApproverChainAsync` returns
`ApproverChain(Approvers, HierarchyDefect, DroppedNodes)`; `CreateAsync` refuses the request when the chain is empty
**because of** a defect, and nothing is written. `ManagerChainAsync(int)` keeps its exact signature and delegates, so
no existing caller changed. Cycle protection is preserved (bounded climb + `chain.Contains`), and a requester whose
own row carries no company produces no chain and is reported as a defect — fail-closed.

Per the brief's *"do not silently discard hierarchy defects without operational logging"*: every drop logs a
`Warning` naming the requester, the foreign node, the company, and why the request will be refused.

### 4.3 Tests (real multi-company fixtures)

CRM: the foreign employee is excluded while the real report is kept; the legacy signature reaches the same answer; a
manager with no company gets an empty set.
Leave: the foreign manager is not an approver and `HierarchyDefect` is set with `DroppedNodes == 1`; **`CreateAsync`
refuses and writes no `LeaveRequest` row**; a same-company manager is still a valid approver; a **cyclic** tree
terminates; an employee with no company produces no chain.

*(The cycle fixture seeds parentless and closes the cycle with an `UPDATE` — EF refuses to `INSERT` a self-referencing
FK cycle in one batch. That is also how a cycle arises in reality: someone re-parents an existing node.)*

---

## 5. Corrections to previously reported figures and claims

| Claim | Correction |
|---|---|
| Batch C §6.2: the hub gap is *"not a new hole… an un-canonicalised one"* | **Wrong.** It was a genuine unauthorized read of live message content. §3.1 |
| Batch C §13: *"Two startup defects"* | **Three.** The third — a cycle through `IPlatformPermissionProvider` → adapters → `IEnumerable<IModuleAccessService>` — is the one that mattered most, because it **did not throw**: Kestrel never listened, so there was no error page, no stack trace, no log line, just a browser loading forever and a white page. Fixed with `Func<IPlatformPermissionProvider>` in `TasksAccessService`. `ValidateOnBuild` catches none of the three: it does not recurse through `IEnumerable<T>` — only *resolving* the collection does |
| The white page was attributed to the two new tables being absent from the target database | **Wrong, and wrong in the costly direction** — it pointed at a deployment step instead of at code just written. The tables *are* absent from `CrossBuyDB2` and *do* gate the four proof endpoints, which made it plausible; but a missing table cannot stop the process from listening, because nothing queries it until a module scope is asked for. Root cause was the DI cycle, found by bisecting registrations, confirmed by `GET /Account/Login` → **HTTP 200, 14,247 bytes**, listening in ~6 s |
| *"604 tests passing"* | The actual figure was **607**. Now confirmed arithmetically: C.1 added **38** cases and the suite totals **645** |

The Batch C report was **not** rewritten; §13 carries a pointer here so it remains the record of what was reported at
the time.

---

## 6. Backlog — regenerated, not remediated

**Unchanged: 183.** Mutating **388**, with-attribute **151**, in-body **54**. `388 = 151 + 54 + 183` still
reconciles, and `Stage1PermissionBacklogTests` passes with its pinned figures untouched.

**This is the correct outcome, not an omission.** The backlog counts **controller endpoints**. C.1 touched a
**hub method** and two **service methods** — none of which is an endpoint. Endpoint remediation is Batch D1, and the
brief forbade bulk edits here. Three real defects were closed and the endpoint backlog did not move; reporting a
reduction would be crediting this patch with work it did not do.

What did change is outside the endpoint count:

| Closed | Was |
|---|---|
| `ChatHub` conversation join/typing | Unauthorized — any employee, any conversation, any company |
| `CrmAccessService.TeamOwnerIdsAsync` | Cross-company owner whitelist |
| `LeaveWorkflowService` approver chain | Cross-company approver with decision rights |
| `TaskScopeQuery` | Did not exist; the mapping was a comment |

---

## 7. Maturity — recalculated honestly

**No points awarded** for the new file, the new tests, or the documentation. Per the standing rule, documentation,
classes, schema and screens earn nothing on their own.

Points are claimed **only** for enforcement that is now real and proven on a path that previously had none:

* **+1 Authorization coverage** — realtime conversation delivery moved from *unauthorized* to *authorized by the
  canonical service*, with the eight cases in §3.3.
* **+1 Company isolation** — two live cross-company leaks closed (CRM owner whitelist; leave approver chain), with
  multi-company tests.
* **0 Scalability/architecture** — `TaskScopeQuery` is a genuine capability, but **no production screen consumes it
  yet**. It is a proven seam, and a seam earns its point when something depends on it. Claiming it now would be
  awarding a point for a class.

**Stage 1 is NOT complete.** Critical/High endpoint enforcement (Batch D1) is untouched: `HyperPosController`
`.StampInvoiceCustomer` and the rest of the 183 remain. No completion is claimed.

---

## 8. Files

**Changed (4):**
`CrossBuy/Hubs/ChatHub.cs` · `CrossBuy/BL/CrmAccessService.cs` · `CrossBuy/BL/LeaveWorkflowService.cs` ·
`docs/platform/Stage-001-Batch-C-Delivery-Report.md` (a pointer only)

**Added (4):**
`CrossBuy/BL/Platform/TaskScopeQuery.cs` · `CrossBuy.Tests/BatchC1ScopeQueryTests.cs` (18) ·
`CrossBuy.Tests/BatchC1HubAndHierarchyTests.cs` (16) · `CrossBuy.Tests/SqlServer/TaskScopeQuerySqlTests.cs` (4)

**Mechanically updated (2):** `Stage1PermissionTests.cs`, `Stage1NotificationAuthorizationTests.cs` — one added
constructor argument each, from `CrmAccessService` taking `IOrgHierarchy`.

**Not touched:** no SQL script, no EF migration, no view, no resx, no `Program.cs`, no `CrossDbContext`, no
`IOrgHierarchy` interface, no controller. **No production database was modified and no SQL was executed against
`CrossBuyDB2`.** No `IgnoreQueryFilters`. No accounting, payroll, inventory, POS, billing, task or communication
calculation changed.

**Deployment:** none required. All four changes are code. `PlatformRoleAssignments` and `ProjectMembers` remain
absent from `CrossBuyDB2` and still gate Batch C's four proof endpoints — unchanged by this patch, and still the
operator's call.

---

## 9. Verification

* Application build: **0 errors** (`-c TestRun`, fresh).
* Test build: **0 errors**.
* Suite: **645 total — 599 passed, 0 failed, 46 skipped**, 47 s. The 46 skips are the `CROSSBUY_TEST_SQL`-gated
  SQL Server tests (including this patch's 4), which report **skipped, never silently passed**.
* One failure was found and fixed during the run (`A_cyclic_org_tree_still_terminates` — EF refused to insert a
  self-referencing FK cycle in a single `SaveChanges`; the fixture now closes the cycle in a second pass).

**Not verified by a running instance.** The three changes are covered by tests that drive the real hub and the real
services, but the application was not re-launched for this patch, so realtime chat was not exercised end-to-end in a
browser. Stated rather than implied.

---

## 10. Stopping here

Per instruction: **stop for review after Batch C.1.** Batch D1 (risk-ranked endpoint enforcement, beginning with
`HyperPosController.StampInvoiceCustomer`) has **not** been started, and D2 (Security Administration Console) has
not been started.