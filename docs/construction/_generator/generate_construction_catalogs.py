#!/usr/bin/env python3
# ==============================================================================================
# CrossBusiness Construction & Contracting — CANONICAL CATALOG GENERATOR
#
# FOURTH TAB deliverable. One canonical dataset per catalog; both the CSV and the corresponding
# Markdown deliverable are emitted from it, so a number can never disagree between the two.
#
# NOTHING here reads or writes the application, the database, or any file outside docs/construction.
# It is pure data + formatting. Run:  python docs/construction/_generator/generate_construction_catalogs.py
#
# Every "Evidence" value is a real path (+ line where it matters) in this repository, verified during
# Phase 1. When a row has no evidence it says "absent — searched <where>", never a guess.
# ==============================================================================================
import csv
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.dirname(HERE)                      # docs/construction
PROVENANCE = (
    "> **Generated file — do not edit by hand.**\n"
    "> Source of truth: `docs/construction/_generator/generate_construction_catalogs.py`.\n"
    "> Regenerate with `python docs/construction/_generator/generate_construction_catalogs.py`.\n"
    "> The paired CSV in this folder is emitted from the same dataset in the same run.\n"
)

# ---- status vocabulary (Phase 2 — exactly the eight the brief defines) -------------------------
REUSABLE = "Existing and Reusable"
EXTEND = "Existing but Requires Extension"
INADEQUATE = "Existing but Construction-Inadequate"
NEW = "New Capability Required"
DUPLICATE = "Duplicate - Do Not Build"
BLOCKED_BUSINESS = "Blocked by Business Decision"
BLOCKED_DATA = "Blocked by Data Measurement"
DEFERRED = "Deferred Platform Dependency"

STATUS_ORDER = [REUSABLE, EXTEND, INADEQUATE, NEW, DUPLICATE,
                BLOCKED_BUSINESS, BLOCKED_DATA, DEFERRED]

