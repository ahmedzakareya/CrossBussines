# CrossBuy — System Context Brief (for an AI assistant)

> Purpose: give an AI enough grounding to safely read, reason about, and extend the CrossBuy
> system. Read this **before** touching anything. It encodes the architecture, the data model,
> what already exists, and the non-obvious rules/traps that will break things if ignored.

---

## 1. What CrossBuy is

A bilingual (Arabic/English, RTL) HR / company-management system with **two front-ends sharing one backend + DB**:

1. **ASP.NET Core MVC (.NET 8)** web app — Metronic 8 theme. Two areas:
   - **Admin / HR back-office** (`AdminController`, `Views/Admin/*`) — manage companies, branches, org structure, employees, policies, job titles.
   - **People self-service portal** (`PeopleController`, `Views/People/*`, layout `_LayoutPeople.cshtml`) — employee-facing: dashboard, profile, org structure, leaves. A post-login **portal chooser** (`PortalController` → `Views/Portal/Choose.cshtml`) lets the user pick Admin vs People.
2. **Flutter mobile app** (`crossbuy_mobile/`) — AR/EN with RTL, consumes the same REST API. Mirrors the People portal (dashboard, profile, structure, leaves, notifications).

- **DB**: SQL Server, database name **`CrossBuyDB2`** (local, Windows auth: `sqlcmd -S localhost -d CrossBuyDB2 -E`).
- **ORM**: EF Core 9. **Auth**: ASP.NET Identity (cookie scheme `Identity.Application` for web) + **JWT bearer** for the API/mobile.
- **Real-time**: SignalR hub at `/hubs/notifications`.

---

## 2. ⚠️ Critical rules & traps (read first — these WILL bite you)

1. **EF migrations are BROKEN.** `__EFMigrationsHistory` is empty and the model snapshot is stale.
   **Do NOT run `dotnet ef database update` / `migrations add`** — it errors (2714, duplicate objects).
   **All schema changes are done via manual SQL** (`ALTER TABLE` / `CREATE TABLE`) executed with `sqlcmd`.
   When you add a model property, add the matching column by hand and keep names identical (EF maps by property name).

2. **Visual Studio / IIS Express locks the build output DLL.** To build & run reliably:
   ```bash
   # build to a separate folder
   dotnet build -c Debug -o /c/temp/cb_run
   # run that DLL pointed at the project content root
   dotnet /c/temp/cb_run/CrossBuy.dll --contentRoot "c:\Users\Lenovo\Desktop\CrossBuy\CrossBuy\CrossBuy" --urls http://0.0.0.0:5000
   ```
   If the copy fails with "file is locked by .NET Host (PID …)", kill that PID and rebuild.
   **Run a single server instance on `:5000` for BOTH web and mobile** — SignalR is per-instance, so two
   instances break real-time notifications.

3. **One server, one port.** Mobile emulator reaches the host at `http://10.0.2.2:5000`; web/Chrome at
   `http://localhost:5000` (see `crossbuy_mobile/lib/app_config.dart`). Keep the backend on `:5000`.

4. **Arabic over `curl` on Windows looks like `?????` or mojibake — that's a console/encoding artifact, NOT a data bug.** The DB columns are `NVARCHAR` and store Arabic correctly. Verify Arabic by reading
   through the API as JSON (parsed in Python) or via the app, not raw `sqlcmd`/`curl` console output.

5. **Metronic icon gotcha:** `ki-translate` and `ki-globe` do **not** exist in this Metronic build — use inline SVG instead.

6. **Code references** in chat use markdown links `[file.cs:line](path#Lline)`, not backticks (project convention).

---

## 3. Tech stack & layout

```
CrossBuy/                         (repo root)
├── CrossBuy/                     (ASP.NET Core MVC project — the backend + web)
│   ├── Controllers/              MVC + Api/ (REST)
│   ├── BL/                       business-logic services (interface + impl pairs)
│   ├── Models/Context/           EF entities (Admin/) + CrossDbContext.cs
│   ├── ViewModel/                DTOs / view models
│   ├── Views/                    Razor (Admin/, People/, Portal/, Shared/)
│   ├── Hubs/NotificationsHub.cs  SignalR hub
│   ├── wwwroot/                  Metronic (Backend-assets/), uploads/employees/ (photos)
│   └── Program.cs                DI registration, auth, SignalR wiring
├── crossbuy_mobile/              Flutter app (lib/)
└── docs/                         generated docs (audits, test data, THIS file)
```

