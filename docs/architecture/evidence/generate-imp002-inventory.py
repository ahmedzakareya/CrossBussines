"""IMP-002 bootstrap-open inventory, generated from one structured source.

Facts traced through the PRODUCTION decision path (not method names):

TWO STRUCTURALLY DIFFERENT MECHANISMS EXIST.

  A) Legacy early-return  (Accounting, Inventory, CRM)
     `if (!await AnyRoleConfiguredAsync(companyId)) return true;`  placed BEFORE any action check.
     The module has NO per-action discretion: every action is open, including financial posting.
       AccountingAccessService.cs:60   InventoryAccessService.cs:48 and :91   CrmAccessService.cs:59

  B) ModuleAccessServiceBase  (HR, Projects, Tasks, Communication)
     Base computes `bootstrapOpen = grants.Count == 0 && !AnyConfiguredAsync(company, scope)`
     (ModuleAccessServiceBase.cs:106-107), logs it at Information (:112), then passes it to the module's
     EvaluateAsync so the module CAN exclude specific actions. Three modules do.

  POS uses NEITHER: PosAccessService has zero bootstrap references. No POS role -> no access.
"""
import csv, os, collections

EV = os.path.join("docs", "architecture", "evidence")

F = ["inventory_id","module","scope","action","access_service","mechanism","current_bootstrap_condition",
     "company_behavior","branch_behavior","confidential","financial_impact","inventory_impact",
     "privilege_impact","operational_impact","current_logging","current_audit","admin_visibility",
     "proposed_classification","migration_impact","risk"]

NONE = "none"
PERCO = "per company - unconfigured company is open"
LOGGED = "Information log per decision (base class)"
NOLOG = "NOT logged - legacy early return has no log line"
NOVIS = "none - no screen shows bootstrap state"

def row(i, mod, scope, action, svc, mech, cond, comp, branch, conf, fin, inv, priv, op,
        log, audit, vis, cls, mig, risk):
    return (i, mod, scope, action, svc, mech, cond, comp, branch, conf, fin, inv, priv, op,
            log, audit, vis, cls, mig, risk)

R = []
# ---------------- A) legacy early-return: NO per-action discretion ----------------
for i, (act, fin, cls, risk) in enumerate([
    ("read",   "reads ledger and statements",       "Explicitly Allowed Bootstrap Open",
     "read-only exposure of financial data on an unconfigured company"),
    ("post",   "POSTS JOURNAL ENTRIES to the GL",   "NEVER BOOTSTRAP OPEN",
     "RISK-040 financial posting is bootstrap-open today with no exclusion"),
    ("pay",    "RECORDS PAYMENTS and receipts",     "NEVER BOOTSTRAP OPEN",
     "RISK-040 payments are bootstrap-open today"),
    ("manage", "period close year-end posting rules","NEVER BOOTSTRAP OPEN",
     "RISK-040 period close and role assignment are bootstrap-open today"),
    ("currency-override", "OVERRIDES FX RATE on a document", "NEVER BOOTSTRAP OPEN",
     "RISK-040 currency override is bootstrap-open today"),
], 1):
    R.append(row(f"BO-A{i:02d}", "Accounting", "Accounting", act, "AccountingAccessService",
        "A: legacy early return (AccountingAccessService.cs:60)",
        "AnyRoleConfiguredAsync false -> return true BEFORE the action switch", PERCO, NONE,
        "no confidential tier in this module", fin, NONE, "manage assigns accounting roles", "accounting screens and APIs",
        NOLOG, "none", NOVIS, cls, "seed an explicit policy matching today then close the four", risk))

