#!/usr/bin/env python3
# ==============================================================================================
# Tasks & Calendar Integration (R1-R5) — deliverable verification.
#
#  1. every deliverable exists and is non-empty
#  2. every repository path cited in the documents resolves on disk
#  3. SHA-256 manifest written
#
# Writes only inside docs/tasks-calendar. Exit 0 = all checks pass.
# Run:  python docs/tasks-calendar/_generator/verify_tc_deliverables.py
# ==============================================================================================
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.dirname(HERE)
ROOT = os.path.dirname(os.path.dirname(OUT))

DELIVERABLES = [
    "Stage-TC-R1-Existing-Implementation-Assessment.md",
    "Stage-TC-R2-Ownership-Reconciliation.md",
    "Stage-TC-R3-Event-Model-and-Notification-Flow.md",
    "Stage-TC-R4-Timeline-Workspace-Communication-Integration.md",
    "Stage-TC-R5-Scheduling-and-Calendar-Synchronization-Preparation.md",
    "Stage-TC-Catalogs-Appendix.md",
    "tasks-calendar-ownership-matrix.csv",
    "tasks-calendar-gap-catalog.csv",
    "tasks-calendar-event-catalog.csv",
    "tasks-calendar-decision-register.csv",
    "_generator/generate_tc_catalogs.py",
    "_generator/verify_tc_deliverables.py",
]

ROOTS = ("CrossBuy/", "CrossBuy.Tests/", "deploy/", "docs/", "crossbuy_mobile/",
         "BL/", "Models/", "Controllers/", "Views/", "Hubs/", "Resources/")
BASES = ("", "CrossBuy")
CITATION = re.compile(r"`([A-Za-z0-9_./-]+\.(?:cs|sql|md|cshtml|csproj|dart|py|resx|json))(?::[0-9,\-]+)?`")


def resolves(rel):
    return any(os.path.exists(os.path.join(ROOT, b, rel)) for b in BASES)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    failures, manifest = [], []

    print("== 1. deliverable existence ==")
    for name in DELIVERABLES:
        p = os.path.join(OUT, name)
        if not os.path.isfile(p):
            failures.append(f"MISSING {name}")
            print(f"  MISSING  {name}")
            continue
        size = os.path.getsize(p)
        if size == 0:
            failures.append(f"EMPTY {name}")
            print(f"  EMPTY    {name}")
            continue
        d = sha256(p)
        manifest.append((name, size, d))
        print(f"  ok {size:>7}  {d[:16]}  {name}")

    print("\n== 2. cited source paths ==")
    cited = {}
    for name, _, _ in manifest:
        if not name.endswith(".md"):
            continue
        with open(os.path.join(OUT, name), encoding="utf-8") as f:
            for m in CITATION.finditer(f.read()):
                p = m.group(1)
                if p.startswith(ROOTS):
                    cited.setdefault(p, set()).add(name)

    unresolved = [p for p in sorted(cited) if not resolves(p)]
    print(f"  distinct cited paths: {len(cited)}")
    for p in unresolved:
        print(f"  UNRESOLVED  {p}  (cited in: {', '.join(sorted(cited[p]))})")
        failures.append(f"UNRESOLVED {p}")
    if not unresolved:
        print("  all cited paths resolve on disk")

    mpath = os.path.join(OUT, "MANIFEST-SHA256.txt")
    with open(mpath, "w", encoding="utf-8") as f:
        f.write("# Tasks & Calendar Integration (R1-R5) - TAB 4 deliverable manifest\n")
        f.write("# sha256  bytes  file\n")
        for name, size, d in manifest:
            f.write(f"{d}  {size}  {name}\n")
    print(f"\n== 3. manifest ==\n  wrote MANIFEST-SHA256.txt ({len(manifest)} entries)")

    print("\n== RESULT ==")
    if failures:
        print(f"  FAILED - {len(failures)} problem(s)")
        for x in failures:
            print(f"    - {x}")
        return 1
    print(f"  PASSED - {len(manifest)} deliverables, {len(cited)} cited paths all resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main())
