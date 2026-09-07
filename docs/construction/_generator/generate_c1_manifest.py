#!/usr/bin/env python3
# ==============================================================================================
# Construction C1 — DISK-STATE MANIFEST
#
# Emits Stage-Construction-C1-Disk-State-Manifest.csv: every file this increment created or
# modified, with its SHA-256, byte size, line count and role — production source, deployment SQL,
# test, or document.
#
# "Disk state" means the tree as it is, not as it is remembered: each row is read from the file on
# disk at the moment the script runs, and a file listed here that does not exist is reported as
# MISSING rather than silently skipped.
#
# Run from the repository root:  python docs/construction/_generator/generate_c1_manifest.py
# ==============================================================================================
import csv
import hashlib
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
OUT = os.path.join(ROOT, "docs", "construction", "Stage-Construction-C1-Disk-State-Manifest.csv")

# (path, change, role, purpose)  — change is CREATED or MODIFIED relative to the pre-C1 tree.
FILES = [
    # ---- production source -----------------------------------------------------------------
    ("CrossBuy/Models/Context/Construction/ConstructionCommercial.cs", "CREATED", "production-source",
     "8 C1 entities, status vocabularies and the EF model configuration"),
    ("CrossBuy/BL/Construction/ConstructionAuditService.cs", "CREATED", "production-source",
     "Append-only line-level audit + the concurrency token helper"),
    ("CrossBuy/BL/Construction/CommercialRevisionService.cs", "CREATED", "production-source",
     "CR-03 immutable commercial revisions, certificate snapshots, historical reproduction"),
    ("CrossBuy/BL/Construction/SubcontractScopeService.cs", "CREATED", "production-source",
     "CR-02 subcontract scope, hard cap (D-07), line-level certification"),
    ("CrossBuy/BL/BoqService.cs", "MODIFIED", "production-source",
     "CR-01 stable-identity save; ReplaceAllAsync no longer deletes"),
    ("CrossBuy/Models/Context/CrossDbContext.cs", "MODIFIED", "production-source-shared",
     "8 DbSets + one ConstructionCommercialModel.Configure call (additive)"),
    ("CrossBuy/Program.cs", "MODIFIED", "production-source-shared",
     "3 AddScoped registrations for the C1 services (additive)"),

    # ---- deployment SQL (NOT executed) ------------------------------------------------------
    ("deploy/sql/construction_c1_commercial_foundation.sql", "CREATED", "deployment-sql",
     "Idempotent additive DDL for the 7 new C1 tables. NOT EXECUTED."),
    ("deploy/sql/construction_c1_contract_mapping_measurement.sql", "CREATED", "deployment-sql",
     "Read-only measurement (SELECT only) for the D-01 mapping decision. NOT EXECUTED."),

    # ---- tests --------------------------------------------------------------------------------
    ("CrossBuy.Tests/ConstructionTestFixture.cs", "CREATED", "test",
     "SQLite shared in-memory fixture over the real CrossDbContext model"),
    ("CrossBuy.Tests/ConstructionC1BoqIdentityTests.cs", "CREATED", "test",
     "CR-01 — 10 tests including the re-billing money test"),
    ("CrossBuy.Tests/ConstructionC1SubcontractCapTests.cs", "CREATED", "test",
     "CR-02 — 10 tests including the hard-block and value-cap tests"),
    ("CrossBuy.Tests/ConstructionC1CommercialRevisionTests.cs", "CREATED", "test",
     "CR-03 — 7 tests including historical reproduction"),
    ("CrossBuy.Tests/ConstructionC1ConcurrencyAndAuditTests.cs", "CREATED", "test",
     "Concurrency and audit — 9 tests including two-context conflict and append-only structure"),

    # ---- C1 documents -------------------------------------------------------------------------
    ("docs/construction/Stage-Construction-C1-Commercial-Foundation-Plan.md", "CREATED", "document",
     "The C1 plan: scope, the constraint that shaped the design, what changed"),
    ("docs/construction/Stage-Construction-CR01-BOQ-Identity-Evidence.md", "CREATED", "document",
     "CR-01 defect, remediation, tests, mutation proof"),
    ("docs/construction/Stage-Construction-CR02-Subcontract-Cap-Evidence.md", "CREATED", "document",
     "CR-02 defect, remediation, tests, mutation proof, scope boundary"),
    ("docs/construction/Stage-Construction-CR03-Commercial-Revision-Evidence.md", "CREATED", "document",
     "CR-03 defect, remediation, tests, mutation proof"),
    ("docs/construction/Stage-Construction-Contract-Mapping-Measurement.md", "CREATED", "document",
     "D-01 measurement specification, decision rule, results form"),
    ("docs/construction/Stage-Construction-Concurrency-and-Audit-Design.md", "CREATED", "document",
     "Token design, audit design, the declared base-table gap"),
    ("docs/construction/Stage-Construction-C1-Delivery-Report.md", "CREATED", "document",
     "C1 delivery report and completion-gate table"),
    ("docs/construction/Stage-Construction-C1-Preservation-Report.md", "CREATED", "document",
     "Hashes, archive, restore verification"),
    ("docs/construction/mutation-proof-results.json", "CREATED", "evidence",
     "Raw mutation-proof output (baseline / mutated / restored per mutation)"),

    # ---- tooling ------------------------------------------------------------------------------
    ("docs/construction/_generator/run_mutation_proofs.py", "CREATED", "tooling",
     "Applies, proves and restores mutations C-01/C-02/C-03; sweeps for leftover markers"),
    ("docs/construction/_generator/generate_c1_manifest.py", "CREATED", "tooling",
     "This script"),
    ("docs/construction/_generator/generate_construction_catalogs.py", "MODIFIED", "tooling",
     "Roadmap extended with C11 (schedule baselines / Primavera-MSProject import) and C12 (resource leveling)"),
]

FIELDS = ["Path", "Change", "Role", "Purpose", "Bytes", "Lines", "SHA256"]


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    rows, missing = [], []
    for rel, change, role, purpose in FILES:
        full = os.path.join(ROOT, rel)
        if not os.path.isfile(full):
            missing.append(rel)
            rows.append([rel, change, role, purpose, 0, 0, "MISSING"])
            continue
        with open(full, "rb") as f:
            data = f.read()
        rows.append([rel, change, role, purpose, len(data),
                     data.count(b"\n") + (0 if data.endswith(b"\n") or not data else 1),
                     sha256(full)])

    with open(OUT, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(FIELDS)
        w.writerows(rows)

    by_role = {}
    for r in rows:
        by_role[r[2]] = by_role.get(r[2], 0) + 1
    created = sum(1 for r in rows if r[1] == "CREATED")
    modified = sum(1 for r in rows if r[1] == "MODIFIED")

    print(f"wrote {os.path.relpath(OUT, ROOT)}")
    print(f"  files: {len(rows)}   created: {created}   modified: {modified}")
    for role in sorted(by_role):
        print(f"    {role:26} {by_role[role]}")
    print(f"  total bytes: {sum(r[4] for r in rows)}")
    if missing:
        print(f"  MISSING: {missing}")
        return 1
    print("  every listed file exists on disk")
    return 0


if __name__ == "__main__":
    sys.exit(main())
