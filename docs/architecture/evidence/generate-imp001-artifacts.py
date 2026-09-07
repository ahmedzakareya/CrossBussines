"""IMP-001 closure artifacts, generated from one structured source.

Facts (readers, writers, routes, authorization attributes, scope columns) were derived from source inspection:
  PlatformRoleAssignments : 4 readers, ZERO production writers (writes only in 4 test files)
  AccountingUserRoles     : AccountingController.AssignAccRole / RemoveAccRole   [AccPerm("manage")]
  InventoryUserRoles      : InventoryController.AssignRole / RemoveRole          [InvPerm("manage")]  + ScopeBranchId
  CrmUserRoles            : CrmController.AssignCrmRole                          [CrmPerm("manage")]
  BranchUserRoles         : PosSetupService.AssignPosRoleAsync / RemovePosRoleAsync (sanctioned exception)
"""
import csv, os, collections

EV = os.path.join("docs", "architecture", "evidence")

# ---------------------------------------------------------------- 1. role source inventory
SRC_F = ["source_id","source_type","table_or_policy","role_or_access_category","reader","writer",
         "company_scope","branch_scope","principal_type","active_behavior","validity_behavior","audit",
         "bootstrap_interaction","migration_classification","authoritative_state_eligibility","risks"]

SRC = [
("SRC-001","Platform authorization grant","PlatformRoleAssignments","Scope + Role code",
 "PlatformRoleDirectory; HrAccessService; ProjectsAccessService","NONE IN PRODUCTION (tests only)",
 "CompanyID direct","ScopeBranchId (nullable)","PrincipalType (Employee today)","IsActive",
 "ValidFrom / ValidTo honoured","CreatedAt / CreatedBy present; no RevokedBy yet",
 "AnyConfiguredAsync per company+scope drives bootstrap-open","TARGET",
 "Eligible once a Grant Writer exists","RISK-037 no writer so HR and Projects are bootstrap-open in production"),
("SRC-002","Module role assignment","AccountingUserRoles","ChiefAccountant Accountant Cashier Auditor",
 "AccountingAccessService","AccountingController.AssignAccRole / RemoveAccRole",
 "CompanyID direct","none","Employee implicit","hard delete on remove","none","CreatedAt only",
 "empty table for a company opens every accounting action","MIGRATE",
 "Legacy authoritative until cutover","RISK-038 writer must stop at cutover"),
("SRC-003","Module role assignment","InventoryUserRoles","InventoryManager WarehouseKeeper others",
 "InventoryAccessService","InventoryController.AssignRole / RemoveRole",
 "CompanyID direct","ScopeBranchId used for WarehouseKeeper","Employee implicit","hard delete on remove","none",
 "CreatedAt only","empty table opens inventory actions","MIGRATE",
 "Legacy authoritative until cutover","Branch scope must map exactly; flattening would widen access"),
("SRC-004","Module role assignment","CrmUserRoles","SalesManager SalesRep Marketing CrmViewer",
 "CrmAccessService","CrmController.AssignCrmRole","CompanyID direct","none","Employee implicit",
 "row presence","none","CreatedAt only","empty table opens CRM actions","MIGRATE",
 "Legacy authoritative until cutover","Owner and team breadth must stay computed in the access service"),
("SRC-005","Branch-scoped POS role","BranchUserRoles","pos-cashier pos-manager pos-waiter pos-kitchen",
 "PosAccessService; PosSetupService","PosSetupService.AssignPosRoleAsync / RemovePosRoleAsync",
 "NONE - branch-owned, no CompanyID","BranchId is the owner","Employee via AspNet user","IsActive flag","none",
 "CreatedAt only","POS is never bootstrap-open","DO NOT MIGRATE (sanctioned exception)",
 "Remains authoritative for POS","Lane login resolves rights before a BusinessContext exists"),
("SRC-006","Business membership","ProjectMembers","project membership role",
 "ProjectsAccessService","NONE IN PRODUCTION (tests only)","CompanyID present but project row is authority",
 "none","Employee","row presence","none","CreatedAt","not a bootstrap source","NEVER MIGRATE",
 "Not an authorization store","Membership has its own lifecycle; must not become a role row"),
("SRC-007","Business membership","ConversationMembers","conversation membership and Owner",
 "CommunicationAccessService; ChatService; ChatHub","ChatService / ChatController",
 "via parent Conversation","none","Employee","row presence","none","JoinedAt","not a bootstrap source",
 "NEVER MIGRATE","Not an authorization store","Batch C.1 proved membership is the control protecting a conversation"),
("SRC-008","Identity role","AspNetRoles / AspNetUserRoles","Admin Administrator SuperAdmin PlatformOps",
 "PlatformOpsAttribute via IsInRole (ONE call site)","ASP.NET Identity APIs","none - global","none","AspNet user",
 "Identity managed","none","Identity managed","PlatformOps composes with accounting manage",
 "DO NOT MERGE","Separate console view","Platform-operator identity, not a business grant"),
("SRC-009","Hierarchy-derived access","Hierarchical via IOrgHierarchy","manager reach over reports",
 "IOrgHierarchy; Hr / Crm / Tasks / Projects access services","People and Structure screens",
 "intersected with company by IOrgHierarchy","none","Employee","tree shape","none","none",
 "not a bootstrap source","NOT STORAGE","Derived at query time","RISK-015 Hierarchical has no CompanyID"),
("SRC-010","Ownership / record-level","AssigneeEmployeeId CreatedByEmployeeId CRM owner","record ownership",
 "Tasks / Crm / Projects access services","the owning document writers","row CompanyID","row BranchId where present",
 "Employee","row data","none","row audit columns","not a bootstrap source","NOT STORAGE",
 "Record data, not a grant","none"),
("SRC-011","Bootstrap-open policy","AnyConfiguredAsync per company+scope","implicit allow when unconfigured",
 "ModuleAccessServiceBase and the module access services","none - it is derived",
 "per company","none","n/a","derived","none","none","IS the bootstrap mechanism","POLICY NOT STORAGE",
 "IMP-002 subject","RISK-005 RISK-006 unconfigured company is permissive"),
("SRC-012","System context policy","SystemContextPolicy / BusinessContext.IsSystem","system actions",
 "access services","none","explicit company on the worker scope","none","System","code policy","none","none",
 "bypasses role checks by design","POLICY NOT STORAGE","Code policy",
 "Must never be creatable as an employee grant"),
("SRC-013","Legacy ad hoc","SessionValidation and PosLaneActivityGuard attributes","not authorization",
 "MVC filter pipeline","n/a","none","lane only","n/a","n/a","none","none",
 "counts as neither grant nor bootstrap","NOT AUTHORIZATION","Never authoritative",
 "IMP-009 rename so it cannot be mistaken for a guard; sits on 257 mutating actions"),
]