# ==============================================================================================
# 1. CAPABILITY MATRIX (Phase 2) — the 60 capabilities the brief enumerates, in its order.
# ==============================================================================================
CAPABILITIES = [
 (1,"Project master",EXTEND,"CrossBuy/Models/Context/Accounting/Dimensions.cs:23 — `Project` is an ACCOUNTING DIMENSION (Code/Name/CostCenterId) with 9 nullable contracting columns bolted on (CustomerId, Location, ContractValue, Status, ActivityTypeId, AdvancePercent, RetentionPercent).","Construction + Projects (shared)","No SiteId, no BranchID, no ParentId, no currency, no contract-number; Status is a free string with no state machine.","C1"),
 (2,"Project hierarchy",NEW,"absent — `Project` has no ParentId (Dimensions.cs:23-45); searched Models/Context/Accounting/Dimensions.cs and CrossDbContext.cs.","Construction","Programme -> project -> sub-project cannot be expressed; a joint venture or multi-phase award has no parent.","C1"),
 (3,"WBS",NEW,"absent as an entity. The only tree is `BoqItem.ParentId` (Models/Context/Accounting/Boq.cs:13), and BoqService.ReplaceAllAsync rebuilds it from ROW ORDER (BL/BoqService.cs:128-132) — presentation, not a work breakdown.","Construction","No stable work-node identity, no path, no dates, no owner, no progress method, no budget ownership.","C1"),
 (4,"BOQ",INADEQUATE,"CrossBuy/Models/Context/Accounting/Boq.cs (33 lines) + BL/BoqService.cs (151 lines) + deploy/sql/boq.sql (26 lines).","Construction","No revision, no status, no source, no immutability; `ReplaceAllAsync` DELETES every row and re-inserts with new IDs (BoqService.cs:108-109) while ProgressBillingLine.BoqItemId still points at the old IDs. No WBS/CostCode reference. Unit is free text.","C2"),
 (5,"Cost Codes",NEW,"absent. `CostCenter` (Dimensions.cs:7) mirrors the ORG TREE (`SourceHierarchicalId` -> Hierarchicals) — an organisational rollup, not a construction cost breakdown. BOQ carries four fixed buckets as decimals (Boq.cs:22-25), not codes.","Construction","No Material/Labor/Equipment/Subcontractor/Overhead/Contingency code set, so no cost-code analysis and no per-category budget.","C1"),
 (6,"Project Budget",INADEQUATE,"BL/ProjectBudgetService.cs (85 lines) is READ-ONLY: estimated = sum of the BOQ cost buckets, actual = net debit on 510104 by ProjectId (lines 76-81). `Project.Budget` is one nullable decimal (Dimensions.cs:33) that no service reads.","Construction","No budget entity, no Original/Revised/Approved version, no baseline, no scenario, no contingency, no transfer, no revision reason, no approval, no immutability.","C2"),
 (7,"Commitments",NEW,"`PurchaseOrder.ProjectId` exists (Models/Context/Inventory/Inventory.cs:316) but NOTHING aggregates it: grep for a commitment computation over PurchaseOrders returns no service.","Construction (reads Purchasing)","Committed cost is invisible, so budget control cannot check a PO before approval and Budget-vs-Committed-vs-Actual cannot be produced.","C2"),
 (8,"Actual Cost",EXTEND,"Three real posting paths, all to 510104 tagged ProjectId: material (BL/ProjectMaterialIssueService.cs:98-102 via StockService), labor (BL/ProjectLaborService.cs:100-101 reclass), equipment depreciation (Models/Context/Accounting/EquipmentDepreciationAllocation.cs).","Accounting (posting) + Construction (dimensions)","Only MATERIAL is attributable below project level (ProjectMaterialIssueLine.BoqItemId). Labor and equipment land as project-level cost only — ProjectBudgetService.UnattributedActual is the honest name for that hole (ProjectBudgetService.cs:28).","C2"),
 (9,"Forecast Cost",NEW,"absent — searched BL/ for forecast/ETC/EAC; nothing.","Construction","No forward view; a project can only be reported after the fact.","C2"),
 (10,"Estimate at Completion",NEW,"absent — no EAC anywhere in BL/.","Construction","Overrun is discovered at the end instead of predicted.","C2"),
 (11,"Client Contracts",INADEQUATE,"There is no contract entity. Contract terms are five nullable columns on the project dimension: ContractValue, AdvancePercent, RetentionPercent (Dimensions.cs:38-44) + CustomerId, Location.","Construction","One project can hold exactly one implicit contract; no contract number, currency, scope, payment terms, bonds, insurance, warranty, DLP, variation/claim rules, attachments or approvals.","C3"),
 (12,"Subcontractor Contracts",INADEQUATE,"Models/Context/Accounting/Subcontract.cs:6-18 — nine fields (ProjectId, VendorId, Description, ContractValue, RetentionPercent, Status).","Construction (reuses Vendor)","No assigned BOQ/WBS scope, no quantities or rates, no dates, no advance, no penalties, no bonds, no insurance, no milestones, no termination, no documents, no approvals.","C3"),
 (13,"Supplier commitments",NEW,"PurchaseOrder.ProjectId (Inventory.cs:316) is the only hook; no commitment ledger, no cost-code allocation on PurchaseOrderLine.","Construction (reads Purchasing)","Material commitments cannot be set against a cost code or a budget line.","C2"),
 (14,"Client Progress Certificates",EXTEND,"Models/Context/Accounting/ProgressBilling.cs + BL/ProgressBillingService.cs (272 lines). Cumulative logic is sound: period = cumulative - previously posted (ProgressBillingService.cs:110), posts via ReceivableService only.","Construction (posting stays Accounting)","Three states only — Draft/Approved/Posted (ProgressBilling.cs:18). No Site-Engineer/QS/PM/Commercial/Finance review chain, no submitted-vs-certified split, no materials-on-site, no penalties or ad-hoc deductions, no BOQ-revision snapshot on the line, no line-level audit, no reversal path.","C6"),
 (15,"Subcontractor Certificates",INADEQUATE,"Models/Context/Accounting/Subcontract.cs:20-44 + BL/SubcontractBillingService.cs. `CumulativeWork` is a single free-typed decimal (SaveBillingDraftAsync param) and `Compute` only does w = cumulative - previouslyBilled (SubcontractBillingService.cs:80).","Construction","NO LINES AT ALL and NO CAP against Subcontract.ContractValue or against any allocated scope — a subcontractor can be certified without limit. No retention release schedule, no penalties, no advance.","C6"),
 (16,"Retention",REUSABLE,"BL/ContractService.cs:35,58 — retention held as a Dr balance on 1104 read straight from posted GL lines tagged ProjectId; withheld inside the certificate as a settlement receipt (ProgressBillingService.cs:230-237); released by ReleaseRetentionAsync with a balance check (ContractService.cs:104-105). Sub side mirrors it on 2105.","Accounting","Percent lives on the project, not on a contract or a certificate line, so a tiered/threshold retention or a per-BOQ-item retention flag is not expressible.","C3"),
 (17,"Advance Payments",REUSABLE,"BL/ContractService.cs:62-91 — Dr cash / Cr 2104 as a LIABILITY, ProjectId-tagged, via JournalEntryService only.","Accounting","Advance percent is a project column; no advance guarantee, no expiry, no multiple advances with separate recovery rules.","C3"),
 (18,"Recovery of Advances",REUSABLE,"BL/ProgressBillingService.cs:120 — A = min(W x AdvancePercent, remaining 2104 balance): recovery cannot exceed what was actually received.","Accounting","Only pro-rata-per-certificate recovery; no milestone or back-loaded recovery method.","C6"),
 (19,"Performance Bonds",NEW,"absent — searched Models/ and BL/ for bond/guarantee; nothing.","Construction","Expiry cannot be tracked, so a bond can lapse silently.","C3"),
 (20,"Bank Guarantees",NEW,"absent — no guarantee register; Banking.cs covers accounts and reconciliation only.","Construction","Same exposure as #19; also no advance-payment-guarantee link to #17.","C3"),
 (21,"Insurance",NEW,"absent — no insurance entity in Models/.","Construction","Policy validity is not enforced at site entry or at certificate approval.","C3"),
 (22,"Variation Orders",INADEQUATE,"Models/Context/Accounting/VariationOrder.cs + BL/VariationOrderService.cs. Draft/Approved only.","Construction","On approve, an `Adjust` line OVERWRITES BoqItem.Quantity/UnitPrice in place (VariationOrderService.cs:128) keeping the old value only on the VO line. No client-approval states, no time impact, no budget revision, no subcontract impact, no documents, no per-line approval.","C7"),
 (23,"Claims",NEW,"absent — no claim entity anywhere in Models/.","Construction","Entitlement, correspondence and settlement live outside the system.","C7"),
 (24,"Delay Events",NEW,"absent — no delay entity.","Construction","Excusable/compensable analysis is impossible; EOT has no evidence trail.","C7"),
 (25,"Extension of Time",NEW,"absent — Project.EndDate (Dimensions.cs:32) can be edited with no reason, no history and no approval.","Construction","A contractual completion date can move silently.","C7"),
 (26,"Daily Site Reports",NEW,"absent — no site-report entity; searched Models/Context for daily/site.","Construction","The single most-used construction document does not exist; manpower, weather, delays and instructions are unrecorded.","C5"),
 (27,"Material Requests",NEW,"absent — Inventory has PurchaseRequest-style flows and InventoryApprovalService, but no site material request to a project/WBS.","Construction (reads Inventory)","Site demand is invisible until stock is already issued.","C4"),
 (28,"Site Warehouse",EXTEND,"Models/Context/Inventory/Inventory.cs:144-159 — `Warehouse` with WarehouseType (Main/Transit/Quarantine/Scrap/Consignment/Virtual) and BranchHierarchicalId.","Inventory","No SiteId and no ProjectId on a warehouse, so a site store is only a naming convention and cross-site access is unconstrained.","C4"),
 (29,"Material Issue",REUSABLE,"Models/Context/Accounting/ProjectMaterialIssue.cs + BL/ProjectMaterialIssueService.cs:98-102 — issues through StockService with CounterAccountOverride 510104, ProjectId-tagged, post-once via Status.","Inventory (writer) + Construction (document)","Line carries an optional BoqItemId only; no WBS, no cost code, no site, no requester/approver.","C4"),
 (30,"Material Return",NEW,"absent — no project material return document; the issue has no reversal path (ProjectMaterialIssueService only posts).","Construction (reads Inventory)","Surplus returned from site cannot be credited back to the project cost.","C4"),
 (31,"Material Transfer",REUSABLE,"Models/Context/Inventory/Inventory.cs:455 `StockTransfer` + BL/StockService.cs:99,670 TransferAsync; inter-branch GL mode is configurable (Inventory.cs:590).","Inventory","No project/WBS dimension on a transfer, so a site-to-site move is not visible in project cost.","C4"),
 (32,"Damaged/Waste Materials",EXTEND,"BL/StockService.cs:92,353 — write-off with Reason Damaged/Expired/Lost/Other; Warehouse type `Scrap` (Inventory.cs:151).","Inventory","No project/WBS attribution and no waste rate against BOQ allowance, so wastage cannot be measured per work item.","C4"),
 (33,"Manpower Logs",INADEQUATE,"BL/ProjectLaborService.cs:56-58 — labor is derived from TaskItems where EntityType == \"Project\" plus TimesheetEntries, costed by IEmployeeCostService.","HR (master+rates) + Construction (log)","No trade, no site, no WBS/activity, no overtime split, no productivity, no subcontractor labor. TaskItem has no ProjectId column at all (Models/Context/Tasks/TaskItem.cs:24 uses generic EntityType/EntityId).","C5"),
 (34,"Equipment Logs",INADEQUATE,"Models/Context/Accounting/EquipmentDepreciationAllocation.cs — a depreciation RECLASS to the project; Hours and Rate are optional helpers that only fill the amount.","Construction (log) + Accounting (posting)","Not a usage log: no operator, no site, no WBS, no idle hours, no downtime, no fuel, no meter reading.","C5"),
 (35,"Fuel Consumption",NEW,"absent — no fuel entity.","Construction","A major construction cost is untracked.","C5"),
 (36,"Equipment Downtime",NEW,"absent — Maintenance.cs covers schedules/records for assets, not project downtime.","Construction (reads Maintenance)","Utilisation and disruption claims have no data.","C5"),
 (37,"RFIs",NEW,"absent — no RFI entity.","Construction","Consultant queries and their response clock are untracked.","C8"),
 (38,"Inspection Requests",NEW,"absent — InventoryApprovalService is a stock approval, not a site inspection.","Construction","Work is covered up without a recorded approval.","C8"),
 (39,"Material Submittals",NEW,"absent.","Construction","Approved-material compliance cannot be evidenced.","C8"),
 (40,"Method Statements",NEW,"absent.","Construction","Method approval is not linked to the work it governs.","C8"),
 (41,"Drawing Register",NEW,"absent — the platform file manager (Models/Context/Library/LibraryItem.cs + BL/FileManagerService.cs) is a folder tree with soft delete and no document metadata.","Construction (metadata) + Platform (storage)","No document number, discipline, status or distribution.","C8"),
 (42,"Revision Control",NEW,"absent — LibraryItem has no revision, no supersede link and no current-revision flag (LibraryItem.cs:6-18).","Construction","A superseded drawing is indistinguishable from the current one — the site can build to the wrong revision.","C8"),
 (43,"As-Built Documents",NEW,"absent.","Construction","Handover has no controlled as-built set.","C8"),
 (44,"Progress Measurement",EXTEND,"Models/Context/Accounting/ProjectProgress.cs + BL/ProgressService.cs (291 lines) — dated cumulative snapshots per BOQ leaf item, Draft/Confirmed.","Construction","Executed value is silently CAPPED at BOQ value (ProgressService.cs:85) and over-quantity only raises a UI flag (`OverBoq`, ProgressService.cs:162) — real over-execution is truncated, not blocked and not routed to a variation. ConfirmAsync does not check the current status (ProgressService.cs:256-263).","C6"),
 (45,"Physical Progress",EXTEND,"BL/ProgressService.cs:185 — OverallPercent = sum(executed value) / sum(BOQ value): a VALUE-weighted percentage.","Construction","Value-weighted is a financial proxy for physical progress; no milestone, weighted-activity or manual-approved method, and no planned-vs-actual.","C6"),
 (46,"Financial Progress",REUSABLE,"BL/ProgressBillingService.cs — certified value per certificate, cumulative per BOQ item; BL/ProjectService.ProfitabilityAsync for revenue vs cost from the GL.","Accounting","Certified and paid are not separated in a project view (payment status lives on the invoice).","C6"),
 (47,"Project Cash Flow",NEW,"absent — no cash-flow projection for a project in BL/.","Construction (Reporting renders)","No forward funding view; retention release and advance recovery timing are invisible.","C9"),
 (48,"Cost Variance",EXTEND,"BL/ProjectBudgetService.cs:17 — Variance = Estimated - Actual per BOQ leaf, plus an Unattributed bucket.","Construction","Variance is BOQ-estimate vs actual, not budget vs committed vs actual vs forecast; and only material is attributed.","C2"),
 (49,"Schedule Variance",NEW,"absent — no baseline schedule; the project has only StartDate/EndDate (Dimensions.cs:31-32).","Construction","Delay cannot be quantified from stored data.","C7"),
 (50,"Project Timeline",DEFERRED,"BL/Platform/EntityRegistry.cs:219 — the `Project` registry entry is `SupportsTimeline = false, SupportsComments = false, SupportsFiles = false, PermissionScope = ScopeNone`; it is registered for search and the record picker only.","THIRD TAB (Communication)","Construction defines the events and payloads; onboarding the timeline is the third tab's work.","C9"),
 (51,"Site Photos",NEW,"storage is reusable (LibraryItem + FileManagerService); the construction binding, geotag and daily-report linkage are absent.","Construction (metadata) + Platform (storage)","Photographic evidence cannot be attached to a work item or a progress claim.","C5"),
 (52,"Health and Safety Events",NEW,"absent — no incident entity.","Construction","Legally required incident records are not held.","C5"),
 (53,"Punch Lists",NEW,"absent.","Construction","Snag closure at handover is untracked.","C8"),
 (54,"Handover",NEW,"absent — no handover document.","Construction","Practical completion has no record and no trigger for the DLP clock.","C8"),
 (55,"Defects Liability Period",NEW,"absent — no DLP dates; retention release (ContractService.cs:95) is a manual amount with no DLP gate.","Construction","Retention can be released before the defect period ends.","C3"),
 (56,"Project Closeout",NEW,"`ProjectsActions.Close` exists as a PERMISSION (BL/ProjectsAccessService.cs:41) but no closeout process, checklist or lock exists behind it.","Construction","A project can keep accepting cost and certificates after completion.","C8"),
 (57,"Construction Reporting",DEFERRED,"The reporting platform is live (BL/Reporting/, 26 files; ReportDefinition/IReportDataSource contracts) and deliberately ships NO module data sources yet (BL/Reporting/PlatformReportDefinitions.cs:14-19).","SECOND TAB (Reporting)","Construction supplies read-only data-source contracts and DTOs; the second tab builds definitions and renderers.","C9"),
 (58,"Mobile Site Operations",NEW,"crossbuy_mobile/lib/screens/ holds 10 screens (dashboard, finance, home, inventory, leave, login, notifications, profile, statistics, structure) — none project or site.","Construction","Site data continues to arrive on paper.","C10"),
 (59,"Approvals",EXTEND,"Two module-specific patterns exist: Models/Context/Admin/LeaveApprovalStep.cs + BL/LeaveWorkflowService.cs, and BL/InventoryApprovalService.cs. There is no generic approval engine.","Platform (should own) + Construction (consumer)","Certificates, variations and budgets each need a multi-step chain; without a shared engine each would reinvent one.","C3"),
 (60,"Audit and traceability",INADEQUATE,"Construction entities carry only CreatedBy/CreatedAt/PostedBy/PostedAt (e.g. ProgressBilling.cs:31-33). Zero RowVersion and zero CHECK constraints across deploy/sql/boq.sql and deploy/sql/projects_p*.sql (verified by grep, count 0).","Construction","No field-level history, no reason capture, no immutable line history, no optimistic concurrency — a commercial value can be overwritten with no trace of the previous one.","C1"),
]

