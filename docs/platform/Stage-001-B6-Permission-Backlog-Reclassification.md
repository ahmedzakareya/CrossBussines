# Stage 1 Batch B / B6 — permission backlog, regenerated and reclassified

**Evidence:** `docs/architecture/evidence/Permission-Backlog-B6.csv` (the 193 rows) ·
`Permission-Coverage.csv` (all 1055 actions) · basis corrected by **CORRECTION-004**.
**B6 does not remediate anything** — remediation is Batch D. B6 establishes what the backlog *is*, and on what basis.

---

## 1. Headline

| | Count |
|---|---|
| Actions scanned | 1055 |
| Mutating (POST/PUT/DELETE/PATCH) | 384 |
| Protected by a module-permission attribute at any level | 151 |
| Authorized in the method body by an access service | 40 |
| **Backlog — no authorization any scan can see** | **193** |

The figure changed from the accepted 189: see CORRECTION-004. Four POS actions were credited to a lane guard that
checks no role.

## 2. The axis that actually matters: authentication vs authorization

Reading "193 unprotected mutating actions" as "193 anonymous endpoints" would be badly wrong. `Program.cs` registers
`SessionValidationMiddleware` **globally**, and it requires the `"Employee"` session for every path EXCEPT:

`/pos/*` (cashier lane) · `/hyper/pos` (hyper lane) · `/home/store` + `/store/*` (public storefront) ·
**`/api/*`** · `/swagger` · `/hubs` · `/account/{login,logout,setlanguage}`

So the backlog splits into two very different populations:

| Reachability | Rows | What the gap means |
|---|---|---|
| **Authenticated** — behind the global session gate or a class-level `[Authorize]` | **192** | Any signed-in employee may invoke it, regardless of role. **Privilege escalation inside a company**, not exposure. |
| **Anonymous by design** | **1** | `AuthApiController` login (JWT issuance). Correct as-is. |

That is the honest severity frame, and it is why nothing here is titled "unauthenticated write".

## 3. Risk classification

### Tier 1 — Critical (10 actions) — already assigned to Hotfix A.1

`AccountingApiController`, class-level `[Authorize(JwtBearer)]`, route `api/acc`, **no module permission**, and
`int companyId = 1` taken **from the request** on at least 10 mutating actions including `PayrollPost`.

* An authenticated JWT holder — any employee with a mobile token — can post to the GL, and can name **another
  company** by passing `?companyId=2`.
* It is on `/api/*`, so `SessionValidationMiddleware` does not apply; only the JWT does.
* **B4 side effect, reported and NOT claimed as the fix:** the write guard now refuses a cross-company insert of a
  pilot entity, which incidentally blocks the `JournalEntry` write path of `PayrollPost` when the token's scope and
  the posted `companyId` disagree. The *authorization* hole — no module permission on a GL-posting endpoint — is
  untouched. Hotfix A.1 remains outstanding in full.

### Tier 2 — High (60 actions) — administration reachable by any signed-in employee

| Controller | Rows | What is administered |
|---|---|---|
| `AdminController` | 42 | employees, companies, branches, hierarchicals, HR policy tables |
| `TasksController` | 13 | task creation/assignment/closure |
| `PeopleController` | 5 | employee records |

All behind the global session gate (they carry **no** class-level attribute at all, so the middleware is their only
guard) and none carries a module permission. A clerk can reach employee and company administration.

### Tier 3 — Medium (74 actions) — module writes with a session but no permission

`PosController` 42 (POS *setup/back-office* screens, behind the Employee gate), `ProjectController` 30,
`InventoryController` 1, `AccountingController` 2 (the two without an `AccPerm`). Module-scoped damage, authenticated
actor, no role check. `ProjectController` and the HR/Projects services have **no access service to call** — that
absence is exactly what Batch C was scoped to create.

### Tier 4 — Medium (45 actions) — communication / collaboration / AI

`ChatController` 9, `FileManagerController` 5, `AiController` 4, `CommController` 4, `AnnouncementsController` 3,
`BrandController` 3, `CalendarController` 2, `CommentsController` 2, `NotificationsApiController` 2,
`NotificationsController` 1, `ServiceController` 2, `HrApiController` 2, `LeaveApiController` 2, plus 4 others.

**Mostly the parallel team's feature modules.** Their conventions are theirs (ADR/PKS: thin controllers with
`[SessionValidation]`), so Batch D must coordinate rather than unilaterally attribute their actions.

### Tier 5 — Low / correct as-is (4 actions)

`AccountController` 2 (login/logout — anonymous by design), `AuthApiController` 1 (JWT issuance),
`HyperPosController.PriceCheck` 1 (a price lookup declared POST; POS session required).

## 4. Antiforgery, measured alongside

36 of the 193 are MVC (non-API) mutating actions with **no** `[ValidateAntiForgeryToken]` **and** no permission —
the compounding case: a CSRF-reachable state change that also performs no role check. Of the 384 mutating actions
overall, 325 carry antiforgery and 38 MVC actions do not.

## 5. What B6 deliberately did NOT do

* **No attribute was added to any action.** The standing instruction is explicit — the backlog is not to be modified
  in bulk, and a permission attribute applied without knowing the intended role is a guess that will be trusted.
* **No maturity score moved.** The backlog was already counted as a gap; 193 vs 189 crosses no 10-point step.
* **No remediation ordering was promised.** Tiers are a risk *classification*; the sequencing decision is Batch D's,
  and Tier 3/4 both depend on services that do not exist yet (Batch C).

## 6. Reproducing this

```powershell
cd CrossBuy
powershell -NoProfile -ExecutionPolicy Bypass -File .\deploy\scan-architecture.ps1
```

The three-category summary prints the 151 / 40 / 193 split. `Stage1PermissionBacklogTests` re-derives the same
numbers from the CSV in the test suite, so the documents and the evidence cannot drift apart silently.