# ---------------------------------------------------------------- 2. migration matrix
MIG_F = ["legacy_source","legacy_role","target_scope","target_role_code","branch_behavior","company_behavior",
         "action_mapping","migration_eligibility","conflict_risk","required_validation","rollback_mapping","status"]

def acc(role, actions):
    return ("AccountingUserRoles", role, "Accounting", role, "n/a - no branch scope",
            "CompanyID copied verbatim", actions, "Eligible", "Low - one row per employee+role",
            "role code must exist in AccountingAccessService role set",
            "legacy row retained; flip cutover flag to Legacy", "Designed - not executed")

def inv(role, actions, branch):
    return ("InventoryUserRoles", role, "Inventory", role, branch,
            "CompanyID copied verbatim", actions, "Eligible", "Medium - ScopeBranchId must map exactly",
            "ScopeBranchId preserved; company-wide must not be substituted",
            "legacy row retained; flip cutover flag to Legacy", "Designed - not executed")

def crm(role, actions):
    return ("CrmUserRoles", role, "CRM", role, "n/a",
            "CompanyID copied verbatim", actions, "Eligible",
            "Medium - owner and team breadth must stay computed",
            "VisibleOwnerIdsAsync behaviour unchanged after migration",
            "legacy row retained; flip cutover flag to Legacy", "Designed - not executed")