CAPABILITY_FIELDS = ["ID","Capability","Status","Evidence","Owner","Gap","TargetPhase"]

# ==============================================================================================
# 2. ENTITY CATALOG — existing construction-relevant entities + proposed new ones.
#    Isolation is the brief's classification: company / project / branch / site / global reference.
# ==============================================================================================
ENTITIES = [
 # --- existing (assessed, NOT redesigned here) ---
 ("Project","Existing","CrossBuy/Models/Context/Accounting/Dimensions.cs:23","company + project","Extend: SiteId, BranchID, ParentId, ContractCurrency, ProjectTypeId; keep every existing column."),
 ("ProjectActivityType","Existing","Dimensions.cs:49","company","Reuse as-is (user-defined lookup)."),
 ("ProjectMember","Existing","Models/Context/Accounting/ProjectMember.cs:16","company + project","Reuse as-is — the record-level access relationship (tab 1 owned)."),
 ("CostCenter","Existing","Dimensions.cs:7","company","Reuse for ORG rollup only; must not be overloaded as a construction cost code."),
 ("BoqItem","Existing","Models/Context/Accounting/Boq.cs:8","company + project","Extend with WbsNodeId, CostCodeId, RevisionId, Status, Source, RowVersion; retire ReplaceAllAsync."),
 ("ProjectProgress / ProjectProgressLine","Existing","Models/Context/Accounting/ProjectProgress.cs","company + project","Extend with WBS/site reference, progress method, over-quantity block, BOQ revision snapshot."),
 ("ProgressBilling / ProgressBillingLine","Existing","Models/Context/Accounting/ProgressBilling.cs","company + project","Extend into the client certificate: review chain, BoqRevisionId, MOS, deductions, RowVersion, line audit."),
 ("Subcontract / SubcontractBilling","Existing","Models/Context/Accounting/Subcontract.cs","company + project","Extend heavily: scope allocation lines, quantity cap, dates, advance, penalties, certificate lines."),
 ("VariationOrder / VariationOrderLine","Existing","Models/Context/Accounting/VariationOrder.cs","company + project","Extend states; STOP in-place BOQ mutation — emit a BOQ revision instead."),
 ("ProjectMaterialIssue / Line","Existing","Models/Context/Accounting/ProjectMaterialIssue.cs","company + project + site","Extend line with WbsNodeId, CostCodeId, SiteId, requester/approver."),
 ("EquipmentDepreciationAllocation","Existing","Models/Context/Accounting/EquipmentDepreciationAllocation.cs","company + project","Keep as the costing reclass; the usage log becomes a separate construction entity."),
 ("Warehouse","Existing","Models/Context/Inventory/Inventory.cs:144","company + branch","Extend with SiteId (nullable) — Inventory-owned change, must be agreed with the Inventory owner."),
 ("PurchaseOrder","Existing","Models/Context/Inventory/Inventory.cs:300","company","Already carries ProjectId; commitment reading is additive and read-only."),
 ("LibraryItem","Existing","Models/Context/Library/LibraryItem.cs:6","company","Reuse as the ONLY file store; construction adds metadata rows that reference it."),
 # --- proposed new construction entities ---
 ("ConstructionSite","New","proposed","company + project","A physical site/location under a project; parent of site stores, DSRs, manpower and equipment logs."),
 ("WbsNode","New","proposed","company + project","The work breakdown node: Code, Name, ParentId, Path, Sequence, Status, dates, owner, ProgressMethod, depth-limited, no cross-project parent."),
 ("CostCode","New","proposed","company (global reference per company)","Cost breakdown code with a Category (Material/Labor/Equipment/Subcontractor/SiteExpense/Transportation/Accommodation/Overhead/Contingency/Other)."),
 ("BoqRevision","New","proposed","company + project","Immutable header for a BOQ version: RevisionNo, Kind (Original/Revised/Approved), Source (Contract/Variation), ApprovedBy/At, reason."),
 ("BoqLineHistory","New","proposed","company + project","Immutable per-line history: old/new quantity, rate, unit, reason, actor, revision."),
 ("ProjectBudget / ProjectBudgetLine","New","proposed","company + project","Versioned budget by WBS x CostCode: Original/Revised/Approved/Baseline/Scenario, immutable once approved."),
 ("BudgetTransfer","New","proposed","company + project","From/To budget line, amount, reason, approval chain."),
 ("ProjectCommitment","New","proposed","company + project","Read-model + record of committed cost from PO / subcontract, allocated to WBS x CostCode."),
 ("CostAllocation","New","proposed","company + project","The construction dimension row for any cost fact: SourceEntityType/Id + Project/WBS/CostCode/BOQ/Contract/Site/Branch."),
 ("ClientContract / ClientContractTerm","New","proposed","company + project","The contract as a first-class document with currency, terms, bonds, insurance, DLP, attachments, approvals."),
 ("SubcontractScope","New","proposed","company + project","Allocated BOQ/WBS scope with quantity and rate — the cap that stops over-certification."),
 ("Certificate / CertificateLine","New (extends ProgressBilling)","proposed","company + project","Client and subcontractor certificate with the full review chain and a BOQ-revision snapshot per line."),
 ("Bond / Insurance","New","proposed","company + project","Guarantee/insurance register with validity, expiry alerting and issuer."),
 ("Claim / DelayEvent","New","proposed","company + project","Entitlement, cause, responsible party, cost and time impact, evidence, settlement."),
 ("MaterialRequest / Line","New","proposed","company + project + site","Site demand with WBS/CostCode/BOQ reference and approval."),
 ("DailySiteReport + child logs","New","proposed","company + project + site","Structured DSR: manpower, equipment, quantities, materials, visitors, delays, incidents, photos."),
 ("ManpowerLog / EquipmentLog","New","proposed","company + project + site","Per-day, per-trade / per-asset hours with WBS reference and cost rate source."),
 ("Rfi / InspectionRequest / Submittal / MethodStatement / Ncr / PunchItem","New","proposed","company + project","Quality and technical control documents with a common header shape."),
 ("DocumentRegister / DocumentRevision","New","proposed","company + project","Controlled register: number, revision, discipline, status, supersede link, current flag, distribution; file bytes stay in LibraryItem."),
 ("ProgressRecord","New","proposed","company + project","Planned vs actual vs approved vs certified progress per WBS node, per method."),
 ("CashFlowPlan / CashFlowLine","New","proposed","company + project","Planned/committed/actual/forecast inflow and outflow by period."),
 ("ConstructionAudit","New","proposed","company + project","Field-level immutable history: entity, field, old, new, reason, actor, correlation, revision."),
]
ENTITY_FIELDS = ["Entity","State","Evidence","Isolation","Decision"]

