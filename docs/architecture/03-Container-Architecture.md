# 03 — Container and Runtime Architecture

## Scope
Runtime topology, process/transaction boundaries, request and background lifecycles, configuration sources and
DI lifetimes.

## Evidence
`CrossBuy/Program.cs` (whole file: DI, auth, session, SignalR, middleware order, hosted services) ·
`BL/ScopedTx.cs` · `Models/SessionValidationMiddleware.cs` · `Models/SessionValidationAttribute.cs` ·
`appsettings*.json` · `BL/BusinessEventDispatchWorker.cs` (in `BL/Platform/`).

## Containers

**One deployable process.** The web host also runs every background worker and both SignalR hubs. There is no
separate worker service, no queue broker, no cache server (session cache is a SQL Server table).

```mermaid
C4Container
  title CrossBuy — Container diagram (as-built)
  Person(user, "Employee", "browser, cookie session")
  Person(visitor, "Storefront visitor", "anonymous")
  System_Ext(mobile, "Flutter app", "REST + JWT, SignalR")

  Container_Boundary(host, "CrossBuy web host (single ASP.NET Core 8 process)") {
    Container(mvc, "MVC + Razor", "38 controllers / 1002 actions / 315 views", "Metronic UI, 11 layouts")
    Container(api, "REST API", "10 API controllers / 273 actions", "JWT, mobile-facing")
    Container(bl, "Business layer", "115 registered services under BL/", "no repository layer")
    Container(kernel, "Platform Kernel", "BL/Platform/*", "registry, events, outbox, projections, permissions")
    Container(hubs, "SignalR hubs", "NotificationsHub, ChatHub, PosHub", "cookie + JWT query token")
    Container(workers, "5 hosted services", "BackgroundService", "in-process timers")
  }

  ContainerDb(db, "SQL Server", "CrossBuyDB2", "218 DbSets, one context, + session cache table")
  System_Ext(smtp, "SMTP (Gmail)", "outbound mail")
  System_Ext(ai, "crossbuy_ai", "Python FastAPI :8000")
  ContainerDb(files, "Local disk", "wwwroot/uploads", "attachments, library, HR docs")

  Rel(user, mvc, "HTTPS")
  Rel(visitor, mvc, "HTTPS (anonymous storefront)")
  Rel(mobile, api, "REST + JWT")
  Rel(mobile, hubs, "SignalR ?access_token=")
  Rel(user, hubs, "SignalR (cookie)")
  Rel(mvc, bl, "in-process")
  Rel(api, bl, "in-process")
  Rel(bl, kernel, "IBusinessEventService")
  Rel(workers, kernel, "claim + dispatch")
  Rel(bl, db, "EF Core 9")
  Rel(kernel, db, "EF Core 9")
  Rel(workers, db, "EF Core 9 + raw ADO")
  Rel(bl, smtp, "System.Net.Mail")
  Rel(bl, ai, "HTTP + shared secret")
  Rel(bl, files, "file I/O")
  Rel(kernel, hubs, "via NotificationService")
```

## Runtime interaction

```mermaid
flowchart LR
  B["Browser / Mobile"] -->|request| MW["Middleware pipeline"]
  MW --> C["Controller action"]
  C --> S["BL service"]
  C -.->|"14 of 38 controllers"| DBX[("CrossDbContext<br/>direct")]
  S --> TX["ScopedTx.BeginOrJoinAsync"]
  TX --> DB[("SQL Server")]
  S --> EV["IBusinessEventService.RecordAsync<br/>(inside TX)"]
  EV --> DB
  TX -->|CommitAsync| DB
  S -.->|"after commit, try/catch"| NOTIF["NotificationService"]
  NOTIF --> HUB["NotificationsHub → client"]
  W["BusinessEventDispatchWorker<br/>every 15s"] -->|"claim Pending"| DB
  W --> TP["TimelineProjectionConsumer"]
  W --> NP["NotificationProjectionConsumer"] --> NOTIF
```

## Request lifecycle

Middleware order in `Program.cs` matters and is fragile (flagged in the Comm-Hub analysis). Observed order:
localization → static files → routing → session → authentication → authorization →
`SessionValidationMiddleware` → endpoints.

```mermaid
sequenceDiagram
  participant U as Browser
  participant L as Localization
  participant SE as Session (SQL cache)
  participant AU as Auth (cookie)
  participant SV as SessionValidationMiddleware
  participant F as SessionValidationAttribute
  participant C as Controller
  participant S as BL service
  participant TX as ScopedTx
  participant DB as SQL Server

  U->>L: HTTPS request
  L->>SE: resolve culture (ar default), load session
  SE->>AU: authenticate cookie principal
  AU->>SV: middleware checks Session["Employee"]
  Note over SV: bypasses /pos/* (PosCtx), /home/store + /store* (visitor),<br/>/api/* (JWT), login/logout/language
  SV->>F: action filter re-checks Session["Employee"]
  F->>C: action executes
  C->>S: service call
  S->>TX: BeginOrJoinAsync (owns or joins)
  TX->>DB: writes + RecordAsync (event) in one transaction
  TX->>DB: CommitAsync
  S-->>C: result
  C-->>U: View / Json / Redirect
  Note over S: post-commit best-effort:<br/>NotifyAsync in try/catch
```

