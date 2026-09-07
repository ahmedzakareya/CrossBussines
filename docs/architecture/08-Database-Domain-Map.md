# 08 — Database Domain Map

## Scope
The complete data layer: **218 `DbSet<>` properties over 261 entity classes in one `CrossDbContext`**, plus the
105 SQL deployment scripts that are the actual schema authority (migrations are disabled).

## Evidence
`CrossBuy/Models/Context/CrossDbContext.cs` · `evidence/DbSet-Inventory.csv` ·
`evidence/Entity-Inventory.csv` · `evidence/SQL-Script-Inventory.csv` · `CrossBuy/deploy/sql/*` + `deploy/sql/*`.

## Structural facts

| Fact | Value | Evidence |
|---|---|---|
| Databases | 1 (`CrossBuyDB2`) + a session-cache table in the same DB | `ConnectionStrings`, `Caching.SqlServer` |
| Schemas | **1 — everything is `dbo`** | no `ToTable(..., schema:)` anywhere; scripts use bare or `dbo.` names |
| Contexts | 1 (`CrossDbContext : IdentityDbContext<Users>`) | class declaration |
| Migrations | **disabled** — `Migrations/` is empty | folder present, no migration classes |
| Schema authority | 105 idempotent SQL scripts (96 with guards) | `SQL-Script-Inventory.csv` |
| Decimal policy | global `(19,4)` convention + **60 explicit per-column pins** so model == DB on all 352 decimal properties | `CrossDbContext.ConfigureConventions` + the `pin` dictionary |
| Global query filters | **none** | no `HasQueryFilter` in the context |

**The decimal pinning block is a genuine strength** — it documents and enforces model/DB parity for every money,
rate and quantity column, and an integrity test asserts it. It is also the only place in the data layer with that
level of rigour.

## High-level domain map

