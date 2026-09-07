# Stage 1 B3 — `Notification.CompanyID` NULL policy

**Status:** Decided (required before B2 by instruction). **Enforced by:** B2's global query filter, via
`NotificationCompanyPolicy`. **Related:** ADR-023 (bypass), ADR-006 (notification projection), ADR-022 (context).

---

## 1. Why there is a decision to make

`Notification.CompanyID` is **`int?`** (`Models/Context/Admin/Notification.cs:24`). Every other entity in the B2
pilot has a NOT NULL `CompanyID`, so for them "the row's company" is always a fact. Here it can be absent.

A nullable column under a company filter has three possible readings, and writing the filter without choosing one
**chooses one by accident** — which is the actual risk this document exists to prevent.

| Reading | Behaviour | Verdict |
|---|---|---|
| **(a) Infer** — a NULL row belongs to whoever is reading it | One NULL row becomes visible to **every** company simultaneously | **Rejected.** It manufactures ownership from an absence — the same class of mistake as the company-1 fallback Batch A deleted: a missing fact silently becoming a convenient one. |
| **(b) Expose** — leave NULL rows outside the filter | Everyone sees them | **Rejected.** A cross-company read with no bypass, no authorization and no audit line. |
| **(c) Exclude** | A NULL row belongs to no company, so it is invisible to a company-scoped request; visible only under an authorized cross-company bypass | **ADOPTED.** |

## 2. The adopted policy (c), stated exactly

1. A `Notifications` row with `CompanyID IS NULL` is **not owned by any company** and is therefore **not visible to
   any company-scoped request**.
2. It becomes visible **only** under a bypass whose kind `AllowsCrossCompany` — where the operator sees it for what
   it is (an unattributed legacy row), was authorized, and is audited.
3. Ownership is **never inferred** — not from the reader's company, not from the recipient's company, not from the
   actor's. Resolution from `Employee.EmpCompanyID` is a *deliberate data-migration step* (§5), not something a
   read path does silently.
4. An **unresolved** scope (no company resolved at all) reads **no** notifications. It is denied, never widened.

### 2.1 The cost, stated plainly

If such rows existed, their recipients would stop seeing them. That is a real regression, which is why §5 is a
*resolution* task rather than a "monitor it" note.

### 2.2 Measured, not assumed

Read-only count against `CrossBuyDB2`, 2026-08-03:

```
SELECT TotalRows = COUNT(*), NullCompany = SUM(CASE WHEN CompanyID IS NULL THEN 1 ELSE 0 END)
FROM dbo.Notifications;
-- 1271, 0
```

**1271 rows, 0 with `CompanyID IS NULL`.** Nothing was written to obtain that number. So adopting (c) changes
nothing for any existing row today — the cost in §2.1 is a future risk, not a present regression.

The tests exist anyway. "There is no such data right now" is not a rule: `INotificationService.NotifyAsync` still
accepts `companyId: null` (it is an optional parameter), so the case is reachable by any caller that omits it, and
the behaviour must be **pinned** rather than left to whichever filter expression happens to get written.

## 3. Implementation — and the trap inside it

`BL/Platform/NotificationCompanyPolicy.cs`:

```csharp
public static Expression<Func<Notification, bool>> QueryFilter(ICompanyScopeHolder scope)
    => n => scope.AllowsCrossCompany
         || (scope.CompanyId != null && n.CompanyID == scope.CompanyId.Value);
```

The `CompanyId != null` guard and the `.Value` are **not decoration**. Written the obvious way —

```csharp
n => scope.AllowsCrossCompany || n.CompanyID == scope.CompanyId    // WRONG
```

— with a nullable on **both** sides, EF Core emits a **null-safe** comparison (`CompanyID = @p OR (CompanyID IS
NULL AND @p IS NULL)`). An unresolved scope, where `@p` is NULL, would then match **exactly the legacy NULL rows** —
handing precisely the unattributed data to precisely the request that could not prove who it was. The inverted
worst case of the intended policy.

With the guard, the comparison is against a non-nullable value, so it becomes `CompanyID = @p` and exclusion (c)
**falls out of SQL's own three-valued logic** rather than needing a special case.

`IsVisible(int? rowCompanyId, ICompanyScopeHolder scope)` is the same rule as a plain function, kept in the same
file so the two cannot drift.

## 4. Tests

In `CrossBuy.Tests/Stage1BypassTests.cs` (all passing, SQLite and SQL Server):

| Test | Proves |
|---|---|
| `A_null_company_notification_is_invisible_to_a_company_scoped_request` | (c) for two different companies — ownership is not inferred from the reader |
| `A_null_company_notification_is_visible_only_under_an_authorized_bypass` | it appears under `PlatformMonitoring` and disappears again when the lease ends |
| `The_null_company_row_is_excluded_by_the_real_predicate_in_the_database` | the predicate **translated to SQL** excludes both another company's row and the NULL row; the bypass returns all three |
| `An_unresolved_scope_reads_no_notifications_at_all_in_the_database` | the §3 trap. This is the assertion that **fails** against the naive formulation |
| `The_B2_predicate_denies_an_unresolved_scope_rather_than_widening_it` | in-memory form of the same rule |

## 5. Backlog — B3-BACKLOG-1: resolve legacy NULL-company notifications

Not done in B3, deliberately: it is a data migration with its own verification, and B3's remit is the bypass. Doing
half of it — a backfill without closing the source — would simply recreate the rows.

Two parts, **in this order**:

1. **Close the source.** Make `CompanyID` mandatory on the write path. `NotifyAsync`'s `companyId` is optional
   today; `NotificationProjectionConsumer` always supplies it (from the event's company), but direct callers may
   not. Audit the direct-`NotifyAsync` producers listed in ADR-006's retained-producers table, then make the
   parameter required.
2. **Resolve existing rows** from `Employee.EmpCompanyID` of the recipient — a **resolution from a real fact**,
   never a default — as an idempotent script in `deploy/sql`, with a before/after count. Only then consider
   `NOT NULL` on the column.

Blocked on neither B2 nor B4; it can be scheduled independently. Owner: hypermarket track. Currently affects
**0 rows**, so it is not urgent — it is a *guard against the first row that appears*.