#!/usr/bin/env python3
"""CrossBusiness Platform - Roadmap v2 generator.

Emits every CSV and every data-driven Markdown table from the canonical dataset in
roadmap_v2_data.py / roadmap_v2_data2.py.

Deterministic: no timestamps, no randomness, stable sort on every collection, fixed
newline and encoding. Running it twice must produce byte-identical output.
"""
import csv, io, os, sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.abspath(os.path.join(HERE, os.pardir))
sys.path.insert(0, HERE)

from roadmap_v2_data import (BASELINE, BASELINE_DATE, STATUS_VOCAB, CAP_FIELDS, CAPABILITIES)
from roadmap_v2_data2 import (SCREEN_FIELDS, SCREENS, PHASE_FIELDS, PHASES, DEP_FIELDS,
                              DEPENDENCIES, DEC_FIELDS, DECISIONS, RISK_FIELDS, RISKS,
                              TAB_FIELDS, TABS, SHARED_FIELDS, SHARED_FILES, ADR_FIELDS, ADRS,
                              SLICE_FIELDS, SQL_SLICES, MANIFEST_FACTS)

import re

# --------------------------------------------------------------- integrity validation
# Runs before any file is written. A referential defect fails the build rather than
# shipping a document that points at a decision or phase which does not exist.
def validate():
    errs = []

    def dupes(rows, key, label):
        seen = {}
        for r in rows:
            seen[r[key]] = seen.get(r[key], 0) + 1
        for k, n in sorted(seen.items()):
            if n > 1:
                errs.append(f"duplicate {label} id: {k} x{n}")

    for rows, key, label in [(CAPABILITIES, "CapabilityId", "capability"),
                             (SCREENS, "ScreenId", "screen"), (PHASES, "PhaseId", "phase"),
                             (DEPENDENCIES, "DependencyId", "dependency"),
                             (DECISIONS, "DecisionId", "decision"), (RISKS, "RiskId", "risk"),
                             (TABS, "TabId", "tab"), (SHARED_FILES, "FileId", "shared file"),
                             (ADRS, "AdrId", "adr"), (SQL_SLICES, "SliceId", "slice")]:
        dupes(rows, key, label)

    # field arity — every row must carry its full field set
    for rows, fields, key, label in [(CAPABILITIES, CAP_FIELDS, "CapabilityId", "capability"),
                                     (SCREENS, SCREEN_FIELDS, "ScreenId", "screen"),
                                     (PHASES, PHASE_FIELDS, "PhaseId", "phase"),
                                     (DECISIONS, DEC_FIELDS, "DecisionId", "decision"),
                                     (RISKS, RISK_FIELDS, "RiskId", "risk")]:
        for r in rows:
            if len(r) != len(fields):
                errs.append(f"{label} {r.get(key)} has {len(r)} fields, expected {len(fields)}")

    # status vocabulary
    for c in CAPABILITIES:
        if c["CurrentStatus"] not in STATUS_VOCAB:
            errs.append(f"capability {c['CapabilityId']} bad status '{c['CurrentStatus']}'")

    phase_ids = {p["PhaseId"] for p in PHASES}
    dec_ids = {d["DecisionId"] for d in DECISIONS}
    risk_ids = {r["RiskId"] for r in RISKS}
    cap_ids = {c["CapabilityId"] for c in CAPABILITIES}
    scr_ids = {s["ScreenId"] for s in SCREENS}

    # every capability names a real phase
    for c in CAPABILITIES:
        if c["RecommendedPhase"] not in phase_ids:
            errs.append(f"capability {c['CapabilityId']} -> unknown phase {c['RecommendedPhase']}")
        for rid in re.findall(r"R-\d+", c["RiskIds"] or ""):
            pass  # capability RiskIds are local sequence numbers, not RiskRegister ids
        for did in re.findall(r"D-\d+[a-z]?", c["BlockingDecision"] or ""):
            if did not in dec_ids:
                errs.append(f"capability {c['CapabilityId']} -> unknown decision {did}")
        for dep in (c["Dependencies"] or "").split(","):
            dep = dep.strip()
            if dep and re.fullmatch(r"[A-Z]{3}-\d+", dep) and dep not in cap_ids:
                errs.append(f"capability {c['CapabilityId']} -> unknown dependency {dep}")

    # dependency edges resolve to a phase or a decision
    for d in DEPENDENCIES:
        for end in (d["From"], d["To"]):
            if end not in phase_ids and end not in dec_ids:
                errs.append(f"dependency {d['DependencyId']} -> unknown endpoint {end}")

    # decisions name real phases in DueGate/BlockingWork where a phase id appears
    for d in DECISIONS:
        for pid in re.findall(r"\bR\d+\b", (d["BlockingWork"] or "") + " " + (d["DueGate"] or "")):
            if pid not in phase_ids:
                errs.append(f"decision {d['DecisionId']} -> unknown phase {pid}")

    # phases name real screens and real decisions
    for p in PHASES:
        for sid in re.findall(r"SCR-\d+", p["Screens"] or ""):
            if sid not in scr_ids:
                errs.append(f"phase {p['PhaseId']} -> unknown screen {sid}")
        for did in re.findall(r"D-\d+[a-z]?", p["Prerequisites"] or ""):
            if did not in dec_ids:
                errs.append(f"phase {p['PhaseId']} -> unknown decision {did}")
        for pid in re.findall(r"\bR\d+\b", p["Prerequisites"] or ""):
            if pid not in phase_ids:
                errs.append(f"phase {p['PhaseId']} -> unknown prerequisite phase {pid}")

    # risks referenced by capabilities must not dangle when they use RSK- form
    for c in CAPABILITIES:
        for rid in re.findall(r"RSK-\d+", c["RiskIds"] or ""):
            if rid not in risk_ids:
                errs.append(f"capability {c['CapabilityId']} -> unknown risk {rid}")

    if errs:
        print("INTEGRITY VALIDATION FAILED:")
        for e in errs:
            print("  " + e)
        sys.exit(1)
    print(f"integrity OK: {len(CAPABILITIES)} capabilities, {len(SCREENS)} screens, "
          f"{len(PHASES)} phases, {len(DECISIONS)} decisions, {len(RISKS)} risks, "
          f"{len(DEPENDENCIES)} edges, {len(ADRS)} ADRs, {len(SQL_SLICES)} slices")