```mermaid
flowchart TB
  subgraph FIN["Accounting core (17 DbSets)"]
    GL["JournalEntry(+Line) · Account · AccountType<br/>FiscalPeriod · CostCenter · Currency · ExchangeRate<br/>TaxCode · BankAccount · BankReconciliation(+Line)"]
    AR["Customer · SalesInvoice(+Line) · SalesReturn(+Line)<br/>Receipt · ReceiptAllocation"]
    AP["Vendor · PurchaseInvoice(+Line) · PurchaseReturn(+Line)<br/>Payment · PaymentAllocation"]
  end
  subgraph ASSET["Fixed assets (4)"]
    FA["FixedAsset · DepreciationLine<br/>AssetMaintenance"]
  end
  subgraph INVD["Inventory (31)"]
    IT["Item · ItemCategory · ItemComponent · UoM · UoMConversion"]
    WH["Warehouse · BinLocation · StockMovement · StockBalance"]
    DOC["PurchaseOrder(+Line) · GoodsReceipt · SalesOrder(+Line)<br/>DeliveryNote · Quotation · StockCount · StockTransfer<br/>InventoryApproval · InventoryUserRole · InventorySettings<br/>OpeningBalance · IntegrityCheckRun"]
  end
  subgraph MFG["Manufacturing (7)"]
    MO["ManufWorkOrder · ManufWorkOrderComponent<br/>ManufWorkOrderLabor · ManufWorkCenter<br/>ManufRoutingOp · ManufPlan · ManufPlanDemand"]
  end
  subgraph POSD["POS (11)"]
    PO["PosOrder(+Line) · PosPayment · PosShift · PosTerminal<br/>RestaurantTable · DiningArea · KitchenStation<br/>BranchPosSetting · BranchUserRole · PosSyncLog"]
  end
  subgraph HRD["HR (26)"]
    HRE["Employee · EmploymentContract · JobTitle · Payslip<br/>LeaveRequest · LeaveApprovalStep · LeaveTypes · LeavePolicies<br/>EmployeeRequest · EmployeeRequestStep · AttendanceRecord<br/>Appraisal(+Line/Criterion) · JobApplication · TrainingCourse<br/>FinalSettlement · EmployeeDocument · Policies · SalaryPolicies"]
  end
  subgraph PRJD["Projects (11)"]
    PR["Project · ProjectContract · Boq · ProjectProgress(+Line)<br/>ProgressBilling(+Line) · Subcontract · SubcontractBilling<br/>VariationOrder(+Line) · ProjectMaterialIssue(+Line)"]
  end
  subgraph TSKD["Tasks (4)"]
    TS["TaskItem · TimesheetEntry · TaskAutoRule/Log<br/>TaskMatchSuggestion"]
  end
  subgraph CRMD["CRM (14)"]
    CR["Lead · Opportunity · CrmAccount · CrmContact · Ticket<br/>Activity · Campaign(+Member) · CrmList(+Member)<br/>CrmCustomField(+Value) · CrmUserRole · CrmAutomationRule"]
  end
  subgraph COMD["Communication (11)"]
    CO["Notification · NotificationMute<br/>Conversation · ConversationMember · ChatMessage · ChatReaction<br/>CommMessage · CommAttachment · Announcement(+Read)<br/>CalendarEvent(+Attendee) · LibraryItem · DocComment"]
  end
  subgraph PLT["Platform (2)"]
    PL["BusinessEvent · BusinessEventDispatch"]
  end
  subgraph ORG["Org & security (unclassified, ~78 incl. Identity)"]
    OR["Companies · CompanyType · Branch · Hierarchical(+Type)<br/>AdministrativeBodiesCompany · CountriesLookup<br/>AccountingUserRole · Attachment · SystemForms<br/>AspNet* Identity tables"]
  end

  INVD -->|"StockMovement → JE"| FIN
  MFG -->|"WIP 1105 / applied 520108"| FIN
  MFG --> INVD
  POSD --> FIN & INVD
  HRD -->|"payroll / settlement JE"| FIN
  PRJD -->|"ProjectId on JE lines"| FIN
  PRJD --> INVD & HRD
  TSKD --> FIN & MFG & HRD
  CRMD -->|"CrmCustomerLink"| FIN
  ASSET --> FIN
  PLT -.->|"EntityType + EntityId"| FIN & INVD & MFG
  COMD -.->|"EntityType + EntityId"| FIN & INVD
  ORG --> HRD & FIN & INVD & POSD
```

## Platform tables ERD

```mermaid
erDiagram
  BusinessEvents ||--o{ BusinessEventDispatch : "FK_BusinessEventDispatch_Event"
  BusinessEvents {
    bigint EventId PK
    uniqueidentifier EventUid "UX_BusinessEvents_EventUid"
    int CompanyID "isolation"
    int BranchID "nullable"
    nvarchar60 EntityType "canonical registry code"
    int EntityId
    nvarchar80 EventType "EntityType.Action"
    int ActorEmployeeId "null = system"
    nvarchar_max Payload "JSON, <=64KB, ISJSON check"
    int PayloadVersion
    uniqueidentifier CorrelationId
    nvarchar120 DedupKey "UX unique filtered on CompanyID+DedupKey"
    nvarchar40 Visibility "CK: Internal|Confidential|Restricted|System"
    datetime2 CreatedAt
    datetime2 CompletedAt "all consumers Done"
  }
  BusinessEventDispatch {
    bigint ID PK
    bigint EventId FK
    nvarchar40 Consumer "UX EventId+Consumer"
    nvarchar20 Status "CK: Pending|Claimed|Done|Failed"
    int Attempts "CK >= 0"
    nvarchar400 Error "truncated"
    datetime2 UpdatedAt "stale-claim clock"
  }
```

This is the **only** table pair in the database with check constraints on a frozen vocabulary, a filtered unique
index for idempotency, and a filtered work index (`Status <> 'Done'`).

## Communication tables ERD

