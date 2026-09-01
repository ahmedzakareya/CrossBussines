# CrossBusiness Platform — Roadmap v2 — 13 Architecture Diagrams

**Ten editable Mermaid sources.** Every diagram distinguishes current from future; no box asserts a capability the capability catalog does not support.

Shared class definitions used throughout:

```
classDef done   fill:#13433a,color:#fff,stroke:#0d2f28;   %% verified
classDef found  fill:#1d6b5a,color:#fff,stroke:#134539;   %% foundation, no UI
classDef legacy fill:#2c4a6b,color:#fff,stroke:#1d3348;   %% legacy working
classDef part   fill:#4a4a4a,color:#fff,stroke:#333;      %% partial
classDef mixed  fill:#8a6d1c,color:#fff,stroke:#5c4812;   %% remediation
classDef block  fill:#8a1c1c,color:#fff,stroke:#5c1212;   %% blocked
classDef plan   fill:#6b5a8a,color:#fff,stroke:#453a5c;   %% planned
classDef none   fill:#333,color:#aaa,stroke-dasharray: 4 3; %% absent
```

---

## 1 · Current-State Architecture

See `Roadmap-V2-05-Current-State-Architecture.md` §2 — the authoritative source. Reproduced there rather than duplicated here so the two cannot drift.

## 2 · Target Architecture

See `Roadmap-V2-06-Target-Architecture.md` §1.

## 3 · Shared Platform Relationships

```mermaid
graph LR
  subgraph P["Shared platforms"]
    REP["Reporting"]:::found
    COM["Communication"]:::found
    WFL["Workflow"]:::part
    FIL["Files"]:::part
    NOT["Notifications"]:::legacy
    SRC["Search"]:::none
    AI["AI"]:::block
    MDM["Master Data"]:::none
  end
  subgraph M["Consuming modules"]
    ACC["Accounting"]:::legacy
    INV["Inventory"]:::legacy
    CRM["CRM"]:::block
    PRJ["Projects"]:::legacy
    CON["Construction"]:::found
    TSK["Tasks"]:::part
    SUP["Support"]:::part
  end
  ACC -.->|"dataset — NOT bound"| REP
  INV -.->|"dataset — NOT bound"| REP
  TSK -.->|"ICommEntitySurface — NOT registered"| COM
  SUP -.->|"ICommEntitySurface — NOT registered"| COM
  ACC -->|"events — active"| NOT
  INV -->|"events — active"| NOT
  CON -.->|"planned"| FIL
  CRM -.->|"planned"| COM
  PRJ -.->|"planned"| REP
  classDef done fill:#13433a,color:#fff;
  classDef found fill:#1d6b5a,color:#fff;
  classDef legacy fill:#2c4a6b,color:#fff;
  classDef part fill:#4a4a4a,color:#fff;
  classDef block fill:#8a1c1c,color:#fff;
  classDef none fill:#333,color:#aaa,stroke-dasharray: 4 3;
```

**Solid = live today. Dotted = contract exists, not consumed.** Every dotted line into Reporting and Communication is the same finding stated seven ways: the platforms are built and nothing uses them.

## 4 · Module Dependency Map

```mermaid
graph TD
  CTX["BusinessContext"]:::done
  JE["JournalEntryService — only GL writer"]:::done
  SS["StockService — only stock writer"]:::done
  ACC["Accounting"]:::legacy --> JE
  INV["Inventory"]:::legacy --> SS
  PUR["Purchasing"]:::legacy --> INV
  PUR --> ACC
  POS["POS"]:::legacy --> INV
  POS --> ACC
  MFG["Manufacturing"]:::legacy --> SS
  PRJ["Projects"]:::legacy --> ACC
  PRJ --> INV
  CON["Construction"]:::found --> PRJ
  CRM["CRM"]:::block --> ACC
  HR["HR"]:::legacy --> ACC
  TSK["Tasks"]:::part --> PRJ
  ACC --> CTX
  INV --> CTX
  CRM --> CTX
  HR --> CTX
  PRJ --> CTX
  classDef done fill:#13433a,color:#fff;
  classDef found fill:#1d6b5a,color:#fff;
  classDef legacy fill:#2c4a6b,color:#fff;
  classDef part fill:#4a4a4a,color:#fff;
  classDef block fill:#8a1c1c,color:#fff;
```

`Projects.billing → Accounting.post` is the dependency worth noting: being a projects administrator confers no right over the ledger, and B6 made that transitive closure hold on unconfigured companies too.

## 5 · Reporting Flow