validate()


def write(path, text):
    """Write with LF endings and UTF-8 so output is byte-stable across runs."""
    full = os.path.join(OUT, path)
    os.makedirs(os.path.dirname(full), exist_ok=True)
    with open(full, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(text)
    return full


def write_csv(path, fields, rows, key):
    buf = io.StringIO()
    w = csv.DictWriter(buf, fieldnames=fields, lineterminator="\n")
    w.writeheader()
    for r in sorted(rows, key=lambda x: x[key]):
        w.writerow(r)
    return write(path, buf.getvalue())


def md_table(fields, rows, key, cols=None):
    """Render a Markdown table. cols limits which fields appear."""
    cols = cols or fields
    out = ["| " + " | ".join(cols) + " |", "|" + "|".join(["---"] * len(cols)) + "|"]
    for r in sorted(rows, key=lambda x: x[key]):
        out.append("| " + " | ".join(str(r[c]).replace("|", "/") for c in cols) + " |")
    return "\n".join(out)


def counts(rows, field):
    tally = {}
    for r in rows:
        tally[r[field]] = tally.get(r[field], 0) + 1
    return sorted(tally.items(), key=lambda kv: (-kv[1], kv[0]))


# ------------------------------------------------------------------ CSV outputs
files = []
files.append(write_csv("roadmap-v2-capability-catalog.csv", CAP_FIELDS, CAPABILITIES, "CapabilityId"))
files.append(write_csv("roadmap-v2-screen-inventory.csv", SCREEN_FIELDS, SCREENS, "ScreenId"))
files.append(write_csv("roadmap-v2-phase-plan.csv", PHASE_FIELDS, PHASES, "PhaseId"))
files.append(write_csv("roadmap-v2-dependencies.csv", DEP_FIELDS, DEPENDENCIES, "DependencyId"))
files.append(write_csv("roadmap-v2-decisions.csv", DEC_FIELDS, DECISIONS, "DecisionId"))
files.append(write_csv("roadmap-v2-risks.csv", RISK_FIELDS, RISKS, "RiskId"))
files.append(write_csv("roadmap-v2-tab-ownership.csv", TAB_FIELDS, TABS, "TabId"))
files.append(write_csv("roadmap-v2-shared-files.csv", SHARED_FIELDS, SHARED_FILES, "FileId"))
files.append(write_csv("roadmap-v2-adr-register.csv", ADR_FIELDS, ADRS, "AdrId"))
files.append(write_csv("roadmap-v2-sql-slice-register.csv", SLICE_FIELDS, SQL_SLICES, "SliceId"))

# ------------------------------------------------------------- 02 capability catalog
status_rows = counts(CAPABILITIES, "CurrentStatus")
mod_rows = counts(CAPABILITIES, "PlatformOrModule")

doc = ["# CrossBusiness Platform — Roadmap v2 — 02 System Capability Catalog", "",
       "**One canonical catalog. Generated from `_generator/roadmap_v2_data.py`; the CSV and this",
       "table come from the same rows, so they cannot disagree.**", "",
       f"Capabilities: **{len(CAPABILITIES)}** across **{len(mod_rows)}** platforms and modules.",
       "", "Machine-readable: `roadmap-v2-capability-catalog.csv`.", "",
       "---", "", "## 1. Status vocabulary", "",
       "Only these values are used. `VerifiedComplete` means evidence exists; it never means",
       "\"a user can do this\" — that is `ProductionActivation` plus `UIStatus`.", "",
       "| Status | Meaning |", "|---|---|",
       "| VerifiedComplete | Implemented and proved by test or mutation evidence |",
       "| ImplementedNotActivated | Code complete, not reachable in production |",
       "| FoundationComplete | Services and schema complete, no user surface |",
       "| ArchitectureComplete | Design decided, little or no code |",
       "| Partial | Some capability, materially incomplete |",
       "| LegacyExisting | Pre-existing working capability, not re-verified |",
       "| RemediationRequired | Exists but carries a known defect or gap |",
       "| Planned | Agreed, not started |",
       "| BlockedByDecision | Cannot proceed without an owner decision |",
       "| BlockedByData | Cannot proceed without a migration or measurement |",
       "| Deferred | Deliberately postponed |",
       "| NotStarted | No source |", "",
       "## 2. Distribution", "",
       "| Status | Capabilities |", "|---|---|"]
doc += [f"| {k} | {v} |" for k, v in status_rows]
doc += ["", "| Platform or module | Capabilities |", "|---|---|"]
doc += [f"| {k} | {v} |" for k, v in mod_rows]
doc += ["", "## 3. Catalog — summary view", "",
        md_table(CAP_FIELDS, CAPABILITIES, "CapabilityId",
                 ["CapabilityId", "PlatformOrModule", "Capability", "CurrentStatus",
                  "ProductionActivation", "UIStatus", "TestEvidence", "RecommendedPhase"]),
        "", "## 4. Catalog — governance view", "",
        md_table(CAP_FIELDS, CAPABILITIES, "CapabilityId",
                 ["CapabilityId", "SecurityStatus", "MultiTenantStatus", "Dependencies",
                  "BlockingDecision", "RiskIds", "SourceEvidence"]),
        "", "## 5. Catalog — full field set", "",
        "The complete 22-field record for every capability is in the CSV; it is too wide to render",
        "legibly here. `Description`, `ImplementationLevel`, `APIStatus`, `DatabaseStatus`,",
        "`ReportingStatus`, `CommunicationStatus`, `MobileStatus` and `AIReadiness` appear there.", ""]
files.append(write("Roadmap-V2-02-System-Capability-Catalog.md", "\n".join(doc)))

# ---------------------------------------------------------------- 04 screen inventory
scr_counts = counts(SCREENS, "Status")
doc = ["# CrossBusiness Platform — Roadmap v2 — 04 Screen Inventory", "",
       "**What a user can actually open today, and what is only planned.**", "",
       "Existing screen counts are measured from `CrossBuy/Views/<folder>/*.cshtml` on disk",
       "(327 `.cshtml` files in total, including partials and shared layouts).", "",
       "Machine-readable: `roadmap-v2-screen-inventory.csv`.", "", "---", "",
       "## 1. Explicit statements required of this inventory", "",
       "| Statement | Status |", "|---|---|",
       "| Business Event Monitor | **DELIVERED** — 3 views, the one delivered platform operational screen |",
       "| Reporting user interfaces | **NOT DELIVERED** — no Views/Reporting folder exists |",
       "| Communication user interfaces | **NOT DELIVERED** — the 3 Views/Comm files are the *legacy* Comm module, not the Communication Platform |",
       "| Construction C1 | **NO FINAL UI** — C1 delivered services and DDL only |",
       "| Security Console | **NOT STARTED** |",
       "| Report Studio | **NOT STARTED** |",
       "| Task/Calendar integrated workspace | **NOT STARTED** |", "",
       "## 2. Distribution", "", "| Category | Screens |", "|---|---|"]
doc += [f"| {k} | {v} |" for k, v in scr_counts]
doc += ["", "`ExistingWeak` means the screen opens but does not carry the capability its module implies.", "",
        "## 3. Inventory", "",
        md_table(SCREEN_FIELDS, SCREENS, "ScreenId",
                 ["ScreenId", "Category", "OwnerPlatformOrModule", "Screen", "Status", "Purpose", "Evidence"]),
        "", "## 4. Planned screens — build contract", "",
        "Every planned screen carries prerequisites, permissions, data sources, workflow, reporting,",
        "communication and mobile relevance:", "",
        md_table(SCREEN_FIELDS, [s for s in SCREENS if s["Status"] == "Planned"], "ScreenId",
                 ["ScreenId", "Screen", "Prerequisites", "Permissions", "DataSources", "Workflow",
                  "Reporting", "Communication", "MobileRelevance"]),
        "", "## 5. Permanent UI rule", "",
        "**Do not invent the final design.** Before implementing any screen, request the approved",
        "Metronic/theme reference from the owner and follow **CrossBusiness Blue**. Every planned",
        "screen above carries `REQUEST owner Metronic reference before build` in its",
        "`DesignReference` field; that is a gate, not a note.", "",
        "Existing specialized operational screens (POS, KDS, hypermarket lane, mobile attendance,",
        "storefront) keep their domain UX and are not to be restyled into the admin shell.", ""]
files.append(write("Roadmap-V2-04-Screen-Inventory.md", "\n".join(doc)))

# ------------------------------------------------------------------- 07 roadmap
doc = ["# CrossBusiness Platform — Roadmap v2 — 07 Implementation Roadmap", "",
       "**Phases re-derived from dependencies, not inherited from the old stage numbering.**", "",
       "Machine-readable: `roadmap-v2-phase-plan.csv`, `roadmap-v2-dependencies.csv`.", "",
       "---", "", "## 1. Why the numbering changed", "",
       "The old sequence assumed a single tree with one team. Four tabs now build concurrently, three",
       "platforms are complete-but-unusable, and the deployment tree has split in two. The binding",
       "constraint is no longer feature scope — it is **integration**. So R1 is not a feature phase:",
       "it makes the tree deployable before anything else ships.", "",
       "R2, R3 and R4 then convert the three existing foundations into product, because a foundation",
       "with no consumer decays: it drifts from real data shapes and cannot be validated.", "",
       "## 2. Phase overview", "",
       md_table(PHASE_FIELDS, PHASES, "PhaseId",
                ["PhaseId", "Name", "Objective", "Prerequisites", "Owner", "Complexity", "ParallelSafety"]),
       "", "## 3. Dependency edges", "",
       md_table(DEP_FIELDS, DEPENDENCIES, "DependencyId"),
       "", "## 4. Phase detail", ""]
for p in sorted(PHASES, key=lambda x: (len(x["PhaseId"]), x["PhaseId"])):
    doc += [f"### {p['PhaseId']} — {p['Name']}", "",
            f"**Objective.** {p['Objective']}", "",
            f"**Business value.** {p['BusinessValue']}", "",
            "| Field | Value |", "|---|---|",
            f"| Included | {p['Included']} |",
            f"| Excluded | {p['Excluded']} |",
            f"| Prerequisites | {p['Prerequisites']} |",
            f"| Owner | {p['Owner']} |",
            f"| Screens | {p['Screens']} |",
            f"| Services | {p['Services']} |",
            f"| Database work | {p['DatabaseWork']} |",
            f"| API work | {p['APIWork']} |",
            f"| Security gates | {p['SecurityGates']} |",
            f"| Migration gates | {p['MigrationGates']} |",
            f"| Test gates | {p['TestGates']} |",
            f"| Complexity | {p['Complexity']} |",
            f"| Parallelization safety | {p['ParallelSafety']} |", "",
            f"**Completion criteria.** {p['CompletionCriteria']}", ""]
files.append(write("Roadmap-V2-07-Implementation-Roadmap.md", "\n".join(doc)))

# ------------------------------------------------------------- 10 decision register
open_count = len([d for d in DECISIONS if d["Status"].startswith("Open")])
doc = ["# CrossBusiness Platform — Roadmap v2 — 10 Decision Register", "",
       f"**{len(DECISIONS)} unresolved business decisions, {open_count} open.**", "",
       "Machine-readable: `roadmap-v2-decisions.csv`.", "", "---", "",
       "## 1. Rule", "",
       "**No decision here is silently chosen.** Each carries a recommendation because a recommendation",
       "is useful, but the recommendation is not the decision and no work proceeds on it. Where a",
       "decision blocks work, the blocked phase is named.", "",
       "## 2. Register", "",
       md_table(DEC_FIELDS, DECISIONS, "DecisionId",
                ["DecisionId", "Area", "Subject", "Recommendation", "BlockingWork", "DueGate", "Status"]),
       "", "## 3. Full detail", ""]
for d in sorted(DECISIONS, key=lambda x: x["DecisionId"]):
    doc += [f"### {d['DecisionId']} — {d['Subject']} ({d['Area']})", "",
            f"**Evidence.** {d['Evidence']}", "", f"**Options.** {d['Options']}", "",
            f"**Recommendation.** {d['Recommendation']}", "",
            f"**Impact.** {d['Impact']} · **Blocks:** {d['BlockingWork']} · **Due:** {d['DueGate']} · **Status:** {d['Status']}", ""]
files.append(write("Roadmap-V2-10-Decision-Register.md", "\n".join(doc)))

# ----------------------------------------------------------------- 11 risk register
doc = ["# CrossBusiness Platform — Roadmap v2 — 11 Risk Register", "",
       f"**{len(RISKS)} risks. {len([r for r in RISKS if r['Severity']=='High'])} High.**", "",
       "Machine-readable: `roadmap-v2-risks.csv`.", "", "---", "",
       "## 1. Risks discovered by this reassessment", "",
       "Three were not in any prior report and came from reading the tree rather than the reports:", "",
       "* **RSK-01** — two `deploy/sql` trees exist. 57 slices in `deploy/sql`, 60 in",
       "  `CrossBuy/deploy/sql`, only 4 filenames in both, and **all 4 differ**. Zero identical.",
       "* **RSK-02** — `platform_schema_history.sql`, the table that is supposed to record what has",
       "  been applied, is itself unapplied. The deployment tracker is untracked.",
       "* **RSK-13** — Tasks and Calendar have **live registered hosted services** and no tab owner.", "",
       "## 2. Register", "",
       md_table(RISK_FIELDS, RISKS, "RiskId",
                ["RiskId", "Area", "Risk", "Severity", "Likelihood", "Impact", "Mitigation", "Status"]),
       "", "## 3. Evidence", "",
       md_table(RISK_FIELDS, RISKS, "RiskId", ["RiskId", "Evidence", "Owner"]), ""]
files.append(write("Roadmap-V2-11-Risk-Register.md", "\n".join(doc)))

# ------------------------------------------------------ 09 parallel execution model
doc = ["# CrossBusiness Platform — Roadmap v2 — 09 Parallel Execution Model", "",
       "**The problem this solves: five cross-tab build breaks from three tabs during Stage 2A,",
       "and a whole platform tree that is still uncommitted.**", "",
       "Machine-readable: `roadmap-v2-tab-ownership.csv`, `roadmap-v2-shared-files.csv`,",
       "`roadmap-v2-adr-register.csv`, `roadmap-v2-sql-slice-register.csv`.", "", "---", "",
       "## 1. What actually went wrong", "",
       "| Observation | Consequence |", "|---|---|",
       "| Five build breaks from three tabs (Reporting `ReportParameterSet`/`IReportService`, Communication `ICommActorDirectory`, Construction `CrossBuy.BL.Construction`/`BoqLineInput`) | Acceptance blocked repeatedly; one increment had to run on an older binary and declare it |",
       "| `git ls-files` returns **empty** for the whole `Platform/` tree | Every tab's base can shift invisibly; selective commits diff against nothing stable |",
       "| Two `deploy/sql` trees, 4 overlapping files, all 4 different | A deploy can apply the wrong version of a slice |",
       "| A stale assembly produced a passing mutation test | Evidence that proved nothing |", "",
       "The common cause is not carelessness — it is that **a shared mutable tree has no mechanism",
       "to reject a broken state**. Every model below is judged on whether it does.", "",
       "## 2. Model comparison", "",
       "| Model | Isolation | Break containment | Integration cost | Verdict |", "|---|---|---|---|---|",
       "| 1. Single shared working tree (today) | None | None — one tab breaks all four | Zero | **Rejected.** This is the observed failure. |",
       "| 2. Sequential tabs | Total | Total | Zero | **Rejected.** Four tabs would run at one quarter speed. |",
       "| 3. Per-tab branch | High | High | Merge conflicts on shared files | **Viable**, but each tab still needs its own checkout to build. |",
       "| 4. Per-tab git worktree | High | High | Same as 3, plus disk | **RECOMMENDED.** Each tab gets a real working directory on its own branch from one clone. |",
       "| 5. Integration branch | N/A | N/A | N/A | **Required alongside 4** — this is where the four branches meet and must stay green. |", "",
       "## 3. Recommendation — per-tab worktree plus a protected integration branch", "",
       "Model **4 + 5**. Each tab develops in its own worktree on its own branch; an `integration`",
       "branch is the single place they combine, and it must build.", "",
       "Worktrees rather than separate clones because the four tabs share one object store and one",
       "SQL slice registry — a second clone would let the registries diverge exactly the way",
       "`deploy/sql` already has.", "",
       "### The binding rule", "",
       "> **No tab may leave the shared integration branch unbuildable.**", "",
       "Operationally: a tab merges to `integration` only after `dotnet build` succeeds in **Debug,",
       "Release and TestRun** in its own worktree, and the full suite is green. A merge that breaks",
       "`integration` is reverted immediately — not fixed forward, because a fix-forward leaves the",
       "other three tabs blocked for the duration.", "",
       "## 4. Rules", "", "| Rule | Statement |", "|---|---|",
       "| Buildability | Debug, Release and TestRun all build before merge. A build error count is captured explicitly; `--no-build` is never the build. |",
       "| Commit | Every tab commits its own files daily. The current state — an entire untracked platform tree — is itself the top process risk (RSK-06). |",
       "| Shared file | Shared files are edited only inside the tab's marked region, or by request to the Integration Owner. resx files get our keys only via git plumbing, never whole-file `git add`. |",
       "| ADR number | Reserve in the ADR registry before writing. ADR-011, 014, 015, 017–021 are free; ADR-038–040 are reserved for R1/R3. |",
       "| SQL slice | Claim the slice name in the SQL registry before authoring. One tree only after R1. |",
       "| Migration ownership | The owning tab authors the slice; the Integration Owner sequences application. Idempotent SQL only — never EF migrations. |",
       "| Program.cs | Integration Owner. Each platform exposes ONE registration extension method (the Reporting tab's `AddCrossBusinessReporting` is the pattern to copy) so the shared file gains one line, not forty. |",
       "| CrossDbContext | Integration Owner. DbSets added in the tab's marked region; never reordered, never reformatted. 245 DbSets make whole-file edits certain conflicts. |",
       "| Integration cadence | Daily merge to `integration` per tab. |",
       "| Full-suite cadence | Full suite on `integration` at least daily, and before any tab starts a new phase. |",
       "| Rollback | Revert the merge commit, not the tab's branch. The tab keeps its history and re-merges when green. |",
       "| Artifact preservation | Every increment archives its own files with SHA-256 per file and a restore-verify requiring 0 mismatches. The archive hash is recorded outside the archive. |",
       "| Stale-build prevention | Delete build outputs before an acceptance build; record the assembly timestamp; never accept a test result from an unverified build. |", "",
       "## 5. Tab Ownership Register", "",
       md_table(TAB_FIELDS, TABS, "TabId", ["TabId", "Tab", "Scope", "Status"]), "",
       "### Owned paths", "",
       md_table(TAB_FIELDS, TABS, "TabId", ["TabId", "OwnedPaths", "MayNotTouch"]), "",
       "**Two modules have no owner and live hosted services: Tasks and Calendar** (D-32, D-33).",
       "Assign before R5.", "",
       "## 6. Shared File Register", "",
       md_table(SHARED_FIELDS, SHARED_FILES, "FileId"), "",
       "## 7. ADR Registry", "",
       md_table(ADR_FIELDS, ADRS, "AdrId"), "",
       "## 8. SQL Slice Registry", "",
       "**This registry exists because the SQL tree has already split.** RSK-01 is the highest-severity",
       "finding of this reassessment.", "",
       md_table(SLICE_FIELDS, SQL_SLICES, "SliceId"), "",
       "## 9. Integration Gate Checklist", "",
       "A merge to `integration` is accepted only when every line is true:", "",
       "| # | Gate |", "|---|---|",
       "| 1 | Debug build `0 Error(s)` — error count captured, not inferred |",
       "| 2 | Release build `0 Error(s)` |",
       "| 3 | TestRun build `0 Error(s)` |",
       "| 4 | Full suite green, skipped count explained (never claim coverage from a skip) |",
       "| 5 | Analyzer gates: CBA001 = 0, CBA004 = 0, CBA006 = 0 |",
       "| 6 | Authorization debt unchanged or **lower**, reconciled if changed |",
       "| 7 | No suppressions, no baseline additions |",
       "| 8 | Only files in the tab's owned paths are modified |",
       "| 9 | Shared-file edits confined to the tab's marked region |",
       "| 10 | Any new SQL slice is claimed in the registry and idempotent |",
       "| 11 | Any new hosted service is added to the DI wiring test |",
       "| 12 | 0 probe databases, 0 mutation markers left behind |", ""]
files.append(write("Roadmap-V2-09-Parallel-Execution-Model.md", "\n".join(doc)))

# ---------------------------------------------------------------- 01 verified baseline
doc = ["# CrossBusiness Platform — Roadmap v2 — 01 Verified Baseline", "",
       f"**Snapshot date: {BASELINE_DATE}.** Every number here is reconciled against the B6 final",
       "delivery report and re-measured from the tree during this reassessment.", "", "---", "",
       "## 1. Stage 2A / B6", "", "| Metric | Value |", "|---|---|",
       f"| B6 status | **{BASELINE['b6_status']}** |",
       f"| Debug build | {BASELINE['build_debug']} |",
       f"| Release build | {BASELINE['build_release']} |",
       f"| TestRun build | {BASELINE['build_testrun']} |",
       f"| Full application suite | **{BASELINE['tests_passed']} passed · {BASELINE['tests_failed']} failed · {BASELINE['tests_skipped']} skipped** |",
       f"| Analyzer tests | {BASELINE['analyzer_tests']} |",
       f"| Analyzer gates | {BASELINE['cba']} |",
       f"| Authorization debt | {BASELINE['auth_debt']} |",
       f"| Detected mutating actions | {BASELINE['actions_total']} |",
       f"| Attribute-secured | {BASELINE['attr_secured']} |",
       f"| In-body secured | {BASELINE['inbody_secured']} |",
       f"| Probe databases | {BASELINE['probe_dbs']} |",
       f"| Mutation markers | {BASELINE['mutation_markers']} |",
       f"| CrossBuyDB2 | {BASELINE['crossbuydb2']} |",
       f"| Preservation | {BASELINE['preservation_files']} files, {BASELINE['preservation_mismatches']} restore mismatches |", "",
       "### The endpoint arithmetic", "",
       f"**{BASELINE['attr_secured']} + {BASELINE['inbody_secured']} + {BASELINE['auth_debt']} = {BASELINE['actions_total']}.**",
       "The accepted Stage 1 measurement was 388 / 157 / 88 / 143. The `+3` on both total and",
       "in-body is Batch A's `PlatformGrantsApiController.Create` / `.Revoke` / `.UpdateValidity`,",
       "all authorized in-body through `IPlatformGrantWriter`. **Debt did not move**, and the gap set",
       "matches the baseline id for id. B6 itself changed only services and added no endpoint.", "",
       "### B6 scope, stated exactly", "",
       "| Site | Status |", "|---|---|",
       "| `AccountingAccessService.cs:60` | CONVERTED |",
       "| `InventoryAccessService.cs:48` | CONVERTED |",
       "| `InventoryAccessService.cs:91` | CONVERTED (narrows behaviour) |",
       "| `CrmAccessService.cs:59` → `return true` | **UNCHANGED — intentional** |",
       "| `CrmAccessService.cs:98` → `return null` | **UNCHANGED — intentional** |", "",
       "B6 replaced the **permission source only**. It changed no query filter, no row-level rule and",
       "no returned data.", "",
       "## 2. Other platform baselines", "", "| Platform | Tests | Schema | Activation | UI |", "|---|---|---|---|---|",
       f"| Reporting | {BASELINE['reporting_tests']} | {BASELINE['reporting_tables']} tables, from-empty and second-apply idempotency proved | Registered via `AddCrossBusinessReporting` | **None** |",
       f"| Communication | {BASELINE['communication_tests']} | {BASELINE['communication_tables']} tables, from-empty and idempotency proved | **Not activated in Program.cs** | **None** |",
       f"| Construction C1 | {BASELINE['construction_tests']} | {BASELINE['construction_tables_ddl']} tables in DDL, **applied nowhere** | Services registered in Program.cs | **None** |", "",
       "Reporting: Dataset Layer, catalog, templates, history, archive, HTML/CSV/Excel implemented.",
       "PDF runtime, email delivery and hosted scheduler deferred. No production module data source.", "",
       "Communication: `CommEntityRef` and `ICommEntitySurface` are the common model; permission",
       "boundary mechanically proved. Task and Support unregistered; DocComments migration not executed.", "",
       "Construction: CR-01/CR-02/CR-03 mutation-proven closed. No DDL applied, no backfill executed.", "",
       "## 3. Measured from the tree during this reassessment", "",
       "| Measure | Value |", "|---|---|",
       "| MVC controllers | 33 |", "| API controllers | 11 |",
       "| Razor views (`.cshtml`, incl. partials and shared) | 327 |",
       "| Top-level BL services | 121 |",
       "| `DbSet<>` declarations in `CrossDbContext` | 245 |",
       "| Registered hosted services | 8 |",
       "| SQL slices — `deploy/sql` | 57 |",
       "| SQL slices — `CrossBuy/deploy/sql` | 60 |",
       "| Slices present in **both** trees | 4 — **and all 4 differ** |",
       "| Test files in `CrossBuy.Tests` | 83 |",
       "| ADRs written | 29 (ADR-001…037, with gaps) |",
       "| Flutter mobile screens | 10 |", "",
       "## 4. Corrections this reassessment makes to prior assumptions", "",
       "| Prior assumption | Verified position |", "|---|---|",
       "| \"Communication has no hosted worker\" | True **for the Communication Platform**. But the *legacy* `BL/Comm` module does register `CommMessageDispatcherHostedService`, and `CommController` plus 3 views exist. Two different things share a prefix. |",
       "| \"Task Management is planned\" | **Wrong.** Tasks has a controller, 5 views, 7 SQL slices (TM-1…TM-9), two registered hosted services and an access service. It is `LegacyExisting`, materially incomplete, and **unowned**. |",
       "| \"Calendar is planned\" | **Wrong.** `CalendarService`, `CalendarController`, `calendar.sql` and 1 view exist. `Partial`, and unowned. |",
       "| \"One `deploy/sql`\" | **Wrong.** Two trees, nearly disjoint, 4 overlapping files all different. |",
       "| \"183 skipped tests\" | Those were SQL evidence tests skipping without `CROSSBUY_TEST_SQL`. With it configured the suite runs **1502 / 0 / 0**. |", ""]
files.append(write("Roadmap-V2-01-Verified-Baseline.md", "\n".join(doc)))

# ------------------------------------------------------------------- 08 dependency map
doc = ["# CrossBusiness Platform — Roadmap v2 — 08 Dependency Map", "",
       "Machine-readable: `roadmap-v2-dependencies.csv`.", "", "---", "",
       "## 1. Phase dependency graph", "", "```mermaid", "graph LR",
       "  R0[R0 Reconciliation]:::done --> R1[R1 Integration Foundation]",
       "  R1 --> R2[R2 Comm Activation + Workspace]",
       "  R1 --> R3[R3 Security Console + CRM + MDM]",
       "  R1 --> R4[R4 Report Center + Studio]",
       "  R1 --> R7[R7 Construction C2-C8]",
       "  R1 --> R9[R9 WMS + Manufacturing]",
       "  R2 --> R5[R5 Tasks + Calendar]",
       "  R3 --> R6[R6 CRM + Support Workspace]",
       "  R5 --> R6",
       "  R3 --> R8[R8 HR + Workforce]",
       "  R4 --> R10[R10 Financial Ops]",
       "  R4 --> R11[R11 Portals]",
       "  R6 --> R11",
       "  R4 --> R12[R12 AI + Search]",
       "  R10 --> R12",
       "  R7 --> R13[R13 Industry Packs]",
       "  R9 --> R13",
       "  R5 --> R14[R14 Mobile + Field]",
       "  R7 --> R14",
       "  R12 --> R15[R15 Ecosystem]",
       "  classDef done fill:#13433a,color:#fff,stroke:#0d2f28;", "```", "",
       "## 2. Decision blockers on the critical path", "", "```mermaid", "graph TD",
       "  D03[D-03 CRM data breadth]:::blocked --> R3",
       "  D04[D-04 CRM owner scope]:::blocked --> R3",
       "  D05[D-05 privacy ceiling]:::blocked --> R2",
       "  D35[D-35 first dataset]:::blocked --> R4",
       "  D19[D-19 DDL environment]:::blocked --> R7",
       "  D34[D-34 SQL tree consolidation]:::blocked --> R1",
       "  R1 --> R2 --> R5",
       "  R1 --> R3",
       "  R1 --> R4",
       "  R1 --> R7",
       "  classDef blocked fill:#8a1c1c,color:#fff,stroke:#5c1212;", "```", "",
       "## 3. Edge list", "", md_table(DEP_FIELDS, DEPENDENCIES, "DependencyId"), "",
       "## 4. Capability dependency clusters", "",
       "| Cluster | Root | Dependent capabilities |", "|---|---|---|",
       "| Business context | PLT-01 | Every module access service, every company-scoped read |",
       "| Business events | PLT-02 | PLT-04, PLT-05, SEC-05, reversal on every correction path |",
       "| Entity registry | PLT-03 | COM-05, PLT-05, SRC-01 |",
       "| Comm activation | COM-08 | COM-09, COM-06, SUP-02, TSK-05, REP-05 |",
       "| Reporting datasets | REP-01 | REP-02..REP-09, ACC-07, AI-01, SRC-01 |",
       "| Construction C1 rollout | CON-04 | CON-05, AUD-01, SCR-38, SCR-39 |",
       "| Bootstrap policy | SEC-02 | SEC-03, SEC-04, SEC-08 |", ""]
files.append(write("Roadmap-V2-08-Dependency-Map.md", "\n".join(doc)))

# ------------------------------------------- R1 enforceable governance registries (JSON)
# The CI gates must not depend on Python or on parsing Markdown, so the registries are emitted
# as JSON next to the tools that read them. Same canonical dataset as the documents above:
# one source, two renderings. A registry that drifts from the roadmap is the exact defect the
# two deploy/sql trees demonstrate.
REPO = os.path.abspath(os.path.join(OUT, os.pardir, os.pardir))
REG = os.path.join(REPO, "governance", "registry")


def write_json(name, payload):
    os.makedirs(REG, exist_ok=True)
    full = os.path.join(REG, name)
    with open(full, "w", encoding="utf-8", newline="\n") as fh:
        json.dump(payload, fh, indent=2, ensure_ascii=False, sort_keys=False)
        fh.write("\n")
    return full


import json  # noqa: E402  (kept local to the registry block)

files.append(write_json("tab-ownership.json", {
    "$comment": "Generated from docs/roadmap-v2/_generator. Do not hand-edit; re-run the generator.",
    "tabs": [{"tabId": t["TabId"], "tab": t["Tab"], "scope": t["Scope"],
              # An ownedPaths entry may carry a human annotation: "governance/** (R1 tools)".
              # The glob is everything before " (" - keeping the annotation would make the path
              # match nothing, and a rule that matches nothing is a rule that is not enforced.
              "ownedPaths": [p.strip().split(" (")[0].strip()
                             for p in t["OwnedPaths"].split(";") if p.strip()],
              "mayNotTouch": t["MayNotTouch"], "status": t["Status"]}
             for t in sorted(TABS, key=lambda x: x["TabId"])]}))

files.append(write_json("shared-files.json", {
    "$comment": "Files no single tab may edit freely. changeRule is enforced by review, "
                "ownership by governance/tools/check-file-ownership.ps1.",
    "sharedFiles": [{"fileId": s["FileId"], "path": s["File"], "owner": s["Owner"],
                     "changeRule": s["ChangeRule"], "reason": s["Reason"]}
                    for s in sorted(SHARED_FILES, key=lambda x: x["FileId"])]}))

files.append(write_json("adr-registry.json", {
    "$comment": "Reserve an ADR number here BEFORE writing the document.",
    "adrs": [{"adrId": a["AdrId"], "title": a["Title"], "area": a["Area"],
              "owner": a["Owner"], "status": a["Status"]}
             for a in sorted(ADRS, key=lambda x: x["AdrId"])],
    "available": [a["AdrId"] for a in sorted(ADRS, key=lambda x: x["AdrId"])
                  if a["Status"] == "Available"]}))

files.append(write_json("sql-slices.json", {
    "$comment": "Slice claims. The AUTHORED registry of record is CrossBuy/deploy/sql/manifest.json "
                "(generated by scan-sql-manifest.ps1); this file records ownership, canonical-root "
                "placement and deployment posture per slice.",
    "canonicalAuthoredRoot": "CrossBuy/deploy/sql",
    "packageRoot": "deploy",
    "authoredRegistry": "CrossBuy/deploy/sql/manifest.json",
    "appliedRegistry": "dbo.PlatformSchemaHistory",
    "slices": [{"sliceId": s["SliceId"], "slice": s["Slice"], "tree": s["Tree"],
                "owner": s["Owner"], "appliedStatus": s["AppliedStatus"],
                "gatesCorePath": s["GatesOurPath"], "note": s["Note"]}
               for s in sorted(SQL_SLICES, key=lambda x: x["SliceId"])]}))

files.append(write_json("manifest-facts.json", {
    "$comment": "Snapshot of the authored registry at R1 design time, so a gate can detect that "
                "manifest.json has moved without anyone noticing.",
    **MANIFEST_FACTS}))

print(f"generated {len(files)} files")
for f in sorted(files):
    print("  " + os.path.relpath(f, REPO))