```mermaid
erDiagram
  Conversations ||--o{ ConversationMembers : has
  Conversations ||--o{ ChatMessages : contains
  ChatMessages ||--o{ ChatReactions : has
  Notifications }o..|| Employee : "RecipientEmployeeID (Restrict)"
  Notifications }o..o{ NotificationMutes : "by Category"
  CommMessages ||--o{ CommAttachments : has
  Announcements ||--o{ AnnouncementReads : "read receipts"
  CalendarEvents ||--o{ CalendarEventAttendees : invites
  LibraryItems ||--o{ LibraryItems : "ParentId tree"
  DocComments {
    int Id PK
    int CompanyID
    nvarchar EntityType "FREE TEXT - no FK, no constraint"
    int EntityId
    nvarchar Body
    datetime2 DeletedAt "soft delete"
  }
  Notifications {
    int ID PK
    int RecipientEmployeeID FK
    int CompanyID "nullable"
    nvarchar Type "catalog key"
    int RefId "untyped"
    nvarchar EntityType "slice 2, nullable"
    int EntityId "slice 2, nullable"
    nvarchar DedupKey "IX_Notifications_Recipient_DedupKey"
  }
```

## Security and organization tables ERD

```mermaid
erDiagram
  AspNetUsers ||--|| Employee : "Employee.UserId (Cascade)"
  Companies ||--o{ Employee : "EmpCompanyID (Restrict)"
  Companies ||--o{ Companies : "ParentCompany tree"
  CompanyTypes ||--o{ Companies : classifies
  Branches ||--o{ Employee : "BranchID (Restrict)"
  CountriesLookup ||--o{ Employee : "CountryID (Restrict)"
  Hierarchicals ||--o{ Hierarchicals : "H_Parent tree"
  HierarchicalTypes ||--o{ Hierarchicals : "H_Type (Cascade)"
  Employee ||--o{ AccountingUserRoles : "EmployeeId (no FK)"
  Employee ||--o{ InventoryUserRoles : "EmployeeId (no FK)"
  Employee ||--o{ CrmUserRoles : "EmployeeId (no FK)"
  Employee ||--o{ BranchUserRoles : "EmployeeId (no FK)"
```

**The four role tables have no declared foreign key to `Employee`** — they are plain `int EmployeeId` columns.
Deleting an employee leaves orphan role rows.

## Approval / workflow tables ERD

```mermaid
erDiagram
  LeaveRequests ||--o{ LeaveApprovalSteps : "LeaveRequestID (FK declared)"
  EmployeeRequests ||--o{ EmployeeRequestSteps : "EmployeeRequestID (no FK attribute)"
  InventoryApprovals {
    int ID PK
    int CompanyID
    nvarchar DocType "PurchaseOrder|StockTransfer|StockCount|WriteOff - free text"
    decimal Amount
    nvarchar PayloadJson "the DEFERRED document, executed on approve"
    nvarchar Status "Pending|Approved|Rejected"
    int RequestedByEmployeeId "no FK"
    int DecidedByEmployeeId "no FK"
    nvarchar ResultDocNo
  }
  LeaveApprovalSteps {
    int ID PK
    int LeaveRequestID FK
    int Level "1 = direct manager"
    int ApproverEmployeeID "no FK"
    int Status "0 pending|1 approved|2 rejected"
    datetime2 DecisionAt
    nvarchar DecisionNote
  }
  EmployeeRequestSteps {
    int ID PK
    int EmployeeRequestID
    int Level
    int ApproverEmployeeID "no FK"
    int Status "0|1|2"
    datetime2 DecisionAt
    nvarchar DecisionNote
  }
```

`LeaveApprovalStep` and `EmployeeRequestStep` are **structurally identical** — the same table duplicated for two
business objects. `InventoryApproval` is a completely different model (single row, no chain, carries the deferred
document as JSON). There is **no** table for the fourth silo (discount approval). See 14.

## Isolation report