MIG = [
 acc("ChiefAccountant","read post pay manage currency-override"),
 acc("Accountant","read post"),
 acc("Cashier","read pay"),
 acc("Auditor","read"),
 inv("InventoryManager","read doc purchase manage","company-wide - ScopeBranchId NULL"),
 inv("WarehouseKeeper","read doc","ScopeBranchId REQUIRED - copied one-to-one"),
 crm("SalesManager","read edit manage over own plus team"),
 crm("SalesRep","read edit over own records"),
 crm("Marketing","read across company"),
 crm("CrmViewer","read across company"),
 ("PlatformRoleAssignments","HrManager HrOfficer PayrollOfficer HrViewer","HR","unchanged","n/a",
  "already company-scoped","HrActions unchanged","ALREADY NATIVE - no migration",
  "None for storage; CRITICAL that a writer exists",
  "Grant Writer must exist before Wave 2 gates HR endpoints","n/a - no migration",
  "Blocked on Grant Writer (RISK-037)"),
 ("PlatformRoleAssignments","ProjectsAdministrator ProjectsFinance ProjectsViewer","Projects","unchanged","n/a",
  "already company-scoped","ProjectsActions unchanged","ALREADY NATIVE - no migration",
  "None for storage","Grant Writer required; ProjectMembers stays separate","n/a",
  "Blocked on Grant Writer (RISK-037)"),
 ("PlatformRoleAssignments","TasksAdministrator TasksSupervisor TasksViewer","Tasks","unchanged","n/a",
  "already company-scoped","TasksActions; Own/Team/Company breadth stays computed","ALREADY NATIVE",
  "None","AccessScope breadth must remain computed not stored","n/a","Native"),
 ("PlatformRoleAssignments","CommunicationAdministrator AnnouncementPublisher OutboxOperator","Communication",
  "unchanged","ScopeBranchId used for announcement audience","already company-scoped",
  "CommunicationActions unchanged","ALREADY NATIVE","None",
  "ConversationMembers must stay separate","n/a","Native"),
 ("BranchUserRoles","pos-cashier pos-manager pos-waiter pos-kitchen","POS","NOT MIGRATED",
  "BranchId is the owner","no CompanyID on the table","POS predicates unchanged",
  "EXCLUDED - sanctioned exception","High if forced - circular dependency at lane login",
  "POS login must keep working unchanged","n/a - never migrated","Excluded by design"),
 ("ProjectMembers","project membership","n/a","NOT A ROLE","n/a","project row is the authority",
  "membership rules stay in ProjectsAccessService","EXCLUDED - business membership","High if migrated",
  "must remain a separate relationship","n/a","Excluded by design"),
 ("ConversationMembers","conversation membership and Owner","n/a","NOT A ROLE","n/a","via parent conversation",
  "membership rules stay in CommunicationAccessService","EXCLUDED - business membership","High if migrated",
  "must remain a separate relationship","n/a","Excluded by design"),
 ("AspNetRoles / AspNetUserRoles","Admin Administrator SuperAdmin PlatformOps","n/a","NOT MERGED","n/a","global",
  "PlatformOpsAttribute unchanged","EXCLUDED - identity role","Medium if merged",
  "separate console view","n/a","Excluded by design"),
]

# ---------------------------------------------------------------- 3. legacy writer shutdown matrix
SHUT_F = ["writer_id","file","action","route","current_authorization","company_source","source_table",
          "future_target_writer","legacy_mode","shadow_mode","platform_mode","disabled_state",
          "user_facing_message","rollback_behavior","tests"]