# ==============================================================================================
# 3. SCREEN CATALOG (Phase / Screen Inventory) — 32 future screens. NO visual design here.
# ==============================================================================================
SCREENS = [
 (1,"Construction Dashboard","Portfolio health for construction projects","Executive, Construction Director, PM","projects, progress, cost, certificates, cash","drill to project workspace","read-only","live","construction.project.read","summary tiles","Executive Project Dashboard","Reporting","which KPIs are confidential"),
 (2,"Construction Projects","List and create construction projects","PM, Commercial, Admin","project master + contract summary","create, edit, archive","code unique per company; no company-1 fallback","active/on-hold/closed","construction.project.read/manage","list only","BOQ Summary","Projects","one or many client contracts per project"),
 (3,"Project Workspace","Single project cockpit","PM, QS, Site Engineer","everything scoped to one project","navigate to every sub-screen","membership or role required","by project status","construction.project.read","summary","Physical vs Financial Progress","all","which tabs a Member vs Observer sees"),
 (4,"WBS Designer","Build and restructure the work breakdown","PM, Planner","WbsNode tree","add, move, archive; impact preview","depth limit; no cross-project parent; no silent re-parent after financial use","draft/active/archived","construction.wbs.manage","read-only tree","Cost Code Analysis","-","max depth; re-parent policy after cost exists"),
 (5,"BOQ Workspace","Maintain BOQ by revision","QS, Commercial","BoqRevision + items","new revision, submit, approve, compare","approved revision immutable; qty change needs revision or variation","draft/approved/superseded","construction.boq.manage/approve","read-only","BOQ Summary","-","one BOQ or separate client/internal structures"),
 (6,"Cost Code Designer","Maintain the cost code set","Commercial, Finance","CostCode","add, deactivate, map to account","cannot delete a code with postings","active/inactive","construction.costcode.manage","none","Cost Code Analysis","Accounting","who may change a code after use"),
 (7,"Project Budget","Versioned budget by WBS x CostCode","Commercial, Finance","ProjectBudget lines","new version, transfer, approve","approved version immutable; transfer needs approval","draft/approved/superseded","construction.budget.manage/approve","read-only","Budget vs Committed vs Actual","Accounting","overspend blocks or warns"),
 (8,"Cost Control","Budget/committed/actual/forecast/EAC","Commercial, PM, Finance","CostAllocation aggregates","set forecast, explain variance","forecast is a stated method, never silent","live","construction.cost.read + confidential view","read-only","Forecast at Completion","Accounting, Purchasing","forecast methodology"),
 (9,"Client Contract","The contract and its terms","Commercial, Legal, PM","ClientContract + bonds/insurance","create, amend, approve","amendment keeps previous values","draft/active/closed","construction.contract.manage/approve","read-only","Client Certificate Register","Accounting","multiple active contracts per project"),
 (10,"Subcontractor Contracts","Subcontracts and allocated scope","Commercial, PM","Subcontract + SubcontractScope","create, allocate scope, approve","allocated qty cannot exceed available BOQ scope without approval","draft/active/terminated","construction.subcontract.manage/approve","read-only","Subcontractor Certificate Register","Purchasing","over-certification policy"),
 (11,"Client Certificates","Raise and progress a client certificate","QS, PM, Commercial, Finance","Certificate + lines","submit, review, approve, post","cumulative cannot exceed approved quantity without a variation; previous certified immutable","full review chain","construction.certificate.create/review/approve/post","approve only","Client Certificate Register","Accounting","approval limits by value"),
 (12,"Subcontractor Certificates","Certify subcontractor work","QS, PM, Finance","Subcontract certificate + lines","submit, review, approve, post","capped at allocated scope","full review chain","construction.subcertificate.*","approve only","Subcontractor Certificate Register","Purchasing, Accounting","retention release schedule"),
 (13,"Variation Orders","Raise, estimate and approve variations","PM, QS, Commercial","Variation + lines + impacts","estimate, submit, approve, implement","approval emits a BOQ revision, never an in-place edit","full VO chain","construction.variation.create/estimate/approve","read-only","Variation Register","Accounting","who approves what value"),
 (14,"Claims","Claim register and entitlement","Commercial, Legal","Claim + evidence","raise, respond, settle","settlement needs an authority","draft/submitted/settled","construction.claim.manage","evidence capture","Claim Register","-","claim approval authority"),
 (15,"Delay Events","Record delays and EOT","PM, Planner","DelayEvent","record, classify, request EOT","excusable/compensable classified explicitly","open/closed","construction.claim.manage","capture on site","Claim Register","-","EOT approval route"),
 (16,"Material Requests","Site demand for material","Site Engineer, Storekeeper","MaterialRequest lines","raise, approve, reserve","must carry project + WBS + cost code","draft/approved/issued","construction.materialrequest.*","full","Material Consumption","Inventory","cross-site warehouse access"),
 (17,"Site Store","Site stock view and issue/return","Storekeeper","stock balances for site warehouses","issue, return, transfer","issue goes through StockService only","live","construction.site.operate","full","Material Consumption","Inventory","who may issue"),
 (18,"Daily Site Reports","Structured daily record","Site Engineer, PM","DSR + child logs","create, submit, review, approve","quantities reference WBS/BOQ","draft/submitted/approved","construction.sitereport.create/review","offline draft + photo","Daily Site Report","HR, Inventory","what makes a DSR mandatory"),
 (19,"Manpower Log","Daily manpower by trade","Site Engineer","ManpowerLog","record, submit","attendance reference where an employee exists","draft/approved","construction.sitereport.create","full","Manpower Report","HR","subcontractor labor identity"),
 (20,"Equipment Log","Daily equipment hours and downtime","Site Engineer","EquipmentLog","record, submit","asset must exist in FixedAssets or be a rental line","draft/approved","construction.sitereport.create","full","Equipment Utilization","Accounting, Maintenance","rented vs owned rate source"),
 (21,"RFIs","Raise and answer RFIs","Site Engineer, Consultant liaison","Rfi","raise, respond, close","response due date tracked","open/answered/closed","construction.rfi.create/respond/approve","create + photo","RFI Register","-","external consultant access"),
 (22,"Inspections","Request and record inspections","QC, Site Engineer","InspectionRequest","request, inspect, approve/reject","no cover-up before approval where configured","requested/passed/failed","construction.inspection.*","full + signature","Inspection Register","-","who signs off"),
 (23,"Material Submittals","Approved-material control","QC, Technical Office","Submittal","submit, review, approve","approved material list drives procurement checks","draft/approved/rejected","construction.submittal.*","read-only","Inspection Register","Purchasing","binding on procurement or advisory"),
 (24,"Method Statements","Method approval","Technical Office, QC","MethodStatement","submit, review, approve","linked to the WBS it governs","draft/approved","construction.submittal.*","read-only","-","-","-"),
 (25,"Drawing Register","Controlled drawing register","Document Controller","DocumentRegister + revisions","issue revision, supersede, distribute","only one current revision; superseded is visibly blocked","current/superseded","construction.drawing.manage/approve","read + warning","Drawing Register","Platform files","drawing approval workflow"),
 (26,"Document Revision Viewer","View a revision safely","everyone on the project","DocumentRevision","open, download","a superseded revision must warn and be marked","current/superseded","construction.drawing.read","read","Drawing Register","Platform files","whether superseded download is blocked or warned"),
 (27,"Progress Workspace","Planned/actual/approved/certified progress","PM, QS, Planner","ProgressRecord per WBS","record, submit, approve","physical progress never derived from invoice value","draft/approved","construction.progress.*","record on site","Physical vs Financial Progress","-","physical progress method per project"),
 (28,"Project Cash Flow","Forward inflow/outflow","Commercial, Finance","CashFlowPlan lines","plan, scenario, compare","planned vs committed vs actual separated","planned/forecast","construction.cost.read + financial detail","read-only","Project Cash Flow","Accounting","forecast methodology"),
 (29,"Project Cost Analysis","Cost by code, WBS, source","Commercial, Finance","CostAllocation","drill, export","confidential commercial data gated","live","construction.cost.read + confidential","read-only","Cost Code Analysis","Accounting","who sees margin"),
 (30,"Handover and Closeout","Practical completion and closeout","PM, Commercial","handover checklist, as-builts","complete, close","closeout locks new cost and certificates","open/handed-over/closed","construction.project.close","checklist","Project Profitability","Accounting","closeout rules"),
 (31,"Defects Liability","DLP tracking and retention release","PM, Commercial, Finance","DLP dates, defects, retention","log defect, release retention","retention release gated by DLP end","active/expired","construction.certificate.post + budget","defect capture","Retention Report","Accounting","retention release trigger"),
 (32,"Construction Settings","Module policy and defaults","Construction Admin","policies, methods, limits","configure","policy changes are audited","-","construction.project.manage","none","-","Platform capabilities","which policies are per-company vs per-project"),
]
SCREEN_FIELDS = ["ID","Screen","Purpose","Roles","Data","Actions","Validation","States","Permissions","MobileNeeds","Reports","Integrations","UnresolvedDecisions"]