### Tables/entities **without** a company field where isolation may be required
**78 entity classes** have no `CompanyID`/`CompanyId`. Complete list in `Entity-Inventory.csv`
(filter `company_field` empty). They fall into four groups:

| Group | Examples | Assessment |
|---|---|---|
| **Line/child tables** (isolation inherited from the parent) | `SalesInvoiceLine`, `PurchaseInvoiceLine`, `JournalEntryLine`, `ProgressBillingLine`, `VariationOrderLine`, `AppraisalLine`, `DepreciationLine`, `BankReconciliationLine` | **Acceptable** — always queried through the parent |
| **Global lookups** | `AccountType`, `CountriesLookup`, `CompanyType`, `Currency`, `ExchangeRate`, `SystemForms` | **Acceptable by design** — but `ExchangeRate` being global is a real multi-company question |
| **HR entities reaching company only via `Employee`** | **`LeaveRequest`**, `LeaveApprovalStep`, `EmployeeRequestStep`, `LeavePolicies`, `LeaveTypes`, `AttendancePolicies`, `SalaryPolicies`, `Policies`, `PolicyAssignments`, `JobTitle` | 🔴 **Risk** — every query must join `Employee`; nothing enforces it |
| **Platform-wide / infra** | `Users` (Identity), `Attachment`, `NotificationMute`, `Hierarchical`, `AdministrativeBodiesCompany`, `CalendarEventAttendee`, `AnnouncementRead` | 🟠 Mixed — `Attachment` and `NotificationMute` are per-employee, so acceptable; `Hierarchical` is the org tree and should arguably be scoped |
| **`FiscalPeriod`** | — | 🔴 **Highest concern** — period open/close is a company-level control with no company column |

### Cross-company leakage risk
**No EF global query filter exists.** Isolation therefore depends on every single query writing
`.Where(x => x.CompanyID == companyId)` by hand, with `companyId` usually supplied by
`private const int DefaultCompanyId = 1` in eight controllers. A missed filter is a silent cross-tenant read.
Evidence of the pattern already leaking into services: `AccountingAccessService`, `InventoryAccessService`,
`CrmAccessService` and `InventoryApprovalService` each declare `private const int CompanyId = 1`.

### Text-based polymorphism (no FK possible)
**10 entities** key on a string type discriminator:

| Table | Discriminator | Constrained? |
|---|---|---|
| `BusinessEvents` | `EntityType` | ✅ validated by `IEntityRegistry` in code |
| `DocComments` | `EntityType` | ✖ **free text** (`Quotation` proves it) |
| `Activities` (CRM) | `EntityType` | ✖ free text (doc comment lists 5 values) |
| `TaskItems` | `EntityType` | ◐ validated against 7 picker types in code |
| `TaskMatchSuggestions` | `EntityType` | ◐ same |
| `CampaignMembers`, `CrmListMembers` | `EntityType` | ✖ free text |
| `CrmCustomFieldValues` | `EntityType` | ✖ free text |
| `Notifications` | `EntityType` (slice 2) | ✅ registry codes only |
| `InventoryApprovals` | `DocType` | ✖ free text (4 known values) |
| `JournalEntries` | `SourceType` + `SourceId` | ✖ **free text, 52 distinct values observed** |
| `StockMovements` | `SourceType` + `SourceId` | ✖ free text |

`JournalEntry.SourceType` with **52 values** is the largest untyped polymorphic surface in the database and the
backbone of every legacy timeline reconstruction.

### Soft delete
Only **5** entities: `CalendarEvent`, `ChatMessage`, `CommMessage`, `DocComment`, `LibraryItem` — all
Communication. `DeletedAt` with no query filter, so every read must remember `.Where(x => x.DeletedAt == null)`.

### Audit columns
`BaseEntity` supplies `CreatedBy` / `CreatedAt` / `updatedBy` / `UpdatedAt` (note the lowercase `updatedBy`).
Not all entities inherit it; many declare `CreatedAt` alone. There is **no automatic audit interceptor** — every
service sets the stamps by hand.

