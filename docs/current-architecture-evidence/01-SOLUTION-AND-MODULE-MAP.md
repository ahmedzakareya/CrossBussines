# 01 — Solution and Module Map

> Read-only evidence. Refs: `master` = 60dc115, `tasks/phase4` = 7c6fcea.


## Solution

```
CrossBuy.sln
├── CrossBuy/                     ASP.NET Core 8 MVC — the entire application. One project.
│   ├── Controllers/       57     MVC + 16 under Api/
│   ├── BL/               306     application + domain services, no separate domain project
│   │   ├── Platform/             the kernel: BusinessContext, events, permissions, registry
│   │   ├── Platform/Orchestration/   Phase 3–4 only (absent on master)
│   │   ├── TasksCalendar/        task + calendar services
│   │   ├── Reporting/     42     Report Studio + Reports Center
│   │   ├── Documents/            document platform
│   │   ├── Communication/        threads, mentions, comm outbox
│   │   ├── Approvals/            read-only inbox over three mechanisms
│   │   └── Uat/                  dataset seeder
│   ├── Models/Context/   106     EF entities, grouped by module folder
│   ├── Views/            361     Razor, runtime-compiled
│   └── wwwroot/
├── CrossBuy.Tests/       194     xUnit
├── CrossBuy.Analyzers/     8     Roslyn authorization analyzers (CBA000–CBA006)
└── CrossBuy.Analyzers.Tests/ 11
```

**There is no layered project separation.** Controllers, application services, domain logic and EF
entities all live in one assembly. The only enforced seam is the `BL/Platform/` kernel, and it is
enforced by governance and analyzers rather than by project references.

**One `DbContext`:** `CrossBuy/Models/Context/CrossDbContext.cs`. Every module shares it. This is the
root cause of most coupling in `18-DIRECT-COUPLING-MAP.md` — a "module boundary" here is a folder
convention, not a compile-time barrier.

## Modules by UI surface

| Module | Views | Notes |
|---|---:|---|
| Inventory | 79 | largest surface; includes Manufacturing screens |
| Accounting | 66 | |
| CRM | 34 | |
| Admin | 31 | HR/appraisals/recruitment/training partials |
| Project | 17 | |
| POS | 16 | plus Hyper 9 |
| People | 12 | |
| Hyper (POS variant) | 9 | |
| Tasks | 8 | |
| ClientPortal | 6 | + Portal 1 |
| Workspace | 5 | |
| Reports | 4 | |
| Calendar | 3 | |
| Comm | 3 | + Chat 1 |
| Roster | 2 | + Store 2 |
| Documents | **1** | |
| Approvals | **1** | |
| Notifications | 1 | |

Documents and Approvals each have a single view despite substantial back-end services — see files 09.

## Platform kernel (`BL/Platform/`)

| Concern | Type |
|---|---|
| Tenant context | `BusinessContext`, `BusinessContextAccessor`, `BusinessContextFactory` |
| Company scope | `CompanyScopeHolder`, `CompanyQueryFilters`, `CompanyWriteGuardInterceptor`, `WorkerCompanyScope` |
| Events | `BusinessEventService`, `BusinessEventDispatchWorker`, `IBusinessEventConsumer` |
| Permissions | `PlatformPermissionProvider`, module adapters, `BootstrapAccessPolicyReader` |
| Entities | `EntityRegistry`, `IEntityRegistry` |
| Org | `OrgHierarchy` / `IOrgHierarchy` |
| Task scope | `TaskScopeQuery` |
| Orchestration | `Orchestration/` — **phase3/4 only** |
| AI | `Ai/` — out of scope for this pack per brief |