**Two overlapping session gates.** `SessionValidationMiddleware` (path-based) **and**
`SessionValidationAttribute` (per-controller) both check `Session["Employee"]`. This is duplicated enforcement
with two different bypass lists — an **Inconsistency**, and the reason a new public path must be added in two
places.

## Background lifecycle

```mermaid
sequenceDiagram
  participant H as Host
  participant W as BusinessEventDispatchWorker
  participant SC as DI scope (per pass)
  participant ST as SqlEventDispatchStore
  participant DB as SQL Server
  participant CN as Consumer

  H->>W: StartAsync
  W->>W: Task.Delay(30s) — let the app come up
  loop PeriodicTimer(PollSeconds=15)
    W->>SC: CreateScope() (CrossDbContext is Scoped)
    loop per registered consumer
      W->>ST: ClaimPendingAsync(consumer, BatchSize)
      ST->>DB: UPDATE TOP(n) ... WITH (ROWLOCK, READPAST, UPDLOCK) OUTPUT inserted.*
      DB-->>ST: claimed rows (Status=Claimed, Attempts+1)
      loop per claimed row
        W->>DB: load BusinessEvent
        W->>CN: HandleAsync(envelope)
        alt success
          W->>ST: MarkDoneAsync → TryCompleteEventAsync
        else throws
          W->>ST: MarkFailedAsync(error, truncated 400)
        end
      end
    end
    W->>SC: dispose scope
  end
  H->>W: StopAsync (stoppingToken) — Claimed rows reclaimed after StaleClaimMinutes
```

## Boundaries

| Boundary | Reality | Evidence |
|---|---|---|
| **Process** | One. Web + workers + hubs co-hosted | `Program.cs AddHostedService` ×5 |
| **Database** | One database, one `DbContext`, **no schemas** (all `dbo`) | `CrossDbContext`, `deploy/sql/*` all unqualified or `dbo.` |
| **Transaction** | `ScopedTx.BeginOrJoinAsync` — own-or-join. **57 sites** across BL | `grep ScopedTx.BeginOrJoinAsync` |
| **Tenant** | `CompanyID` column convention. **No EF global query filter** | see 08 |
| **Module** | Namespace/folder only; no assembly boundary | 02 |

### Transaction ownership
`ScopedTx` is the only transaction primitive. It owns a transaction when the `DbContext` has none and becomes a
no-op joiner when one exists — which is what lets `ManufService` wrap `StockService` without editing it
(ADR-001 slice-2 addendum). Highest concentrations: `StockService` (15 sites), `PosOrderService` (5),
`ReceivableService` (6), `PayableService` (4), `JournalEntryService` (3).

**Gap:** three write paths had *no* transaction until slice 2 (`CreateCustomerAsync`, `SaveCustomerAsync`,
`ManufService.CreateAsync`/`SaveHeaderAsync`). Others may remain — see 10.

## Configuration sources

| Source | Contents |
|---|---|
| `appsettings.json` | `ConnectionStrings:DefaultConnection`, `Jwt:*`, `AiService:*`, `Smtp:*`, `Platform:EventDispatch:*` |
| `appsettings.Development.json` / `appsettings.Production.json` | environment overrides |
| Environment variables | the documented convention for prod secrets (`docs/DEPLOYMENT.md`) — **not enforced**; secrets are present in `appsettings.json` |
| Options binding | only `BusinessEventDispatchOptions` uses `Configure<T>`. Everything else reads `IConfiguration` inline |

`appsettings.json` is **gitignored** (`.gitignore:20`), so it is per-environment by construction — but it is
also invisible to review.

## DI lifetimes

Overwhelmingly **Scoped** (all 115 registered services except `IdProtector`, which is Singleton, and the 5
hosted services). This is deliberate: everything shares the request `CrossDbContext`, which is what allows
`IBusinessEventService` to enlist in the caller's ambient `ScopedTx`.

**Consequence:** any service that needs to run outside a request must create its own scope. All five workers do.

## Gaps
- No health checks, no metrics, no readiness/liveness endpoints.
- No response caching, no output caching, no distributed cache other than session.
- No schema separation in the database; module ownership of tables is implicit.
- Four of five workers are unsafe to scale out (see 15).

## Risks
- **Single process** means workers cannot be scaled or restarted independently of the web tier.
- **Middleware/attribute duplication** for session gating: a bypass added in one place and not the other is a
  security or availability bug.
- Runtime Razor compilation (`Mvc.Razor.RuntimeCompilation`) is referenced — good for dev, a cost in prod.

## Dependencies
01-System-Context, 09-Platform-Kernel, 13-Security, 15-Background-Workers, 18-Deployment.

## Recommendations
1. Collapse the two session gates into one.
2. Add health checks (DB, SMTP reachability, worker last-success) before any scale-out.
3. If scale-out is ever needed, workers must move behind a leader election or the claim pattern (see 15).
