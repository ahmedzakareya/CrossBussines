<!-- Generated from docs/architecture/evidence/*.csv by the architecture discovery pass. Counts are re-derivable by re-running the scan. -->

# 02 — Repository Inventory

## Scope
Complete file, type and registration inventory of the entire repository. Nothing sampled: every count below
comes from a scripted pass over all tracked and untracked-but-not-ignored files.

## Evidence
`evidence/Controller-Inventory.csv` · `Endpoint-Inventory.csv` · `Service-Inventory.csv` ·
`Entity-Inventory.csv` · `DbSet-Inventory.csv` · `Screen-Inventory.csv` · `SQL-Script-Inventory.csv` ·
`Worker-Inventory.csv` · `Hub-Filter-Middleware-Inventory.csv`

## Solutions and projects

| Item | Value | Evidence |
|---|---|---|
| Solution | `CrossBuy.sln` | 2 projects |
| Web project | `CrossBuy/CrossBuy.csproj` — `Microsoft.NET.Sdk.Web`, `net8.0`, `Nullable=enable`, `ImplicitUsings=enable` | csproj |
| Test project | `CrossBuy.Tests/CrossBuy.Tests.csproj` — `Microsoft.NET.Sdk`, `net8.0`, FrameworkReference `Microsoft.AspNetCore.App` | csproj |
| Python AI service | `crossbuy_ai/` — 21 `.py` files, **not in the solution**, separate process | see 16-API-Integration-Map |
| Flutter mobile app | `crossbuy_mobile/` — 30 `.dart` files, **not in the solution** | see 16-API-Integration-Map |

### NuGet dependencies (web project)
EF Core 9.0.0 (+ `SqlServer`, `Design`, `Tools`) · ASP.NET Core 8.0.11 packages (`Identity.EntityFrameworkCore`,
`Authentication.JwtBearer`, `Authentication.Certificate`, `Mvc.Razor.RuntimeCompilation`,
`Diagnostics.EntityFrameworkCore`, `Components.QuickGrid*`) · `Microsoft.Extensions.Caching.SqlServer` 8.0.11 ·
`Microsoft.Extensions.Localization` 9.0.0 · `AutoMapper.Extensions.Microsoft.DependencyInjection` 12.0.1 ·
`ClosedXML` 0.105.0 · `QRCoder` 1.8.0 · `Swashbuckle.AspNetCore` 6.6.2 ·
`Microsoft.VisualStudio.Web.CodeGeneration.Design` 8.0.23

**Version-mixing note:** EF Core and `Microsoft.Extensions.Localization` are on 9.0.x while the ASP.NET Core
packages are on 8.0.x against a `net8.0` target. It builds, but it is an inconsistency worth recording.

### Test project dependencies
`Microsoft.EntityFrameworkCore.Sqlite` 9.0.0 · `Microsoft.NET.Test.Sdk` 17.11.1 · `xunit` 2.9.2 ·
`xunit.runner.visualstudio` 2.8.2 · `Xunit.SkippableFact` 1.4.13

## File census (tracked)

| Extension | Count | Extension | Count |
|---|---|---|---|
| `.svg` | 1336 | `.sql` | 101 |
| `.png` | 868 | `.scss` | 57 |
| `.resx` | 821 | `.pdf` | 51 |
| `.jpg` | 563 | `.md` | 42 |
| `.cshtml` | 305 | `.dart` | 30 |
| `.cs` | 287 | `.py` | 21 |
| `.js` | 239 | `.html`/`.xml`/`.docx` | 6/6/6 |
| `.css` | 130 | `.ps1`/`.json` | 4/4 |

Tracked files: **4985**. Untracked-not-ignored: **111** (the in-flight Platform Kernel, Communication Hub,
Calendar and File Manager work). Total `.cs` under `CrossBuy/` including untracked: **287**;
`.cshtml`: **315**.

## Counts by category

| Category | Count | Note |
|---|---|---|
| Controllers | **38** | of which **10** are API controllers (`Controllers/Api/` or `[ApiController]`) |
| Controller actions (endpoints) | **1002** | of which **273** on API controllers; **270** perform writes |
| Classes under `BL/` | **339** | includes DTO/result classes; **115** are DI-registered |
| DI registrations in `Program.cs` | **110** | Scoped-dominant |
| Entity/model classes | **261** | `Models/` tree |
| `DbSet<>` properties | **218** | single `CrossDbContext` |
| Razor views | **315** | of which **51** partials, **246** map to a controller action |
| SignalR hubs | 3 | `ChatHub`, `NotificationsHub`, `PosHub` |
| Hosted services / background workers | **5** | see 15-Background-Workers |
| Authorization attributes | 5 | `SessionValidationAttribute`, `AccPermAttribute`, `InvPermAttribute`, `CrmPermAttribute`, `PosLaneActivityGuardAttribute` |
| Middleware | 1 | `SessionValidationMiddleware` |
| SQL deployment scripts | **105** | **96** contain idempotency guards |
| Tests | 91 | 82 in-process + 9 SQL Server integration |

### DI lifetimes
- `(not registered)`: 224
- `Scoped`: 109
- `HostedService`: 5
- `Singleton`: 1

## Per-module inventory

| Module | Controllers | Actions | Registered services | Entities | Screens |
|---|---|---|---|---|---|
| AI | 1 | 7 | 1 | 0 | 0 |
| Accounting | 3 | 157 | 19 | 17 | 61 |
| Admin | 7 | 296 | 4 | 7 | 46 |
| CRM | 1 | 72 | 6 | 14 | 23 |
| Communication | 8 | 52 | 7 | 11 | 9 |
| FixedAssets | 0 | 0 | 3 | 4 | 0 |
| HR | 3 | 26 | 18 | 27 | 12 |
| Inventory | 3 | 173 | 13 | 33 | 66 |
| Manufacturing | 0 | 0 | 1 | 7 | 0 |
| POS | 4 | 134 | 3 | 11 | 25 |
| Platform | 1 | 1 | 20 | 27 | 0 |
| Projects | 1 | 47 | 9 | 11 | 16 |
| Tasks | 1 | 21 | 10 | 4 | 5 |
| Unclassified | 5 | 16 | 1 | 88 | 0 |

## Layers observed

```
Views/ (Razor, Metronic)  ──►  Controllers/ (+ Controllers/Api)  ──►  BL/ (services)  ──►  Models/Context (EF)
                                      │                                   │
                                      └── direct CrossDbContext access ────┘  (see 10-Controller-Service-Transaction-Map)
```

There is **no repository layer**. `BL/` services take `CrossDbContext` directly, and
**30 of 38 controllers also take `CrossDbContext` directly**, bypassing `BL/`. There is no separate
Domain or Application assembly — everything lives in one web project.

## Gaps
- No repository or unit-of-work abstraction; `ScopedTx` is the only transaction primitive.
- `Migrations/` folder exists but is **empty** — migrations are disabled; `deploy/sql/` is the deployment path.
- `deploy/sql` exists in **two locations** (`CrossBuy/deploy/sql` and `deploy/sql`), splitting the deployment set.
- `crossbuy_ai` and `crossbuy_mobile` are not in the solution and have no build integration.

## Risks
- Single-project structure means no compile-time enforcement of layer boundaries.
- Direct `CrossDbContext` use in controllers makes service-level invariants (transactions, events) skippable.
- The split `deploy/sql` folders make "have all scripts been applied?" unanswerable from one place.

## Dependencies
This inventory is the input for every other document in this set.

## Recommendations
1. Consolidate the two `deploy/sql` folders and add a manifest/ordering file.
2. Record the EF-9-on-net8 package split as a deliberate decision or align it.
3. Treat "controller writes without going through `BL/`" as a lint rule candidate (evidence in
   `Permission-Coverage.csv` + `Controller-Inventory.csv:direct_dbcontext`).