## Cross-domain relationship map

```mermaid
flowchart LR
  SM["StockMovements<br/>SourceType+SourceId"] -.-> SI["SalesInvoices"] & PI["PurchaseInvoices"] & WO["ManufWorkOrders"] & PJ["ProjectMaterialIssues"] & PS["PosOrders"]
  JE["JournalEntries<br/>SourceType+SourceId<br/>(52 values)"] -.-> SI & PI & PS & WO & PY["Payslips"] & FA["FixedAssets"] & PB["ProgressBillings"] & RC["Receipts"] & PM["Payments"]
  BE["BusinessEvents<br/>EntityType+EntityId"] -.-> SI & PI & CU["Customers"] & WO
  DC["DocComments<br/>EntityType (free text)"] -.-> SI & PI & QT["Quotations"]
  AC["Activities<br/>EntityType (free text)"] -.-> CU & LD["Leads"] & OP["Opportunities"]
  TI["TaskItems<br/>EntityType (7 types)"] -.-> SI & CU & WO & PS & EM["Employees"] & PJ2["Projects"] & IT["Items"]
```

Every cross-domain link in the system is **string-based**. There is no `BusinessRelation` table (Book 1 Layer 3
— **Planned**, not implemented).

## Tables lacking proper foreign keys
Systematic pattern: **role tables, approval approver columns, and every polymorphic link have no FK.**
Specifically: `AccountingUserRoles.EmployeeId`, `InventoryUserRoles.EmployeeId`, `CrmUserRoles.EmployeeId`,
`BranchUserRoles.EmployeeId`, `LeaveApprovalStep.ApproverEmployeeID`,
`EmployeeRequestStep.ApproverEmployeeID` + `.EmployeeRequestID`, `InventoryApproval.RequestedByEmployeeId` /
`.DecidedByEmployeeId`, `Notification.ActorEmployeeID`, `BusinessEvent.ActorEmployeeId`, all `EntityId` columns.

Declared FKs exist mainly where EF needed them for navigation (`CrossDbContext.OnModelCreating`: `Employee`↔
`Users`/`Country`/`Company`/`Branch`, `Companies`↔`CompanyType`/parent, `Hierarchical` tree, `LeaveRequest`↔
`Employee`/`LeaveType`, `Notification`↔`Recipient`, `BusinessEventDispatch`→`BusinessEvents`).

## SQL deployment scripts
105 scripts, **96 with idempotency guards**. The 9 without guards need review (`SQL-Script-Inventory.csv`,
`idempotent = False`). Scripts live in **two folders** — `CrossBuy/deploy/sql` (101) and `deploy/sql` (4) — with
no manifest or ordering file. Only `platform_business_events*.sql` declare check constraints; only two declare
foreign keys.

## Gaps
- No schemas → no database-level module ownership.
- No global query filters → isolation is manual everywhere.
- No audit interceptor → audit stamps are per-service discipline.
- No relation table, no tag table, no object-file table.
- No script manifest / applied-migrations table.

## Risks
| Risk | Severity |
|---|---|
| Cross-company read via a forgotten `CompanyID` filter | **Critical** |
| `FiscalPeriod` has no company column — period control may be global | **High** |
| HR entities scoped only through `Employee` | **High** |
| Orphan role rows after employee deletion (no FK) | Medium |
| `JournalEntry.SourceType` free text with 52 values | Medium |
| Soft-delete rows leaking into reads (no filter) | Medium |
| Two `deploy/sql` folders, no manifest | Medium |

## Dependencies
06, 07, 09, 13, 18.

## Recommendations
1. Add EF **global query filters** for `CompanyID` and `DeletedAt` — the single highest-value data-integrity
   change available, and it is additive.
2. Give `FiscalPeriod` a company column (or prove globality is intended).
3. Add FKs to the four role tables and the approval approver columns.
4. Consolidate `deploy/sql` and add an applied-scripts table.