# ==============================================================================================
# 4. BUSINESS EVENT CATALOG (Phase 19) — PROPOSALS ONLY. Nothing is raised in this increment.
#    Names follow the kernel contract: "<EntityCode>.<Action>", PascalCase action, registry-validated.
# ==============================================================================================
EVENTS = [
 ("ConstructionProject.Created","v1","ConstructionProject","ProjectId","required","required","creator","code, name, type, site, contract currency","contract value","Timeline, Notification"),
 ("WbsNode.Created","v1","WbsNode","WbsNodeId","required","required","creator","code, name, parent, path, method","-","Timeline"),
 ("Boq.Approved","v1","BoqRevision","BoqRevisionId","required","required","approver","revision no, kind, source, line count, total value","total value, rates","Timeline, Notification, Cost control"),
 ("ProjectBudget.Approved","v1","ProjectBudget","BudgetId","required","required","approver","version, total by cost code","amounts","Timeline, Notification"),
 ("ClientContract.Approved","v1","ClientContract","ContractId","required","required","approver","contract no, customer, currency, dates","value, terms","Timeline, Notification"),
 ("Subcontract.Approved","v1","Subcontract","SubcontractId","required","required","approver","vendor, scope summary, dates","value, rates","Timeline, Notification"),
 ("ProgressCertificate.Submitted","v1","Certificate","CertificateId","required","required","submitter","certificate no, period, cumulative qty","values","Timeline, Notification, Approval inbox"),
 ("ProgressCertificate.Approved","v1","Certificate","CertificateId","required","required","approver","certificate no, net payable","values, deductions","Timeline, Notification"),
 ("ProgressCertificate.Posted","v1","Certificate","CertificateId","required","required","poster","invoice id, receipt ids","values","Timeline, Notification, Cash flow"),
 ("Variation.Raised","v1","Variation","VariationId","required","required","originator","vo no, reason, scope summary","cost impact","Timeline, Notification"),
 ("Variation.Approved","v1","Variation","VariationId","required","required","approver","vo no, boq revision id, time impact days","cost impact","Timeline, Notification, Cost control"),
 ("Claim.Raised","v1","Claim","ClaimId","required","required","originator","claim type, cause, responsible party","entitlement value","Timeline, Notification"),
 ("Delay.Recorded","v1","DelayEvent","DelayEventId","required","required","recorder","date, cause, affected wbs, classification","cost impact","Timeline, Notification"),
 ("MaterialRequest.Raised","v1","MaterialRequest","MaterialRequestId","required","required","requester","site, wbs, items, quantities","-","Timeline, Notification"),
 ("MaterialRequest.DeliveredToSite","v1","MaterialRequest","MaterialRequestId","required","required","storekeeper","site, warehouse, quantities, movement ids","unit cost","Timeline, Cost control"),
 ("DailySiteReport.Submitted","v1","DailySiteReport","DailySiteReportId","required","required","submitter","date, site, manpower total, equipment total, quantities","cost rates","Timeline, Notification"),
 ("Rfi.Raised","v1","Rfi","RfiId","required","required","originator","rfi no, discipline, due date","-","Timeline, Notification"),
 ("Inspection.Approved","v1","InspectionRequest","InspectionRequestId","required","required","inspector","inspection no, wbs, result","-","Timeline, Notification"),
 ("DrawingRevision.Issued","v1","DocumentRevision","DocumentRevisionId","required","required","document controller","document no, revision, discipline, supersedes","-","Timeline, Notification, Site warning"),
 ("ConstructionProject.HandoverCompleted","v1","ConstructionProject","ProjectId","required","required","project manager","handover date, as-built set, DLP end","retention balance","Timeline, Notification, Retention"),
]
EVENT_FIELDS = ["EventName","Version","Aggregate","EntityId","Company","Project","Actor","Payload","SensitiveFields","ProposedConsumers"]

# ==============================================================================================
# 5. RISK REGISTER — every row carries a real path. No invented counts.
# ==============================================================================================
RISKS = [
 ("CR-01","Critical","High","BOQ full-replace orphans certified history and can re-bill work already billed",
  "BL/BoqService.cs:108-109 deletes every BoqItem of the project and re-inserts with NEW IDs; BL/ProgressBillingService.cs:77-85 keys previously-billed value by BoqItemId. After a re-save the previously-billed lookup finds nothing, so period value = full cumulative again.",
  "BOQ, client certificates, budget report, progress","Replace ReplaceAllAsync with revision-based editing; forbid delete/reinsert once any progress, certificate or issue references an item.","Construction owner + Commercial","Open","C2 gate"),
 ("CR-02","Critical","High","Subcontractor over-certification is unbounded",
  "BL/SubcontractBillingService.cs:80 computes W = cumulative - previouslyBilled with NO comparison to Subcontract.ContractValue and no scope lines; SubcontractBilling has no line detail at all (Models/Context/Accounting/Subcontract.cs:20-44).",
  "Subcontract certificates, AP, project cost","Introduce SubcontractScope with quantity/rate and cap cumulative certified per scope line; block or require approval above the cap.","Commercial","Open","C3 gate"),
 ("CR-03","Critical","Medium","Variation approval overwrites contractual BOQ values in place",
  "BL/VariationOrderService.cs:128 sets it.Quantity/it.UnitPrice on the live BoqItem; the only trace is OldQuantity/OldUnitPrice on the VO line (Models/Context/Accounting/VariationOrder.cs:44-45). Certificates that already used the old rate keep no snapshot (ProgressBillingLine has no revision column).",
  "BOQ, variations, all prior certificates","Variation approval must emit a BoqRevision; certificate lines must store the BoqRevisionId they were certified against.","Commercial","Open","C7 gate"),
 ("CR-04","High","High","Client certificate posting is not atomic",
  "BL/ProgressBillingService.cs:202-258 — invoice, retention receipt and advance-recovery receipt are three separate calls with no ambient transaction (no ScopedTx in the file). A failure at step 3 leaves a posted invoice and a posted retention receipt with the certificate still 'Approved'.",
  "Accounting (AR), certificates","Wrap the certificate posting in one ambient transaction, or make it resumable with an explicit partial-post state. Accounting-owned change — must be agreed with the Accounting owner.","Accounting owner","Open","C6 gate"),
 ("CR-05","High","Medium","Posted document ids are recovered by MAX(ID) instead of being returned",
  "BL/ProgressBillingService.cs:233-236 and 242-245 read MAX(Receipt.ID) before and after the call; BL/SubcontractBillingService.cs:164-167 does the same for Payments. A concurrent receipt for the same customer/vendor mis-links the certificate.",
  "Certificates, AR/AP reconciliation","Have the payable/receivable services return the created id (their change), or key on the certificate's own source reference.","Accounting owner","Open","C6 gate"),
 ("CR-06","High","High","No optimistic concurrency anywhere in the construction schema",
  "grep for RowVersion/timestamp over deploy/sql/boq.sql and deploy/sql/projects_p*.sql returns 0 in every file; no construction entity declares a concurrency token.",
  "BOQ, budget, certificates, contracts, variations, progress, documents","Add RowVersion to every commercially significant construction entity and a compare/reload UX; never last-write-wins on a value.","Construction owner","Open","C1 gate"),
 ("CR-07","High","Medium","Construction tables carry almost no database-level integrity",
  "Across boq.sql and projects_p*.sql there are 4 FOREIGN KEY/REFERENCES occurrences (all line->header) and 0 CHECK constraints; BoqItems has no FK to Projects and no company/project composite guard (deploy/sql/boq.sql).",
  "Every construction table","Additive idempotent SQL adding FK, composite (CompanyID, ProjectId) guards and CHECKs for status/percent/quantity domains.","Construction owner","Open","C1 gate"),
 ("CR-08","High","Medium","Over-execution is silently truncated instead of raised as a variation",
  "BL/ProgressService.cs:85 caps executed value at min(cumQty, boqQty) x unitPrice; line 162 only sets a UI boolean OverBoq. Nothing blocks, notifies or routes it.",
  "Progress, certificates, variations","Make over-quantity an explicit decision: block, or require an approved variation, and record the excess.","Commercial","Open","C6 gate"),
 ("CR-09","High","High","Project create/edit/delete is ungated and hardcoded to company 1",
  "CrossBuy/Controllers/ProjectController.cs:180-203 — SaveProject and DeleteProject call no GateAsync and pass DefaultCompanyId (=1, line 20). The controller uses DefaultCompanyId 61 times while only 24 actions call GateAsync.",
  "Project master, company isolation","FIRST-TAB owned (authorization). Reported, not modified: extend the D1 gate to the remaining project actions and resolve the company from IRequestCompanyResolver.","First tab (Authorization)","Open — referred","C0 gate"),
 ("CR-10","Medium","High","Construction journals pick an arbitrary currency",
  "_db.Currencies.Select(c => c.ID).FirstOrDefaultAsync() with no company filter and no functional-currency selection appears 5 times across BL/ContractService.cs, BL/ProjectLaborService.cs and BL/EquipmentDepreciationService.cs.",
  "Advance, retention release, labor, equipment reclass","Accounting-owned: resolve the company functional currency. Construction must not add a sixth occurrence and needs a contract currency on the contract entity.","Accounting owner","Open","C3 gate"),
 ("CR-11","Medium","High","Cost is untraceable below project level for labor and equipment",
  "BL/ProjectLaborService.cs:100-101 posts only ProjectId; EquipmentDepreciationAllocation carries only ProjectId. BL/ProjectBudgetService.cs:28 names the consequence: UnattributedActual.",
  "Cost control, budget variance","Introduce CostAllocation carrying Project/WBS/CostCode/BOQ/Site and populate it at every cost source; do not change posting behaviour in this increment.","Construction owner","Open","C2 gate"),
 ("CR-12","Medium","Medium","Retention can be released before the defects liability period ends",
  "BL/ContractService.cs:95-121 checks only that the amount does not exceed the GL retention balance; there is no DLP date anywhere in the model.",
  "Retention, closeout","Add DLP dates to the contract and gate release on them, with an explicit override that is authorised and audited.","Commercial + Finance","Open","C3 gate"),
 ("CR-13","Medium","High","A superseded drawing is indistinguishable from the current one",
  "Models/Context/Library/LibraryItem.cs:6-18 — no revision, no supersede link, no current flag; BL/FileManagerService.cs treats items as a folder tree only.",
  "Document control, site execution","Construction DocumentRegister/DocumentRevision with a single current revision, a supersede link and a hard site warning; files stay in LibraryItem.","Construction owner","Open","C8 gate"),
 ("CR-14","Medium","Medium","No approval chain exists for commercial documents",
  "Certificates have three states (Models/Context/Accounting/ProgressBilling.cs:18); the only approval engines are Models/Context/Admin/LeaveApprovalStep.cs and BL/InventoryApprovalService.cs, both module-specific.",
  "Certificates, variations, budgets","Ask the platform for a shared approval engine; if it is not offered, define a construction-local chain rather than copying the HR one.","Construction owner + Platform","Open","C3 gate"),
 ("CR-15","Medium","Medium","Audit history is create/post stamps only",
  "Construction entities carry CreatedBy/CreatedAt/PostedBy/PostedAt only (e.g. Models/Context/Accounting/ProgressBilling.cs:31-33); no field-level history table exists.",
  "Every commercial value","ConstructionAudit with old/new/reason/actor/correlation, written by the owning service in the same transaction.","Construction owner","Open","C1 gate"),
 ("CR-16","Medium","Low","Project confirm/close has no state machine",
  "BL/ProgressService.cs:256-263 ConfirmAsync sets Status with no current-status check; Project.Status is a free string (Models/Context/Accounting/Dimensions.cs:39); ProjectsActions.Close exists with no process behind it (BL/ProjectsAccessService.cs:41).",
  "Progress, project lifecycle","Explicit state machines with allowed transitions and an audited reason.","Construction owner","Open","C1 gate"),
 ("CR-17","Medium","Medium","Site is not a modelled dimension",
  "Warehouse carries BranchHierarchicalId only (Models/Context/Inventory/Inventory.cs:152); no entity in Models/ has a SiteId. GoodsReceipt has no ProjectId (Inventory.cs:339-357).",
  "Site operations, material, manpower, equipment","Introduce ConstructionSite and reference it from construction documents; a Warehouse.SiteId is an Inventory-owned change to be agreed.","Construction + Inventory owner","Open","C4 gate"),
 ("CR-18","Low","Medium","Offline site capture will duplicate records without an idempotency key",
  "crossbuy_mobile/lib/screens/ has no project or site screen today, so the contract can still be set before any client exists.",
  "Mobile site operations","Every offline-capable construction document defines a client-generated idempotency key and a documented conflict policy before the first mobile screen is built.","Construction owner","Open","C10 gate"),
 ("CR-19","Low","High","Construction has no automated test coverage",
  "CrossBuy.Tests/ holds 50+ test files, none construction: grep for boq/certificate/subcontract test classes returns none; the project-related hits are access-service and gate tests only.",
  "Every construction rule","Each construction slice ships tests for its invariants (cap, immutability, isolation, concurrency) in the existing xUnit project.","Construction owner","Open","C1 gate"),
 ("CR-20","Low","Medium","Construction is not onboarded to the platform timeline or files",
  "BL/Platform/EntityRegistry.cs:219 — Project: SupportsTimeline=false, SupportsComments=false, SupportsFiles=false, PermissionScope=ScopeNone.",
  "Timeline, comments, files","THIRD-TAB owned. Construction defines the events and payloads (see the event catalog) and requests onboarding; it does not write consumers.","Third tab (Communication)","Open — referred","C9 gate"),
]
RISK_FIELDS = ["ID","Severity","Probability","Impact","Evidence","AffectedScope","Mitigation","Owner","Status","Gate"]