for i, (act, inv_i, cls, risk) in enumerate([
    ("read",     "reads stock balances and costs", "Explicitly Allowed Bootstrap Open",
     "read exposure of cost and stock data"),
    ("doc",      "CREATES STOCK DOCUMENTS - receipts issues transfers", "NEVER BOOTSTRAP OPEN",
     "RISK-040 stock movements are bootstrap-open today"),
    ("purchase", "creates purchase documents",     "NEVER BOOTSTRAP OPEN",
     "RISK-040 purchasing is bootstrap-open today"),
    ("manage",   "warehouse setup costing method item master", "NEVER BOOTSTRAP OPEN",
     "RISK-040 inventory administration is bootstrap-open today"),
], 1):
    R.append(row(f"BO-B{i:02d}", "Inventory", "Inventory", act, "InventoryAccessService",
        "A: legacy early return (InventoryAccessService.cs:48)",
        "AnyRoleConfiguredAsync false -> return true BEFORE the action switch", PERCO,
        "ScopeBranchId IGNORED while open - branch scoping does not apply because the switch is never reached",
        "no confidential tier", "stock valuation and COGS follow stock documents", inv_i,
        "manage assigns inventory roles", "inventory and warehouse screens",
        NOLOG, "none", NOVIS, cls, "seed explicit policy then close doc purchase manage", risk))

R.append(row("BO-B05", "Inventory", "Inventory", "warehouse-access (CanUseWarehouseAsync)", "InventoryAccessService",
    "A: legacy early return (InventoryAccessService.cs:91)",
    "AnyRoleConfiguredAsync false -> return true, so EVERY warehouse is usable", PERCO,
    "WarehouseKeeper branch restriction NOT applied while open", "no confidential tier",
    "stock value moves in any warehouse", "any warehouse reachable", NONE, "warehouse pickers",
    NOLOG, "none", NOVIS, "NEVER BOOTSTRAP OPEN",
    "close together with doc; warehouse scope must be enforced from first configuration",
    "RISK-041 warehouse scope is unenforced while bootstrap-open"))

for i, (act, cls, risk) in enumerate([
    ("read",   "Explicitly Allowed Bootstrap Open", "owner scoping not applied while open"),
    ("edit",   "Legacy Behavior Requiring Migration", "any employee may edit any customer record"),
    ("manage", "NEVER BOOTSTRAP OPEN", "RISK-040 CRM administration and role assignment are open"),
], 1):
    R.append(row(f"BO-C{i:02d}", "CRM", "CRM", act, "CrmAccessService",
        "A: legacy early return (CrmAccessService.cs:59)",
        "AnyRoleConfiguredAsync false -> return true BEFORE role and owner checks", PERCO, NONE,
        "no confidential tier", "credit limits and pricing exposure", NONE,
        "manage assigns CRM roles", "CRM screens",
        NOLOG, "none", NOVIS, cls,
        "seed explicit policy; VisibleOwnerIdsAsync returns null (unrestricted) while open - owner scope must be restored on configuration",
        risk))

# ---------------- B) ModuleAccessServiceBase: module CAN and DOES exclude ----------------
HR = [
 ("read","Explicitly Allowed Bootstrap Open","HR screens visible"),
 ("employee-view","Explicitly Allowed Bootstrap Open","employee records readable"),
 ("employee-manage","Legacy Behavior Requiring Migration","employee records editable"),
 ("attendance-manage","Legacy Behavior Requiring Migration","attendance editable"),
 ("leave-manage","NEVER BOOTSTRAP OPEN","RISK-006 encashment provision and carry-over are open - money and balances"),
 ("leave-approve","Legacy Behavior Requiring Migration","approval out of turn is separately guarded by the chain"),
 ("payroll-view","Legacy Behavior Requiring Migration","payslip exposure"),
 ("organization-manage","Legacy Behavior Requiring Migration","org tree editable"),
 ("performance-manage","Legacy Behavior Requiring Migration","appraisals editable"),
]
for i,(act,cls,risk) in enumerate(HR,1):
    R.append(row(f"BO-D{i:02d}","HR","HR",act,"HrAccessService",
        "B: base class + module discretion",
        "grants.Count==0 AND AnyConfiguredAsync false -> module decides", PERCO, NONE,
        "confidential tier is separate and closed",
        "leave-manage disburses cash and posts provisions", NONE, NONE, "HR screens",
        LOGGED, "none beyond the log", NOVIS, cls,
        "RISK-037: PlatformRoleAssignments has NO production writer so HR cannot leave bootstrap-open at all today",
        risk))
