#!/usr/bin/env python3
# ==============================================================================================
# Verification for the FOURTH TAB construction deliverables.
#
#  1. every deliverable file exists and is non-empty
#  2. every repository path cited inside the deliverables resolves on disk
#  3. every SHA-256 is recorded (manifest)
#  4. nothing outside docs/construction is written by this script
#
# Run from the repository root:  python docs/construction/_generator/verify_deliverables.py
# Exit code 0 = all checks pass; 1 = at least one failure (printed).
# ==============================================================================================
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.dirname(HERE)                       # docs/construction
ROOT = os.path.dirname(os.path.dirname(OUT))      # repository root

DELIVERABLES = [
    "Stage-Construction-00-Executive-Assessment.md",
    "Stage-Construction-01-Repository-Inventory.md",
    "Stage-Construction-02-Current-Capability-Matrix.md",
    "Stage-Construction-03-Domain-Boundaries.md",
    "Stage-Construction-04-WBS-and-BOQ-Design.md",
    "Stage-Construction-05-Cost-Control-Design.md",
    "Stage-Construction-06-Contracts-and-Certificates.md",
    "Stage-Construction-07-Site-Operations.md",
    "Stage-Construction-08-Quality-and-Document-Control.md",
    "Stage-Construction-09-Variations-Claims-and-Delays.md",
    "Stage-Construction-10-Progress-and-Cash-Flow.md",
    "Stage-Construction-11-Permissions-and-Isolation.md",
    "Stage-Construction-12-Business-Events.md",
    "Stage-Construction-13-Reporting-Requirements.md",
    "Stage-Construction-14-Mobile-and-Site-UX-Contract.md",
    "Stage-Construction-15-Risk-Register.md",
    "Stage-Construction-16-Implementation-Roadmap.md",
    "Stage-Construction-17-Screen-Inventory.md",
    "Stage-Construction-18-Decision-Register.md",
    "Stage-Construction-Final-Delivery-Report.md",
    "construction-capability-matrix.csv",
    "construction-entity-catalog.csv",
    "construction-screen-catalog.csv",
    "construction-event-catalog.csv",
    "construction-risk-register.csv",
    "construction-roadmap.csv",
    "construction-decision-register.csv",
    "_generator/generate_construction_catalogs.py",
    "_generator/verify_deliverables.py",
    "_generator/archive_and_restore_test.py",
    "Stage-Construction-C1-Commercial-Foundation-Plan.md",
    "Stage-Construction-CR01-BOQ-Identity-Evidence.md",
    "Stage-Construction-CR02-Subcontract-Cap-Evidence.md",
    "Stage-Construction-CR03-Commercial-Revision-Evidence.md",
    "Stage-Construction-Contract-Mapping-Measurement.md",
    "Stage-Construction-Concurrency-and-Audit-Design.md",
    "Stage-Construction-C1-Delivery-Report.md",
    "Stage-Construction-C1-Preservation-Report.md",
    "Stage-Construction-C1-Disk-State-Manifest.csv",
    "mutation-proof-results.json",
    "_generator/run_mutation_proofs.py",
    "_generator/generate_c1_manifest.py",
]

# A citation looks like `CrossBuy/BL/Foo.cs:123`, `BL/Foo.cs:123` or `deploy/sql/boq.sql` — inside
# backticks. Paths are written either repository-relative or project-relative (relative to CrossBuy/),
# because that is how they are referenced in the codebase's own comments; both forms are accepted and
# each is resolved against both bases.
ROOTS = ("CrossBuy/", "CrossBuy.Tests/", "deploy/", "docs/", "crossbuy_mobile/", "engineering/",
         "BL/", "Models/", "Controllers/", "Views/", "Hubs/", "Resources/", "Infrastructure/")
BASES = ("", "CrossBuy")          # resolve against repo root and against the project directory
CITATION = re.compile(r"`([A-Za-z0-9_./-]+\.(?:cs|sql|md|cshtml|csproj|dart|py|resx|json))(?::[0-9,\-]+)?`")


def resolves(rel):
    return any(os.path.exists(os.path.join(ROOT, base, rel)) for base in BASES)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    failures = []

    # ---- 1. deliverables exist and are non-empty -------------------------------------------
    print("== 1. deliverable existence ==")
    manifest = []
    for name in DELIVERABLES:
        path = os.path.join(OUT, name)
        if not os.path.isfile(path):
            failures.append(f"MISSING deliverable: {name}")
            print(f"  MISSING  {name}")
            continue
        size = os.path.getsize(path)
        if size == 0:
            failures.append(f"EMPTY deliverable: {name}")
            print(f"  EMPTY    {name}")
            continue
        digest = sha256(path)
        manifest.append((name, size, digest))
        print(f"  ok {size:>8}  {digest[:16]}  {name}")

    # ---- 2. cited repository paths resolve --------------------------------------------------
    print("\n== 2. cited source paths ==")
    cited = {}
    for name, _, _ in manifest:
        if not name.endswith(".md"):
            continue
        with open(os.path.join(OUT, name), encoding="utf-8") as f:
            text = f.read()
        for m in CITATION.finditer(text):
            p = m.group(1)
            if p.startswith(ROOTS):
                cited.setdefault(p, set()).add(name)

    missing_paths = []
    for p in sorted(cited):
        if not resolves(p):
            missing_paths.append(p)
    print(f"  distinct cited paths: {len(cited)}")
    if missing_paths:
        for p in missing_paths:
            print(f"  UNRESOLVED  {p}   (cited in: {', '.join(sorted(cited[p]))})")
            failures.append(f"UNRESOLVED cited path: {p}")
    else:
        print("  all cited paths resolve on disk")

    # ---- 3. manifest ------------------------------------------------------------------------
    manifest_path = os.path.join(OUT, "MANIFEST-SHA256.txt")
    with open(manifest_path, "w", encoding="utf-8") as f:
        f.write("# CrossBusiness Construction & Contracting - FOURTH TAB deliverable manifest\n")
        f.write("# sha256  bytes  file\n")
        for name, size, digest in manifest:
            f.write(f"{digest}  {size}  {name}\n")
    print(f"\n== 3. manifest ==\n  wrote MANIFEST-SHA256.txt ({len(manifest)} entries)")

    # ---- 4. result --------------------------------------------------------------------------
    print("\n== RESULT ==")
    if failures:
        print(f"  FAILED - {len(failures)} problem(s):")
        for x in failures:
            print(f"    - {x}")
        return 1
    print(f"  PASSED - {len(manifest)} deliverables, {len(cited)} cited paths all resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())