# ==============================================================================================
# 6. ROADMAP — dependency-ordered. "Blocks" is the dependency the brief requires us to respect.
# ==============================================================================================
ROADMAP = [
 ("C0","Assessment and decisions","This increment: inventory, matrix, boundaries, design, catalogs, decisions","none","C1..C10","Owner review of the decision register; referral of CR-09 to the first tab","Delivered by this increment"),
 ("C1","Construction foundation","ConstructionSite, project extensions, WbsNode, CostCode, ConstructionAudit, RowVersion, DB integrity","C0 decisions D-01,D-02,D-13","C2,C4,C5","WBS depth + re-parent policy agreed; additive SQL only","Not started"),
 ("C2","BOQ, Budget, Cost Control","BoqRevision + immutability, ProjectBudget versions, ProjectCommitment, CostAllocation, forecast/EAC","C1 (WBS + CostCode must exist first)","C3,C6","CR-01 closed; budget control policy decided (D-08)","Not started"),
 ("C3","Client and subcontract contracts","ClientContract, SubcontractScope, bonds, insurance, DLP, approval chain","C2 (BOQ revisions exist)","C6","CR-02 capped; retention/advance methods decided (D-04,D-05)","Not started"),
 ("C4","Site material operations","MaterialRequest, site store binding, issue/return with WBS+CostCode, waste","C1 (site + cost code)","C5,C6","No second inventory engine: StockService remains the only stock writer","Not started"),
 ("C5","Daily site reports, manpower, equipment","DSR, ManpowerLog, EquipmentLog, fuel, downtime, photos, incidents","C1, C4","C6,C9","HR and asset masters referenced, never duplicated","Not started"),
 ("C6","Progress certificates","Client + subcontractor certificates with the full review chain and revision snapshots","C2, C3 (BOQ + contract), C4/C5 for MOS and quantities","C7,C9","CR-04, CR-05, CR-08 resolved with the Accounting owner","Not started"),
 ("C7","Variations, claims, delays","Variation states + BOQ revision emission, Claim, DelayEvent, EOT, schedule variance","C6 (certificates must snapshot revisions first)","C9","CR-03 closed","Not started"),
 ("C8","RFIs, inspections, submittals, document control","Quality documents + DocumentRegister/Revision, punch list, handover, closeout","C1; C5 for site linkage","C9,C10","CR-13 closed before any site drawing enforcement","Not started"),
 ("C9","Reporting and dashboards integration","Read-only data sources + DTOs handed to the second tab; timeline payloads handed to the third","C2,C3,C6,C7,C8 (data must exist)","C10","Report DTOs defined before integration; no renderers built here","Not started"),
 ("C10","Mobile site workspace","Offline DSR, inspection, RFI, punch list, photo, receipt","C5,C8; CR-18 idempotency contract","C11","Privacy and permission review passed","Not started"),
 ("C11","Schedule baselines and planning interchange","Activity + baseline entities; Primavera P6 XER import; Microsoft Project import where technically feasible (MPP is a closed binary — via MSPDI/XML export, or XER only, decided after a spike); schedule variance against an approved baseline","C1 (WBS identity), C7 (delay/EOT need a baseline to measure against)","C12","A spike report states exactly which XER/MSPDI fields map to WbsNode/Activity and which are dropped, BEFORE any importer is written; no import may create or re-parent a WBS node that carries cost","Not started — deferred by the C1 brief"),
 ("C12","Resource leveling","Labour and equipment demand curves from the schedule; over-allocation detection; leveling proposals for planner approval","C11 (a baseline schedule), C5 (manpower and equipment actuals)","none","Leveling never writes an actual: it proposes a plan a human approves","Not started — deferred by the C1 brief"),
]
ROADMAP_FIELDS = ["Phase","Title","Contents","DependsOn","Blocks","Gate","Status"]