for i,(act,) in enumerate([("payroll-manage",),("confidential-view",)],1):
    R.append(row(f"BO-D9{i}","HR","HR",act,"HrAccessService",
        "B: base class + EXPLICIT EXCLUSION (HrAccessService.cs:115)",
        "explicitly REFUSED under bootstrap-open - already correct", PERCO, NONE,
        "confidential-view IS the confidential tier",
        "payroll-manage is the payroll basis", NONE, NONE, "HR screens",
        LOGGED + " plus a denial log", "none", NOVIS, "Not Bootstrap Open",
        "no change needed - this is the pattern the other modules should follow",
        "none - correct today and the model for Accounting and Inventory"))

for i,(act,cls,risk) in enumerate([
 ("read","Explicitly Allowed Bootstrap Open","project data readable"),
 ("create","Legacy Behavior Requiring Migration","projects creatable"),
 ("edit","Legacy Behavior Requiring Migration","projects editable"),
 ("budget-view","Explicitly Allowed Bootstrap Open","budget readable"),
 ("budget-manage","NEVER BOOTSTRAP OPEN","budget-manage is the only accounting check for GL-reaching project actions"),
 ("close","Legacy Behavior Requiring Migration","project closure"),
 ("manage","NEVER BOOTSTRAP OPEN","company-wide project administration"),
],1):
    R.append(row(f"BO-E{i:02d}","Projects","Projects",act,"ProjectsAccessService",
        "B: base class + module discretion (ProjectsAccessService.cs:116,126,133)",
        "grants.Count==0 AND AnyConfiguredAsync false -> module decides", PERCO, NONE,
        "no confidential tier","budget-manage gates PostLabor PostMaterialIssue PostEquipmentDepreciation",
        "material issue moves stock", NONE, "project screens",
        LOGGED,"none",NOVIS,cls,
        "RISK-037 applies - no production writer, so Projects is bootstrap-open today", risk))
R.append(row("BO-E08","Projects","Projects","billing","ProjectsAccessService",
    "B: base class, but DELEGATES to AccountingAccessService",
    "requires accounting post as well - so it inherits Accounting bootstrap-open", PERCO, NONE,
    "no confidential tier","progress billing posts to the GL", NONE, NONE,"project billing screens",
    LOGGED,"none",NOVIS,"NEVER BOOTSTRAP OPEN",
    "closing Accounting post also closes project billing - a single fix covers both",
    "RISK-040 open today only because Accounting post is open"))

for i,(act,cls) in enumerate([
 ("read","Explicitly Allowed Bootstrap Open"),("create","Explicitly Allowed Bootstrap Open"),
 ("edit","Legacy Behavior Requiring Migration"),("assign","Legacy Behavior Requiring Migration"),
 ("reassign","Legacy Behavior Requiring Migration"),("complete","Legacy Behavior Requiring Migration"),
 ("reopen","Legacy Behavior Requiring Migration"),
],1):
    R.append(row(f"BO-F{i:02d}","Tasks","Tasks",act,"TasksAccessService",
        "B: base class + module discretion (TasksAccessService.cs:107,125,177)",
        "bootstrap-open still REQUIRES a record relationship for a named task (:177)", PERCO, NONE,
        "no confidential tier","GenerateInvoice is separately gated by accounting post",
        "PostLaborToWO posts work-order cost", NONE,"task screens",
        LOGGED,"none",NOVIS,cls,
        "narrowest bootstrap-open in the platform: module-level open, record-level still relational",
        "low - relationship requirement bounds the exposure"))
R.append(row("BO-F08","Tasks","Tasks","manage","TasksAccessService",
    "B: base class + EXPLICIT EXCLUSION (TasksAccessService.cs:125)",
    "bootstrapOpen ? action != Manage - manage is refused", PERCO, NONE,
    "no confidential tier",NONE,NONE,"company-wide task administration","task screens",
    LOGGED,"none",NOVIS,"Not Bootstrap Open","already correct","none"))

for i,(act,cls,risk) in enumerate([
 ("read","Explicitly Allowed Bootstrap Open","module-level only - conversation read still requires membership"),
 ("send","Explicitly Allowed Bootstrap Open","module-level only - membership still required per conversation"),
 ("create-group","Explicitly Allowed Bootstrap Open","group creation"),
 ("announcement-send","Legacy Behavior Requiring Migration","company-wide announcement while unconfigured"),
],1):
    R.append(row(f"BO-G{i:02d}","Communication","Communication",act,"CommunicationAccessService",
        "B: base class + module discretion (CommunicationAccessService.cs:102)",
        "grants.Count==0 AND AnyConfiguredAsync false -> module decides", PERCO,
        "announcement audience honours ScopeBranchId when grants exist","no confidential tier",
        NONE,NONE,NONE,"chat and announcement screens",
        LOGGED,"none",NOVIS,cls,
        "membership checks are INDEPENDENT of bootstrap-open - Read=>isMember holds even when open", risk))