**Mobile packages:** Provider (state), Dio (HTTP), fl_chart (charts), signalr_netcore (real-time),
intl, shared_preferences (token). Custom AR/EN i18n in `lib/l10n/app_localizations.dart` with RTL.

**Web:** Razor + Metronic 8 (`wwwroot/Backend-assets/`), **ApexCharts** (bundled in plugins.bundle.js),
**flatpickr** (bundled — used for date pickers), Cairo font for Arabic, RTL via `html[lang="ar"]`.

---

## 4. Data model — the org hierarchy is the heart of everything

### `Hierarchicals` (self-referencing tree) — `Models/Context/Admin/Hierarchical.cs`
- `H_ID` (PK), `H_Parent` (parent H_ID, self-ref), `H_Name` / `H_NameEn`, `H_ObjectID`, `H_Type`.
- **`H_Type`**: `1 = Company`, `2 = Branch`, `3 = Administrative body`, `4 = Position`, `5 = Employee`
  (see `HierarchicalTypes` table).
- **`H_ObjectID`** is the FK into the entity of that type: type 1 → `Companies.CompanyID`,
  type 2 → `Branch.ID`, type 4 → `JobTitle.ID`, type 5 → `Employee.ID`.
- **Tree shape** (this matters for approvals):
  ```
  Unit(1/2/3) → Position(4) → Employee(5) → Position(4) → Employee(5) → …
  ```
  i.e. an employee "sits" in a Position node, whose parent is either a higher manager's Employee node
  or a Unit node. An employee whose Position's parent is a **Unit node** is that **unit's head**.

### Other key entities (`Models/Context/Admin/`)
- **`Employee`** — `ID`, `FullName`/`FullNameEn`, `JobTitleID`, `BranchID?`, `EmpCompanyID`,
  `ProfileImage` (`/uploads/employees/<file>`), `DepartmentID?`, `EmploymentType?`, `IsActive`,
  **`UserId`** (FK → `AspNetUsers.Id`, links employee ↔ Identity login).
- **`Users : IdentityUser`** — adds `IsActive`, `IsEndUser`. (Identity tables `AspNetUsers`, etc.)
- **`Companies`** (PK `CompanyID`), **`Branch`** (PK `ID`, FK `CompanyID`; **no manager field** —
  the branch head is whoever sits in the head Position under the branch node).
- **`JobTitle`** (`ID`, `Title`, `TitleAr`).
- **Policies stack**: `Policies` (a leave/HR policy) → `LeavePolicies` (per-leave-type entitlement
  `EntitlementDaysPerYear`) + `AttendancePolicies` (work-day flags `WorkOnSunday…WorkOnSaturday`,
  work hours, grace, permissions). `PolicyAssignments` maps `EmployeeID → LeavePolicyTypeID (=Policies.ID)`.
- **Leave module**: `LeaveTypes`, `LeaveRequest`, **`LeaveApprovalStep`** (multi-level chain),
  `Notification` (bilingual in-app notifications).

### `CrossDbContext` (`Models/Context/CrossDbContext.cs`)
DbSets incl. `Hierarchicals`, `Employee`, `Companies`, `Branches`, `Policies`, `LeavePolicies`,
`AttendancePolicies`, `PolicyAssignments`, `LeaveTypes`, `LeaveRequests`, `LeaveApprovalSteps`,
`Notifications`.

---

## 5. What has been BUILT (the leave + self-service feature set)

### 5.1 Leave requests & **multi-level approval workflow** — `BL/LeaveWorkflowService.cs` (central)
The single source of truth for create/approve/reject. Both web (`PeopleController`) and API
(`LeaveApiController`) delegate to it.