```mermaid
sequenceDiagram
  participant U as User
  participant RC as Report Center (PLANNED)
  participant RS as ReportService
  participant DR as DatasetRegistry
  participant DS as DataSource
  participant EX as Exporter
  Note over U,RC: NO PATH TODAY — no UI exists
  U-->>RC: request report
  RC->>RS: RunAsync(definition, parameters)
  RS->>RS: ReportAuthorizationService
  RS->>DR: resolve dataset
  DR->>DS: query (AsNoTracking, company-scoped)
  Note over DR,DS: NO PRODUCTION MODULE BOUND (D-35)
  DS-->>RS: shaped rows
  RS->>EX: HTML / CSV / Excel (live) · PDF (no runtime, D-09)
  EX-->>RC: artifact
  RC-->>U: download
  RS->>RS: ReportHistoryService + ReportArchiveService
```

## 6 · Communication Flow

```mermaid
sequenceDiagram
  participant U as User
  participant UI as Comm UI (NOT STARTED)
  participant TS as CommThreadService
  participant AP as CommAccessPolicy
  participant MN as CommMentionService
  participant ND as CommNotificationDispatcher
  Note over U,UI: NOT ACTIVATED — registration not called in Program.cs
  U-->>UI: comment on an entity
  UI->>TS: AddComment(CommEntityRef, body)
  TS->>AP: may this principal see this entity?
  AP-->>TS: allow / deny (boundary mechanically proved)
  TS->>MN: resolve @user and @role (D-15 open)
  TS->>ND: dispatch per preference
  ND-->>U: notification
  Note over TS: CommAuditWriter records (D-07 open)
```

## 7 · Construction Integration Flow

```mermaid
graph LR
  BOQ["BOQ line — stable identity CR-01"]:::done
  REV["Commercial revision — immutable CR-03"]:::done
  SUB["Subcontract scope"]:::done
  CERT["Certification — capped CR-02"]:::done
  AUD["Construction audit — append-only"]:::done
  DDL[("construction_c1 DDL — 8 tables<br/>APPLIED NOWHERE")]:::block
  PRJ["Projects"]:::legacy
  ACC["Accounting"]:::legacy
  PRJ --> BOQ --> REV
  SUB --> CERT
  CERT -.->|"C6 — deferred"| ACC
  BOQ --> AUD
  BOQ -.-> DDL
  CERT -.-> DDL
  classDef done fill:#13433a,color:#fff;
  classDef legacy fill:#2c4a6b,color:#fff;
  classDef block fill:#8a1c1c,color:#fff;
```

The red node is the whole risk: services are registered in `Program.cs` while their tables exist in no database (RSK-11).

## 8 · Security and Business Context Flow

```mermaid
sequenceDiagram
  participant R as HTTP request
  participant MW as CompanyScopeMiddleware
  participant BC as BusinessContextFactory
  participant AS as ModuleAccessService
  participant BP as BootstrapAccessPolicyReader
  participant DB as Database
  R->>MW: request
  MW->>BC: resolve signed-in employee and company
  alt unresolved
    BC-->>R: FAIL CLOSED — reads nothing, writes nothing
  end
  BC->>AS: CanAsync(context, action, target)
  AS->>AS: company > 0 ? else deny company_unresolved
  AS->>AS: known action ? else deny unknown_action
  alt roles configured
    AS-->>R: role switch — DecisionSource = LegacyRole
  else no roles configured
    AS->>BP: ResolveDecisionAsync(scope, action)
    BP->>BP: NeverBootstrapOpen check — BEFORE any DB read
    alt Never action
      BP-->>AS: deny never_bootstrap_open
    else
      BP->>DB: active policy for (company, scope, action)
      DB-->>BP: policy or none
      BP-->>AS: allow with policy id, or deny
    end
  end
  Note over AS: CRM still returns true here — D-03 / D-04
```

## 9 · Roadmap Phase Dependency Map

See `Roadmap-V2-08-Dependency-Map.md` §1 and §2.

## 10 · Product Integration Map

```mermaid
graph TB
  subgraph NOW["TODAY — four tabs, one tree"]
    T1["Tab 1 Security"]:::done
    T2["Tab 2 Reporting"]:::found
    T3["Tab 3 Communication"]:::found
    T4["Tab 4 Construction"]:::found
    TREE["ONE SHARED MUTABLE TREE<br/>5 build breaks · all work uncommitted"]:::block
    T1 --> TREE
    T2 --> TREE
    T3 --> TREE
    T4 --> TREE
  end
  subgraph TARGET["TARGET — worktrees + integration branch"]
    W1["worktree/security"]:::done
    W2["worktree/reporting"]:::found
    W3["worktree/communication"]:::found
    W4["worktree/construction"]:::found
    INT["integration branch<br/>MUST ALWAYS BUILD"]:::done
    GATE["12-point integration gate"]:::mixed
    W1 --> GATE
    W2 --> GATE
    W3 --> GATE
    W4 --> GATE
    GATE --> INT
  end
  NOW ==>|"R1"| TARGET
  classDef done fill:#13433a,color:#fff;
  classDef found fill:#1d6b5a,color:#fff;
  classDef mixed fill:#8a6d1c,color:#fff;
  classDef block fill:#8a1c1c,color:#fff;
```