R.append(row("BO-G05","Communication","Communication","manage-group","CommunicationAccessService",
    "B: base class, membership-based","requires Owner or commAdmin - membership not bypassed", PERCO, NONE,
    "no confidential tier",NONE,NONE,NONE,"chat screens",LOGGED,"none",NOVIS,
    "Not Bootstrap Open","membership rule is independent","none"))
R.append(row("BO-G06","Communication","Communication","outbox-manage","CommunicationAccessService",
    "B: base class + EXPLICIT EXCLUSION",
    "explicitly refused under bootstrap-open - email bodies are never open by default", PERCO, NONE,
    "email bodies are restricted payload",NONE,NONE,NONE,"outbox screens",
    LOGGED + " plus a denial log","none",NOVIS,"Not Bootstrap Open",
    "already correct - the second model to follow","none"))

# ---------------- POS and the non-bootstrap paths ----------------
R.append(row("BO-H01","POS","POS","view sell order kitchen manage","PosAccessService",
    "NEITHER - no bootstrap path exists",
    "ResolveByUserIdAsync returns null when no BranchUserRole exists -> NO ACCESS", "no company on the table",
    "BranchId owns the row and target branch must match","no confidential tier",
    NONE,NONE,NONE,"POS lanes",
    "none","none",NOVIS,"Not Bootstrap Open",
    "POS is already closed-by-default and is the strongest module in this respect",
    "none - POS lane guards are NOT bootstrap-open and must not be classified as such"))
R.append(row("BO-H02","Platform","Platform","platform operations","PlatformOpsAttribute",
    "NEITHER - identity role or accounting manage",
    "requires an Identity admin role OR accounting manage", "global", NONE,
    "exposes cross-module audit payloads", NONE, NONE, "platform operations", "event monitor",
    "none","none",NOVIS,"NEVER BOOTSTRAP OPEN",
    "INHERITS Accounting bootstrap-open through the accounting-manage fallback - documented in the attribute itself",
    "RISK-042 PlatformOps is reachable via accounting manage which is bootstrap-open today"))
R.append(row("BO-H03","Platform","Platform","system-context actions","SystemContextPolicy",
    "NEITHER - code policy",
    "BusinessContext.IsSystem plus SystemContextPolicy.Allows", "explicit worker company", NONE,
    "no confidential tier", "system actions can post", NONE, NONE, "background workers",
    "worker logs","none",NOVIS,
    "Not Bootstrap Open","code policy - must never be settable as a grant or a bootstrap policy",
    "none - already outside the bootstrap mechanism"))

p = os.path.join(EV, "Stage-002-Bootstrap-Open-Inventory.csv")
with open(p, "w", encoding="utf-8", newline="") as fh:
    w = csv.writer(fh, quoting=csv.QUOTE_ALL)
    w.writerow(F)
    for r in R:
        assert len(r) == len(F), f"{r[0]}: {len(r)} fields, expected {len(F)}"
        w.writerow(r)

got = list(csv.DictReader(open(p, encoding="utf-8")))
ids = [x["inventory_id"] for x in got]
print(f"rows: {len(got)} | unique ids: {len(set(ids))==len(ids)} | "
      f"empty fields: {sum(1 for x in got for k in F if not x[k])}")
print()
print("classification:", dict(collections.Counter(x["proposed_classification"] for x in got)))
print("mechanism     :", dict(collections.Counter(x["mechanism"][:1] for x in got)))
print()
print("NEVER BOOTSTRAP OPEN but open TODAY:")
for x in got:
    if x["proposed_classification"] == "NEVER BOOTSTRAP OPEN" and "REFUSED" not in x["current_bootstrap_condition"]:
        print(f"   {x['inventory_id']}  {x['module']}.{x['action']}")