SHUT = [
("W-001","Controllers/AccountingController.cs","AssignAccRole","POST /Accounting/AssignAccRole",
 'AccPerm("manage") + SessionValidation + anti-forgery','DefaultCompanyId constant (RISK-003)',
 "AccountingUserRoles","IPlatformGrantWriter","writes legacy as today",
 "writes legacy AND mirrors to platform via the writer","REDIRECT to Grant Writer - must not write legacy",
 "action returns a blocked result explaining migration",
 "Accounting roles are now managed in Platform Security",
 "flip flag to Legacy; legacy writer re-enabled","assign then read-back through AccountingAccessService in all three modes"),
("W-002","Controllers/AccountingController.cs","RemoveAccRole","POST /Accounting/RemoveAccRole",
 'AccPerm("manage")','DefaultCompanyId constant',"AccountingUserRoles","IPlatformGrantWriter revoke",
 "hard-deletes legacy row as today","deletes legacy AND revokes the platform assignment",
 "REDIRECT to revoke - preserves history instead of hard delete",
 "blocked result","Accounting roles are now managed in Platform Security",
 "flip flag to Legacy","revoke denies immediately; revoked row retained"),
("W-003","Controllers/InventoryController.cs","AssignRole","POST /Inventory/AssignRole",
 'InvPerm("manage") + anti-forgery','DefaultCompanyId constant (267 refs in this controller)',
 "InventoryUserRoles","IPlatformGrantWriter with ScopeBranchId","writes legacy as today",
 "writes legacy AND mirrors including ScopeBranchId",
 "REDIRECT - ScopeBranchId must be carried through exactly","blocked result",
 "Inventory roles are now managed in Platform Security","flip flag to Legacy",
 "WarehouseKeeper scoped to branch A is denied at branch B in every mode"),
("W-004","Controllers/InventoryController.cs","RemoveRole","POST /Inventory/RemoveRole",
 'InvPerm("manage")','DefaultCompanyId constant',"InventoryUserRoles","IPlatformGrantWriter revoke",
 "hard-deletes as today","deletes legacy AND revokes platform","REDIRECT to revoke","blocked result",
 "Inventory roles are now managed in Platform Security","flip flag to Legacy","revoked scoped grant denies"),
("W-005","Controllers/CrmController.cs","AssignCrmRole","POST /Crm/AssignCrmRole",
 'CrmPerm("manage") + anti-forgery','DefaultCompanyId constant (97 refs)',"CrmUserRoles","IPlatformGrantWriter",
 "writes legacy as today","writes legacy AND mirrors","REDIRECT","blocked result",
 "CRM roles are now managed in Platform Security","flip flag to Legacy",
 "VisibleOwnerIdsAsync unchanged in all three modes"),
("W-006","Controllers/Api/DevSeedController.cs","seed helpers writing InventoryUserRoles and CrmUserRoles",
 "POST /api/devseed/* (DevOnly)","DevOnly dev endpoints","explicit company argument","InventoryUserRoles / CrmUserRoles",
 "test fixtures should seed the platform table","allowed - dev only",
 "allowed but should mirror so dev data matches production shape",
 "must switch to the platform table or dev data diverges from production authority",
 "n/a - DevOnly","n/a","n/a","dev endpoints must not be the reason a migration test passes"),
("W-007","BL/PosSetupService.cs","AssignPosRoleAsync / RemovePosRoleAsync","via POS setup screens",
 "POS setup screens (PosPerm / lane admin)","BranchId owns the row - no CompanyID","BranchUserRoles",
 "NONE - remains the POS writer","unchanged","unchanged","unchanged - POS is excluded",
 "n/a - never disabled","n/a","n/a","POS login and role predicates unchanged after every cutover"),
]

def emit(name, fields, rows):
    p = os.path.join(EV, name)
    with open(p, "w", encoding="utf-8", newline="") as fh:
        w = csv.writer(fh, quoting=csv.QUOTE_ALL)
        w.writerow(fields)
        for r in rows:
            assert len(r) == len(fields), f"{name}: {r[0]} has {len(r)} fields, expected {len(fields)}"
            w.writerow(r)
    got = list(csv.DictReader(open(p, encoding="utf-8")))
    # Uniqueness is checked on the row's IDENTIFYING key, which is not always column 1: the migration matrix is
    # keyed by (legacy_source, legacy_role) because one source contributes many roles. Asserting on column 1
    # alone reported a false duplicate.
    key = (lambda x: (x[fields[0]], x[fields[1]])) if fields[0] == "legacy_source" else (lambda x: x[fields[0]])
    keys = [key(x) for x in got]
    dupes = [k for k, n in collections.Counter(keys).items() if n > 1]
    print(f"{name}: {len(got)} rows | unique key: {not dupes} | "
          f"empty fields: {sum(1 for x in got for k in fields if not x[k])}")
    assert not dupes, f"{name}: duplicate keys {dupes}"
    return got

src = emit("Stage-002-Role-Source-Inventory.csv", SRC_F, SRC)
mig = emit("Stage-002-Role-Migration-Matrix.csv", MIG_F, MIG)
shut = emit("Stage-002-Legacy-Role-Writer-Shutdown-Matrix.csv", SHUT_F, SHUT)

print()
print("migration classification:", dict(collections.Counter(r["migration_eligibility"].split(" -")[0] for r in mig)))
print("source classification   :", dict(collections.Counter(r["migration_classification"].split(" (")[0] for r in src)))
