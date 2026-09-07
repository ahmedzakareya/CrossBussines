# 07 — Org Hierarchy and HR

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## The reporting line is not on Employee

`Models/Context/Admin/Employee.cs` has **no** `ManagerId`, `DirectManager` or `ReportsTo` column
(verified: zero matches). The reporting line lives entirely in the **`Hierarchical` tree**.

`Models/Context/Admin/Hierarchical.cs` — the complete entity:

```csharp
public int  H_ID       // PK
public string? H_Name  // Arabic
public string? H_NameEn
public int? H_Parent   // self-referencing
public string? H_Notes
public int? H_Type     // node type
public int? H_ObjectID // related object id
```

**`Hierarchical` has no `CompanyID`.** The org tree is a single global structure shared by all 14
companies. That is the root cause of the cross-company manager problem: nothing in the tree itself
says which tenant a node belongs to.

`H_Type == 5` means an **employee node**, with `H_ObjectID = Employee.ID`
(`OrgHierarchy.cs:48` — "LeaveWorkflowService … both filter H_Type == 5 with H_ObjectID = Employee.ID").
The convention is employee node → child position nodes → child employee nodes → …, so a manager is the
**first `H_Type == 5` ancestor**, not the immediate parent (`OrgHierarchy.cs:132`).

## `IOrgHierarchy` — `BL/Platform/OrgHierarchy.cs`

| Method | Purpose |
|---|---|
| `DirectAndIndirectReportsAsync(managerId, …)` | full downward subtree; **always includes the caller** |
| `DirectManagerAsync(employeeId, …)` | first `H_Type == 5` ancestor |

### Cycle and depth protection — two guards, deliberately

| Guard | Where | Note |
|---|---|---|
| `visited` set | lines 80/93 (down), 158/173 (up) | "the tree is user-maintained" |
| Step/hop bound | `MaxHops = 64` line 168 | "stops the walk EVEN IF the visited set is ever weakened" |

The file states the reasoning: *"Every other defect here is a wrong answer, which a test catches; an
unbounded walk is a hang, which it does not."*

## How cross-company leakage is prevented — by the callers, not the tree

Because `Hierarchical` carries no company, every consumer must re-filter. `TaskEscalationService.cs:205–222`
does this explicitly and explains why:

> *"Deliberately `Employee.EmpCompanyID` and not the entity registry: **TM-2 means the registry does
> not company-filter Employee, so `BelongsToCompanyAsync` would answer yes for anybody**."*

It also records the reason the check is needed at all: *"rows the generator writes directly, which
never go through SaveAsync, are not covered"* by the Phase-1 save-time guard.

## TM-2, documented accurately (not fixed)

`PlatformPermissionProvider.BelongsToCompanyAsync` (line 121) delegates to
`IEntityRegistry.ResolveAsync` and returns `resolved.Found`. Its own comment explains the intent —
"Found = false means the row does not exist in the caller's company … the caller must not be able to
tell those apart" — which is correct **for entities the registry company-filters**.

**Employee is not one of them.** So for `entityCode == "Employee"` the method answers `true` for an
employee of any company. `TaskEscalationService` is the only place found that works around this
explicitly. Consumers that call `BelongsToCompanyAsync` for Employee without their own
`EmpCompanyID` filter would inherit the hole.

Recorded as finding **F-03**. Not fixed here.

## HR: implemented vs missing

| Capability | Evidence | Status |
|---|---|---|
| Employee master | `Models/Context/Admin/Employee.cs` | implemented |
| Org tree | `Hierarchical` + `IOrgHierarchy` | implemented, **no company column** |
| Attendance | `BL/AttendanceService.cs` (raises events) | implemented |
| Leave | `BL/LeaveWorkflowService.cs`, `LeaveApprovalStep` | implemented |
| Employee requests | `BL/EmployeeRequestService.cs` | implemented |
| Appraisals | `BL/AppraisalService.cs`, `AdminController.Appraisals.cs` | implemented |
| Recruitment | `AdminController.Recruitment.cs` | implemented |
| Training | `AdminController.Training.cs` | implemented |
| Onboarding | `Controllers/EmployeeOnboardingController.cs` | implemented |
| Roster | `Controllers/RosterController.cs`, `Reporting/HrRosterDatasets.cs` | implemented + reportable |
| HR API | `Controllers/Api/HrApiController.cs`, `LeaveApiController.cs` | implemented |
| **Payroll** | no service, no entity found | **not present** |
| **Manager column on Employee** | — | **not present by design** (tree instead) |
| **Employee active-state check on task assignment** | — | **not found on any path** |
