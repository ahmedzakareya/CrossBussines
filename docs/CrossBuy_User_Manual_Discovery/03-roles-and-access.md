# 03 — User roles and access

Who can do what, as the product declares it.

Machine-readable: `role-permission-matrix.csv` (73 rows), `route-protection.csv` (1,220 routes),
`reference/access-summary.json`.

---

## ⚠ The limit on this whole chapter

**Every row below is repository-verified. None is runtime-verified.**

The discovery ran on a single administrator session. Creating a second user, or granting a role to
test it, would have changed data — which the brief forbids. So this chapter describes the access
model *as written*, and cannot describe what any particular user actually sees.

> **Do not infer complete access control from an administrator session.** An administrator passes
> most checks, so the screens that appear to "work" for them say nothing about a warehouse keeper.

Document 12 lists exactly what must be observed before the manual's role-based quick starts can be
written with confidence.

---

## 1. Access is three layers, not one

```
  1. IDENTITY ROLE        AspNetRoles — SuperAdmin, Administrator, Admin, Auditor, PlatformOps
         │                the framework-level role attached to the sign-in
         ▼
  2. MODULE ROLE          InventoryUserRoles, AccountingUserRoles, CrmUserRoles, BranchUserRoles
         │                per company/branch, granted on each module's own "roles" screen
         ▼
  3. PLATFORM GRANT       PlatformRoleAssignments — scope × role, the kernel's own registry
                          checked for cross-module capabilities
```

The layers **intersect**: a capability needs the module layer *and*, where the platform kernel is
involved, the grant layer. Passing one is not enough.

### 1.1 A measured consequence

On the development install: **`PlatformRoleAssignments` contains zero rows.**

Anything gated on a platform grant is therefore closed for everyone on that install — including the
administrator. This is the mechanism behind the HR refusal described in document 09: HR's
`employee-manage` is in the platform's `NeverBootstrapOpen` set, so it does **not** fall open when
no role has been granted. It refuses until someone grants it deliberately.

For the manual this is a first-run instruction, not a bug: **a fresh install must grant HR roles
before an employee can be saved.**

---

## 2. Identity roles found in the database

| Role | Observed |
|---|---|
| `SuperAdmin` | present |
| `Administrator` | present |
| `Admin` | present |
| `Auditor` | present |
| `PlatformOps` | present |

Runtime viewed (`SELECT Name FROM AspNetRoles`). Five roles exist; what distinguishes
`SuperAdmin` from `Administrator` from `Admin` was **not established** — the names suggest a
hierarchy that no single source confirms. This must be settled before the manual describes them.

`PlatformOps` is the exception, because its meaning is pinned in code:
`PlatformOpsAttribute.IsAdmin(context) || accounting-manage`. It gates the Business Event Monitor,
and the sidebar hides that row using the *same* predicate the screen enforces.

---

## 3. Module roles — declared

| Module | Declared roles | Where |
|---|---|---|
| **HR** | `HrManager`, `HrOfficer`, `PayrollOfficer`, `HrViewer` | `HrRoles`, typed constants |
| **Projects** | `ProjectsAdministrator`, `ProjectsFinance`, `ProjectsViewer` | `ProjectsRoles` |
| **Tasks** | `TasksAdministrator`, `TasksSupervisor`, `TasksViewer` | `TasksRoles` |
| **Communication** | `CommunicationAdministrator`, `AnnouncementPublisher`, `OutboxOperator` | `CommunicationRoles` |
| **Calendar** | `CalendarAdministrator`, `CalendarSupervisor` | `CalendarRoles` |
| **Inventory** | `InventoryManager`, `PurchasingOfficer`, `WarehouseKeeper`, `Auditor` | string literals in `InventoryAccessService` |
| **Accounting** | `ChiefAccountant`, `Auditor` | string literals in `AccountingAccessService` |
| **CRM** | `SalesManager`, `SalesRep`, `CrmViewer`, `Marketing` | literals + observed assignments |
| **POS / Restaurant** | **none found** | see §3.2 |

### 3.1 Roles actually assigned on the development install

Runtime viewed:

| Table | Role | Assignments |
|---|---|---|
| `InventoryUserRoles` | `InventoryManager` | 1 |
| `AccountingUserRoles` | `ChiefAccountant` | 1 |
| `CrmUserRoles` | `SalesRep` | 2 |
| | `SalesManager` | 1 |
| | `Marketing` | 1 |
| | `CrmViewer` | 1 |
| `PlatformRoleAssignments` | — | **0** |

`Marketing` appears as an assignment but is not among the roles the CRM service declares in the
code that was read. Either it is declared somewhere not found, or it is a stale assignment. **Not
verified** — worth checking before the CRM chapter names its roles.

### 3.2 POS has no role vocabulary

The Restaurant module has a **Cashier roles** screen (`/Pos/CashierRoles`) and a
`BranchUserRoles` table, but no `PosRoles` constant list and no comment vocabulary were found.
The permitted cashier roles are therefore **not established from source**. The screen itself will
list them — capture it and read them off, which document 12 records as an outstanding task.

---

## 4. Action vocabularies — what each module can gate