# ==============================================================================================
# 7. DECISION REGISTER — business decisions we must NOT guess.
# ==============================================================================================
DECISIONS = [
 ("D-01","Can one project hold multiple active client contracts?",
  "Today impossible: contract terms are columns on Project (Models/Context/Accounting/Dimensions.cs:36-44), so exactly one implicit contract exists.",
  "(a) one contract per project; (b) many contracts per project with scope split; (c) one contract spanning many projects (programme)",
  "(b) — a separate ClientContract entity with a project link, because packages and phased awards are normal in contracting and (a) forces fake projects.",
  "(b) means BOQ, certificates and retention all key on ContractId, not ProjectId — a wider change in C3.",
  "Blocks C3 and the certificate design"),
 ("D-02","Do the client BOQ and the internal cost estimate share one structure?",
  "Today one structure: BoqItem holds the client UnitPrice AND four estimated cost buckets on the same row (Boq.cs:20-25).",
  "(a) keep one row with both; (b) separate client BOQ from internal estimate, linked",
  "(b) — the client BOQ is contractual and immutable once approved, the internal estimate changes constantly; sharing a row is what makes rate changes dangerous.",
  "(b) requires a migration path for existing BoqItem cost buckets and a mapping UI.",
  "Blocks C2"),
 ("D-03","What are the certification approval limits?",
  "None exist: ApproveAsync only checks Draft and GrossWork>0 (BL/ProgressBillingService.cs:191-200).",
  "(a) single approver; (b) value-banded approvers; (c) role chain regardless of value",
  "(b) with a role chain floor — value bands are how contracting firms actually delegate.",
  "Needs a limits table and the approval engine of CR-14.",
  "Blocks C6"),
 ("D-04","How is retention calculated?",
  "Flat percent of period work: R = W x Project.RetentionPercent (BL/ProgressBillingService.cs:118).",
  "(a) flat percent per certificate; (b) percent capped at a contract ceiling; (c) tiered (higher until X% complete, then lower); (d) per-BOQ-item applicability",
  "(b)+(d) — a ceiling and an item-level applicability flag cover most Gulf contracts; (c) as an optional method.",
  "Changes the certificate computation and needs a retention term set on the contract.",
  "Blocks C3 and C6"),
 ("D-05","How is the advance recovered?",
  "Pro-rata per certificate, capped at the remaining 2104 balance (BL/ProgressBillingService.cs:120).",
  "(a) pro-rata (today); (b) start after X% progress; (c) fixed instalments; (d) back-loaded",
  "Keep (a) as default, add (b) as a contract term — (b) is common and today unrepresentable.",
  "Advance recovery rules move onto the contract entity.",
  "Blocks C3"),
 ("D-06","How are materials on site (MOS) valued in a certificate?",
  "Not supported: the certificate has no MOS concept (Models/Context/Accounting/ProgressBilling.cs).",
  "(a) not certified at all; (b) at invoice cost; (c) at BOQ material rate; (d) at a contractual percentage of value",
  "(d) with (b) as the valuation base — the contract usually caps MOS at a percentage.",
  "Introduces a non-work component in the certificate that must be recovered as the work is executed.",
  "Blocks C6"),
 ("D-07","What is the subcontractor over-certification policy?",
  "None — unbounded (CR-02).",
  "(a) hard block at allocated scope; (b) allow with an approved variation; (c) allow with a warning",
  "(a) as the default with (b) as the authorised exception. (c) is what effectively happens today and it is the defect.",
  "Requires SubcontractScope in C3 before any subcontractor certificate work in C6.",
  "Blocks C3 and C6"),
 ("D-08","Does budget overspend block or warn?",
  "Neither: there is no budget entity and no commitment check anywhere (BL/ProjectBudgetService.cs is a report).",
  "(a) warn only; (b) block at approval of PO/subcontract; (c) per-company policy switch",
  "(c) defaulting to warn — blocking procurement is a business risk the owner must choose, not us.",
  "A blocking mode reaches into Purchasing approval, which is another module's behaviour.",
  "Blocks C2"),
 ("D-09","What are the cost transfer rules between cost codes?",
  "No cost codes and no transfer exist.",
  "(a) no transfers; (b) transfers with approval and reason; (c) free transfer inside a category",
  "(b) — a transfer is a commercial decision and must leave a reason and an approver.",
  "Needs BudgetTransfer plus audit.",
  "Blocks C2"),
 ("D-10","May one site access another site's warehouse?",
  "Unconstrained: a warehouse has no site and no project (Models/Context/Inventory/Inventory.cs:144-159); any warehouse can be picked on a project material issue.",
  "(a) site-restricted; (b) restricted with an approved exception; (c) unrestricted",
  "(b) — cross-site borrowing is real but must be visible.",
  "Requires Warehouse.SiteId (an Inventory-owned change) or a construction-side site-warehouse map.",
  "Blocks C4"),
 ("D-11","How is physical progress calculated?",
  "Value-weighted only: sum(executed value)/sum(BOQ value) (BL/ProgressService.cs:185).",
  "(a) value-weighted (today); (b) quantity-weighted per WBS; (c) milestone; (d) weighted activity; (e) manual approved",
  "Support (b),(c),(d),(e) per WBS node with an explicit ProgressMethod; keep (a) as a reporting rollup only, never as the definition of physical progress.",
  "Requires ProgressMethod on WbsNode and a ProgressRecord entity.",
  "Blocks C6"),
 ("D-12","What is the forecast (ETC/EAC) methodology?",
  "None exists.",
  "(a) remaining budget; (b) earned-value CPI-based; (c) manual per cost code; (d) rate-of-spend trend",
  "(c) as the authoritative input with (a),(b),(d) shown as references — a forecast a commercial manager did not own is not used.",
  "Forecast must be stamped with its method and author to be auditable.",
  "Blocks C2 reporting"),
 ("D-13","What is the drawing approval workflow?",
  "No document control exists (CR-13).",
  "(a) issue-only register; (b) internal review then issue; (c) consultant approval cycle with status codes (A/B/C)",
  "(c) — consultant status codes are the norm and drive whether the site may build.",
  "Determines whether a superseded revision is blocked or warned (screen 26).",
  "Blocks C8"),
 ("D-14","What is the mobile offline policy?",
  "No mobile construction surface exists yet (crossbuy_mobile/lib/screens/ has no project screen).",
  "(a) online only; (b) offline draft, server authoritative; (c) full offline with conflict merge",
  "(b) — drafts offline, every posting online, idempotency key per document. (c) is where duplicated site data comes from.",
  "Sets the CR-18 contract before any mobile build.",
  "Blocks C10"),
 ("D-15","What are the project closeout rules?",
  "A Close permission exists with no process (BL/ProjectsAccessService.cs:41).",
  "(a) status change only; (b) checklist-gated close that locks cost and certificates; (c) close plus a separate DLP-active state",
  "(c) — a closed project with an active DLP is the real end state, and retention is still outstanding.",
  "Needs handover, DLP and a lock on new cost/certificates.",
  "Blocks C8"),
]
DECISION_FIELDS = ["ID","Decision","CurrentEvidence","Options","Recommendation","Consequences","BlockingScope"]


# ==============================================================================================
# formatting helpers
# ==============================================================================================
def cell(v):
    return str(v).replace("|", "\\|").replace("\n", " ")

def table(headers, rows):
    out = ["| " + " | ".join(headers) + " |",
           "|" + "|".join(["---"] * len(headers)) + "|"]
    for r in rows:
        out.append("| " + " | ".join(cell(c) for c in r) + " |")
    return "\n".join(out)

def write_csv(name, fields, rows):
    path = os.path.join(OUT, name)
    with open(path, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(fields)
        w.writerows(rows)
    return path

def write_md(name, text):
    path = os.path.join(OUT, name)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text if text.endswith("\n") else text + "\n")
    return path


# ==============================================================================================
# document builders
# ==============================================================================================
def doc_capability_matrix():
    counts = {s: sum(1 for c in CAPABILITIES if c[2] == s) for s in STATUS_ORDER}
    total = len(CAPABILITIES)
    summary = table(["Status", "Count", "Share"],
                    [[s, counts[s], f"{counts[s]*100//total}%"] for s in STATUS_ORDER if counts[s]])
    body = table(CAPABILITY_FIELDS, CAPABILITIES)
    return f"""# Stage-Construction-02 — Current-State Capability Matrix

{PROVENANCE}
**Product:** CrossBusiness Construction & Contracting · **Tab:** FOURTH · **Mode:** assessment only.

Every one of the {total} capabilities the brief enumerates is classified against the LIVE code, in the brief's own
order, using only the eight permitted statuses. `Evidence` is a real path; where a capability does not exist the
evidence says so and names where it was searched.

## Distribution

{summary}

**Reading of the distribution.** The contracting layer that exists is a *thin commercial spine* — BOQ, measurement,
client certificate, subcontract certificate, variation, retention and advance — built correctly on the existing
Accounting and Inventory writers. What is missing is everything that makes that spine *controllable*: WBS, cost
codes, a budget, commitments, immutability, concurrency, site operations, quality and document control.

Two statuses are the ones to read carefully:

- **{INADEQUATE}** — the capability exists and is used, but its current shape *permits a commercial error*
  (see CR-01, CR-02, CR-03 in the risk register). These are the rows that must be fixed before the module is
  extended, not after.
- **{DEFERRED}** — owned by another tab. Construction defines contracts and payloads only.

## The matrix

{body}

## What this matrix deliberately does not say

- It does not call the existing Projects module a construction system. It is an **accounting dimension**
  (`Models/Context/Accounting/Dimensions.cs:23`) with contracting columns added, and the whole cost chain is keyed on
  `ProjectId` alone.
- It does not propose replacing anything classified `{REUSABLE}`. Retention, advance, advance recovery, material
  issue and stock transfer are correct as designed and are reused unchanged.
- It does not assign work. That is `Stage-Construction-16-Implementation-Roadmap.md`.
"""