- **Approver routing (`ManagerChainAsync`)**: from the employee, climb the org tree manager-by-manager
  and **stop at the head of the NEAREST organizational unit** (the first ancestor employee whose
  Position's parent is a Unit node — branch/admin-body/company). That chain = the approval levels.
  - Sequential: level 1 = direct manager … last = the unit head (final approver).
  - **Any rejection stops the whole request immediately.**
  - If the requester **is** a unit head (no manager above) → **auto-approved**.
  - NOTE: `Policies.ApprovalLevels` column exists but is **now unused** — depth is driven by the
    org structure (unit head), not a fixed number. (Earlier design used a fixed count; superseded.)
- **`LeaveRequest`** carries: `Status` (0 pending / 1 approved / 2 rejected), `CurrentLevel`,
  `CurrentApproverEmployeeID` (whose turn it is now). **`LeaveApprovalStep`** rows record each level
  (Level, ApproverEmployeeID, Status, DecisionAt/Note) = full audit trail.
- **Pending-for-me query**: `Status==0 && CurrentApproverEmployeeID == me`.

### 5.2 Working-days counting — `BL/LeaveDashboardService.cs`
- `WorkDayFlagsAsync(empId)` → `bool[7]` indexed by `DayOfWeek` (0=Sun … 6=Sat) from the employee's
  policy `AttendancePolicies`; `null` = no restriction.
- `WorkingDaysAsync(empId, start, end)` counts **only working days** in the range (excludes the policy's
  weekly rest days). Leave duration & balance deduction use this. (No official-holidays table yet.)

### 5.3 Validation rules enforced on create (in `LeaveWorkflowService.CreateAsync`)
1. End ≥ start; valid leave type.
2. **No overlap**: rejects if the date range overlaps ANY existing request that is pending or approved,
   **across all leave types** (can't be on two leaves the same day). Rejected requests don't block.
3. **Working days > 0** (all-rest-day ranges rejected).
4. **Strict balance**: requested working days ≤ remaining balance (`entitlement − approved-used this year`,
   per type). Re-checked again at final approval.

### 5.4 Real-time notifications — `Hubs/NotificationsHub.cs`, `BL/NotificationService.cs`
- Bilingual `Notification` rows + SignalR push to group `emp-{id}`; **15s polling fallback** in clients.
- Fires on submit (→ first approver), each escalation (→ next approver + a progress note to requester),
  and final decision (→ requester). Messages name the **position** of the next approver (e.g.
  "بانتظار موافقة مدير") — they intentionally do **not** say "next level / level N of M".
- Notification `Type`: `leave_submitted | leave_approved | leave_rejected | leave_progress`.
  Mobile routes taps via `AppNav.routeForNotification`.

### 5.5 Dashboards / profile / structure
- People dashboard + mobile dashboard: balances per leave type, request stats, donut (by type),
  6-month trend, month calendar with leave days highlighted (web ApexCharts; mobile fl_chart).
- Profile: real status/department/employment/activity/documents/stats (all DB-backed).
- Org structure view renders real images (company logo, branch logo, employee photos) from `wwwroot`.

### 5.6 Mobile freshness (important behavior)
The bottom nav uses an **`IndexedStack`** (all screens stay alive → `initState` runs once). To avoid
stale views, there is a global signal **`AppNav.dataChanged`** (bumped by `ApiService` after any
successful create/decide) that Dashboard/Leaves/Profile listen to; they also reload on tab-focus and on
incoming notifications. Keep this pattern when adding data screens.

---

## 6. REST API surface (all under `/api`, JWT bearer unless noted)

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/login` (body `{userName, password}` → `{token}`), `GET /api/auth/ping` |
| Me | `GET /api/me/profile`, `/me/dashboard`, `/me/documents`, `/me/activity`, `/me/position`, `/me/structure` |
| Leave | `GET /api/leave/types`, `/leave/workdays`, `/leave/my`, `/leave/pending`; `POST /api/leave` (create); `POST /api/leave/{id}/decision` (`{approve:bool, note}`) |
| Notifications | `GET /api/notifications`, `POST /api/notifications/{id}/read`, `POST /api/notifications/read-all` (cookie **or** JWT) |
| HR | `GET /api/hr/summary`, `/hr/jobtitles` (+POST/PUT), `/hr/companies`, `/hr/branches` |
| Employees | `GET /api/employees/{id}` |
| Dev (⚠ dev-only, key-guarded) | `GET /api/dev/seed-users?key=seed123`, `/dev/seed-test-org?key=seed123`, `/dev/seed-dept-heads?key=seed123` |

---

## 7. Auth & test accounts

- Web login via `AccountController`; API via `AuthApiController` (returns JWT). Employee resolved from the
  Identity user by `Employee.UserId == AspNetUsers.Id` (`EmployeeService.GetEmployeeByUserIdAsync`).
- **Test fixtures** (passwords are hashed in `AspNetUsers`; plaintext known only because the seeders set them):
  - **Test branches org** (seeded via `/api/dev/seed-test-org` + `/api/dev/seed-dept-heads`), password `Test@1234`:
    `cairo.manager` (branch head) / `cairo.dept` (dept head) / `cairo.staff`;
    `alex.manager` / `alex.dept` / `alex.staff`.
    Structure: Company → Cairo/Alex Branch → Branch Manager → Department Head → Employee
    (so an employee's chain = Dept Head → Branch Manager).
  - **Legacy team** (`/api/dev/seed-users`): `Admin`/`Admin@123` (= employee "Ahmed", company head),
    `sara.ali`/`Sara@123`, `khaled.hassan`/`Khaled@123`.
  - Full details in `docs/CrossBuy_Test_Data_v2.docx`.

---

## 8. How to build / run / test

```bash
# Backend (see trap #2):
dotnet build -c Debug -o /c/temp/cb_run
dotnet /c/temp/cb_run/CrossBuy.dll --contentRoot "<repo>/CrossBuy/CrossBuy" --urls http://0.0.0.0:5000

# DB schema change (migrations are broken — use raw SQL):
sqlcmd -S localhost -d CrossBuyDB2 -E -i "C:\path\to\change.sql"

# Mobile (Flutter SDK at /c/src/flutter/bin/flutter), emulator "crossbuy_pixel" / emulator-5554:
/c/src/flutter/bin/flutter run -d emulator-5554

# Verify API/Arabic correctly (avoid raw curl-console for Arabic):
TOK=$(curl -s -X POST http://localhost:5000/api/auth/login -H "Content-Type: application/json" \
  -d '{"userName":"cairo.staff","password":"Test@1234"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
curl -s http://localhost:5000/api/leave/my -H "Authorization: Bearer $TOK"
```

---

## 9. Known gaps / candidates for the "rest of the system" analysis

- **Attendance / check-in-out (geolocation-bound)** — discussed (GPS geofencing, Wi-Fi/IP, BLE, QR/NFC,
  biometric terminals, etc.) but **not built**; planned after app deployment.
- **Official holidays** — no `Holidays` table yet; leave currently excludes only weekly rest days.
- **EF migrations** are unusable — a real fix would re-baseline migrations against the live schema.
- **`Policies.ApprovalLevels`** column is dead (kept for safety) — approval depth is org-structure-driven.
- **HR back-office** (AdminController + Views/Admin) is large and mostly outside the recent leave work —
  a prime area to audit next (employees, policies, structure editors).
- Other API controllers (`HrApiController`, `EmployeesApiController`) are thin and may need expansion.

---

## 10. File map (most relevant)

**Backend services** (`BL/`): `LeaveWorkflowService` (approval chain), `LeaveDashboardService`
(balances + working days), `NotificationService`, `EmployeeService`, `PolicesService`,
`AdministrativeStructureService`, `CompanyService`, `JobTitleService`, `TokenService`, `UserService`.

**Controllers**: `PeopleController` (self-service web), `AdminController` (HR back-office),
`PortalController` (chooser), `AccountController` (web auth);
API: `AuthApiController`, `MeApiController`, `LeaveApiController`, `NotificationsApiController`,
`HrApiController`, `EmployeesApiController`, `DevSeedController`.

**Entities** (`Models/Context/Admin/`): `Hierarchical`, `Employee`, `Users`, `Companies`, `Branch`,
`JobTitle`, `Policies`, `LeavePolicies`, `AttendancePolicies`, `PolicyAssignments`, `LeaveTypes`,
`LeaveRequest`, `LeaveApprovalStep`, `Notification`.

**Web views** (`Views/People/`): `Dashboard.cshtml`, `Profile.cshtml`, `Structure.cshtml`,
`Leaves.cshtml`; layout `Views/Shared/_LayoutPeople.cshtml`; chooser `Views/Portal/Choose.cshtml`.

**Mobile** (`crossbuy_mobile/lib/`):
- `main.dart`, `app_config.dart` (API base URL), `l10n/app_localizations.dart` (AR/EN, RTL)
- `screens/`: `home_screen` (IndexedStack + bottom nav), `dashboard_screen`, `profile_screen`,
  `structure_screen`, `leave_screen`, `notifications_screen`, `login_screen` (`statistics_screen` is orphaned)
- `services/`: `api_service.dart` (Dio + token), `app_nav.dart` (tab nav + `dataChanged` signal)
- `providers/`: `auth_provider`, `locale_provider`, `notification_provider` (SignalR + poll + toast)
- `models/`: `employee`, `dashboard`, `leave` (incl. `ApprovalStep`), `app_notification`, `org_node`, etc.
- `widgets/`: `app_ui.dart` (design system: colors, `SectionCard`, `AppHeader`…), `charts.dart`,
  `in_app_toast.dart`

---

*Generated as a handoff brief. Treat section 2 (traps) as binding constraints when making changes.*