| Scope | Actions |
|---|---|
| **Inventory** | `read` · `doc` · `purchase` · `manage` |
| **Accounting** | `read` · `post` · `pay` · `manage` · `currency-override` · `period-close` · `period-reopen` |
| **CRM** | `read` · `edit` · `manage` |
| **HR** | `read` · `employee-view` · `employee-manage` · `attendance-manage` · `leave-manage` · `leave-approve` · `payroll-view` · `payroll-manage` · `organization-manage` · `performance-manage` · `confidential-view` · `employee-request` |
| **Projects** | `read` · `create` · `edit` · `manage` · `budget-view` · `budget-manage` · `billing` · `billing-prepare` · `billing-approve` · `billing-post` · `close` |
| **Tasks** | `read` · `create` · `edit` · `assign` · `reassign` · `complete` · `reopen` · `manage` |
| **Communication** | `read` · `send` · `create-group` · `manage-group` · `announcement-send` · `outbox-manage` |
| **Calendar** | `read` · `create` · `edit` · `manage` |
| **Insight (AI)** | `InventoryItem` · `CrmOpportunity` · `CrmAccount` |

### 4.1 Reading these correctly

**HR and Projects are the finely-grained ones**, and their granularity is meaningful:

- HR separates `payroll-view` from `payroll-manage`, and isolates `confidential-view` — so
  somebody can run HR without reading confidential notes.
- Projects separates the billing chain into `billing-prepare` → `billing-approve` →
  `billing-post`. **That is segregation of duties expressed in permissions**, and it is the one
  place in the product where the money chain is split three ways. The manual should make this
  explicit: the person who prepares a progress billing cannot also approve and post it unless they
  hold all three.
- Accounting isolates `period-close` and `period-reopen`, and `currency-override`.

**Inventory, Accounting and CRM declare their words in a code comment**, not as typed constants:

```csharp
Task<bool> CanAsync(string action);   // read | doc | purchase | manage
```

The words are real and the checks use them, but a rename cannot be caught by the compiler. The
matrix marks these rows `(comment only)`.

### 4.2 Eleven platform scopes

`Accounting`, `Calendar`, `Communication`, `Crm`, `Hr`, `Inventory`, `Manufacturing`, `Pos`,
`Projects`, `Tasks`, and `None`. Manufacturing and Pos have a scope but no published vocabulary —
consistent with Manufacturing being labelled *work in progress* in the portal.

---

## 5. Route protection — measured

1,220 routable actions were read with the attributes guarding each
(`route-protection.csv`).

| | Count |
|---|---:|
| Routable actions | 1,220 |
| Explicitly anonymous (`[AllowAnonymous]`) | **17** |

**Seventeen anonymous endpoints** is a small, checkable number — sign-in, password recovery, the
public storefront, health. The manual's security note can state plainly that everything else
requires a session.

Guards observed on controllers and actions include `[Authorize]`, `[SessionValidation]`,
`[PlatformOps]`, `[ApiController]` + JWT, and `[AllowAnonymous]`. The per-route detail is in the
CSV; it is the right place to answer "why can this person not open that screen".

---

## 6. Company and branch scope

- **Your company comes from your employee record** (`Employee.EmpCompanyID`), resolved through
  `BusinessContextFactory`. It is not chosen at sign-in and there is no company switcher. Working
  in a second company means a second employee record.
- If a signed-in employee has no company, the product refuses rather than defaulting — the source
  says *"there is no default"*.
- **Branch** is a second axis, with its own `BranchUserRoles` table and per-branch POS settings.
- Most data is filtered by company in the data layer, not in the UI. Four accounting tables
  (`Account`, `CostCenter`, `BankAccount`, `CashBox`) and the bill-of-material line
  (`ItemComponent`) sit outside that global filter and are filtered explicitly instead.
- One deliberate cross-company exception: the organisation-structure report
  (`admin.orgstructure.reports.view`). The tree has no company column, so the report shows every
  company's subtree to anyone holding that key — documented in the source as an intentional
  decision, with narrowing left to an explicit root-node parameter.

**For the manual:** the permission `admin.orgstructure.reports.view` must be described as a
cross-company grant. It is the only one.

---

## 7. Built-in versus configurable

| Built in, not configurable | Configurable by an administrator |
|---|---|
| The five identity roles | Which employee holds which module role |
| The action vocabularies (the words themselves) | Platform grants (scope × role) |
| Which route each attribute guards | Warehouse access per user |
| The intersection rule between layers | Branch and POS/cashier roles |
| `NeverBootstrapOpen` membership | Report permission keys → roles |

The per-module "roles" screens — `/Inventory/InventoryRoles`, `/Accounting/AccountingRoles`,
`/Crm/CrmRoles`, `/Pos/CashierRoles` — are where the configurable half is administered. Each was
reached and rendered; see `screenshot-manifest.csv`.

---

## 8. What a role-based quick start still needs

The brief asks each role to be documented with its modules, navigation, allowed actions, approval
responsibilities and company/branch scope. Four of those six can be written now from the matrix.
Two cannot:

| Needed | Status |
|---|---|
| Who each role serves | ⚠ inferable from the name; **not stated anywhere in source** |
| Modules and navigation visible | ❌ requires signing in as that role |
| Allowed actions | ✅ from the matrix |
| Approval responsibilities | ✅ four silos — Leave, Request, Inventory, ProjectBilling (document 06) |
| Company / branch scope | ✅ employee record + branch roles |
| Restrictions and prerequisites | ⚠ partial — the platform-grant prerequisite is known; per-screen prerequisites are in `screen-catalog.json` |

**The blocking item is the second row**, and it needs one thing: a set of test accounts, one per
role, on the development install. That is a data change and was therefore not made.