def doc_events():
    body = table(EVENT_FIELDS, EVENTS)
    return f"""# Stage-Construction-12 — Business Events (proposed)

{PROVENANCE}
**Nothing in this document is raised in this increment.** These are *proposals* that follow the platform kernel's
mandatory contract, so that adopting them later is a wiring change and not a redesign.

## Standing constraint

The hyper track's standing decision — do not raise platform business events until the kernel work is committed in
git — applies here too, and for the same reason: `IBusinessEventService.RecordAsync` must be called inside the
caller's ambient transaction immediately before commit **with no swallowing catch**, so raising an event makes the
kernel a hard runtime dependency of the raising path. A construction certificate that cannot post because the event
table changed is a worse outcome than a certificate with no timeline entry.

Therefore: **Construction defines events and payloads (this document). It does not raise them, and it does not write
consumers** — consumers listed below are *requests* to the third tab, not work items for this tab.

## Contract every proposed event already obeys

- Name is `<EntityCode>.<Action>`, action PascalCase, and the entity code must first be registered in
  `BL/Platform/EntityRegistry.cs` (today the only construction-adjacent code is `Project`, and it is registered with
  `SupportsTimeline = false, SupportsComments = false, SupportsFiles = false` at line 219 — i.e. **not onboarded**).
- Every event carries CompanyID and ProjectId, because no construction fact is meaningful without both.
- `SensitiveFields` names what must be withheld from a general timeline: contract values, rates and margins are
  commercial data, and a timeline is read by more people than a certificate is.
- A `DedupKey` is set on every event whose producing action can be retried (certificate submit/approve/post).
- The action must be a real transition — no `Updated` events.

## Proposed catalogue

{body}

## Onboarding prerequisites (owned elsewhere)

1. Register the construction entity codes in `EntityRegistry` (kernel change — coordinate).
2. Turn on `SupportsTimeline`/`SupportsComments`/`SupportsFiles` per entity, and set a real `PermissionScope`
   (today `Project` is `ScopeNone`).
3. Only then add `RecordAsync` calls at the transitions above, inside the existing transaction.
"""


def doc_risks():
    sev_rank = {"Critical": 0, "High": 1, "Medium": 2, "Low": 3}
    rows = sorted(RISKS, key=lambda r: (sev_rank[r[1]], r[0]))
    counts = {}
    for r in RISKS:
        counts[r[1]] = counts.get(r[1], 0) + 1
    summary = table(["Severity", "Count"],
                    [[s, counts[s]] for s in ["Critical", "High", "Medium", "Low"] if s in counts])
    body = table(RISK_FIELDS, rows)
    return f"""# Stage-Construction-15 — Risk Register

{PROVENANCE}
Every row is **evidence-backed**: a path, and a line where the line is what makes the point. No risk here was
invented to fill a category, and the count is whatever the evidence produced — {len(RISKS)} risks.

## Distribution

{summary}

Three are Critical, and all three are the same *kind* of defect: **a commercial value can change without leaving a
usable trace**. CR-01 changes it by deleting and re-inserting, CR-02 by never bounding it, CR-03 by overwriting it in
place. Everything else in this register is either a consequence of that pattern or an absence of control around it.

## Register

{body}

## Ownership note

CR-09 (project create/edit/delete ungated, company 1 hardcoded) and CR-20 (timeline onboarding) are **referred**,
not scheduled: authorization belongs to the first tab and timeline infrastructure to the third. This tab reports the
evidence and does not touch those files.

CR-04, CR-05 and CR-10 sit inside Accounting-owned code paths reached from a construction screen. They are real, they
are reported with evidence, and the fix is the Accounting owner's call — construction must not quietly re-post the
ledger to work around them.
"""


def doc_roadmap():
    body = table(ROADMAP_FIELDS, ROADMAP)
    return f"""# Stage-Construction-16 — Implementation Roadmap

{PROVENANCE}
Dependency-ordered. The `DependsOn`/`Blocks` columns are the contract; the order is not a preference.

## Why this order and not the obvious one

The obvious order is "build the visible documents first". That is what produced the current defects: certificates and
variations exist while WBS, cost codes, budget, immutability and concurrency do not — so a certificate can be raised
against a BOQ that can be deleted (CR-01) and a variation can silently rewrite a rate a certificate already used
(CR-03).

So the foundation phases (C1, C2) exist to make the documents that *already ship* safe, before anything new is added
on top of them.

## Hard dependency rules carried from the brief

- WBS before BOQ allocation · Cost codes before cost analysis · BOQ before progress certificates
- Contracts before certificates · Variations before revised BOQ values
- Document control before site drawing enforcement · Reporting DTOs before reporting integration
- Permissions before production screens · Evidence before migration · Migration before cleanup

## Phases

{body}

## Gate discipline

No phase starts while the previous phase's `Gate` column is unmet, and every phase ends with: additive idempotent SQL
in `deploy/sql` (never an EF migration), tests for its own invariants, and a re-read-from-a-new-context acceptance.
"""


def doc_screens():
    body = table(SCREEN_FIELDS, SCREENS)
    return f"""# Stage-Construction-17 — Screen Inventory (future screens)

{PROVENANCE}
**No visual layouts are designed here, and none may be invented later.** See the theme-first rule below — it is a
binding constraint on every screen in this list.

## Theme-first UI rule (binding)

For every screen in this inventory:

1. **Do not invent the design.** Request the approved Metronic/theme reference for that screen type first.
2. **Use the supplied theme screen or component as the design source** — the approved reference is the input, not an
   afterthought.
3. **Preserve CrossBusiness Platform visual identity** — the Metronic 8 `app-*` shell, the brand palette via Metronic
   tokens, RTL/LTR parity, and `@Localizer` with ar/en/fr resx for every user-facing string.
4. **Do not create a speculative replacement component where an approved theme component exists.**

Specialised operational surfaces (site store, mobile site capture) keep their domain UX, exactly as POS and the
manufacturing floor do — but they still take their components from the approved theme.

This rule is restated in `Stage-Construction-14-Mobile-and-Site-UX-Contract.md`, which is the construction UX
contract of record.

## Inventory ({len(SCREENS)} screens)

{body}

## Screens deliberately absent from this list

- A construction *reporting* screen: report presentation is the second tab's Report Studio. Construction supplies
  data sources and DTOs only (`Stage-Construction-13-Reporting-Requirements.md`).
- A construction *comments/timeline* panel: that is the third tab's infrastructure, requested per entity.
- A construction *permission admin* screen: authorization UI is the first tab's Security Console.
"""


def doc_decisions():
    body = table(DECISION_FIELDS, DECISIONS)
    return f"""# Stage-Construction-18 — Decision Register

{PROVENANCE}
{len(DECISIONS)} business decisions that **must not be guessed**. Each carries current evidence from the live code,
the real options, a recommendation with a reason, the consequence of taking it, and what it blocks.

A recommendation is offered on every one of them — an unanswered question should not stop design work — but the
recommendation is *not* an assumption in force: where a decision blocks a phase, the phase's gate requires the
owner's answer.

## Register

{body}

## How these were chosen

Each of these is a point where the code currently makes a silent choice that a contracting business would normally
decide explicitly. D-04 is the clearest example: retention is a flat percent of period work because that is the one
line of arithmetic that exists (`BL/ProgressBillingService.cs:118`) — not because a ceiling or a tiered method was
considered and rejected.
"""


# ==============================================================================================
def main():
    written = []
    written.append(write_csv("construction-capability-matrix.csv", CAPABILITY_FIELDS, CAPABILITIES))
    written.append(write_csv("construction-entity-catalog.csv", ENTITY_FIELDS, ENTITIES))
    written.append(write_csv("construction-screen-catalog.csv", SCREEN_FIELDS, SCREENS))
    written.append(write_csv("construction-event-catalog.csv", EVENT_FIELDS, EVENTS))
    written.append(write_csv("construction-risk-register.csv", RISK_FIELDS, RISKS))
    written.append(write_csv("construction-roadmap.csv", ROADMAP_FIELDS, ROADMAP))
    written.append(write_csv("construction-decision-register.csv", DECISION_FIELDS, DECISIONS))

    written.append(write_md("Stage-Construction-02-Current-Capability-Matrix.md", doc_capability_matrix()))
    written.append(write_md("Stage-Construction-12-Business-Events.md", doc_events()))
    written.append(write_md("Stage-Construction-15-Risk-Register.md", doc_risks()))
    written.append(write_md("Stage-Construction-16-Implementation-Roadmap.md", doc_roadmap()))
    written.append(write_md("Stage-Construction-17-Screen-Inventory.md", doc_screens()))
    written.append(write_md("Stage-Construction-18-Decision-Register.md", doc_decisions()))

    print(f"capabilities={len(CAPABILITIES)} entities={len(ENTITIES)} screens={len(SCREENS)} "
          f"events={len(EVENTS)} risks={len(RISKS)} roadmap={len(ROADMAP)} decisions={len(DECISIONS)}")
    for p in written:
        print("wrote", os.path.relpath(p, OUT))
    return 0


if __name__ == "__main__":
    sys.exit(main())