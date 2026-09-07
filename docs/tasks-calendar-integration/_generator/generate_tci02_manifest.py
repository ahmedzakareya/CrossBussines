#!/usr/bin/env python3
# ==============================================================================================
# Tasks & Calendar Integration 02 — DISK-STATE MANIFEST + PRESERVATION  (TAB 4)
#
#   manifest -> Stage-Tasks-Calendar-02-Disk-State-Manifest.csv   (SHA-256 per file)
#   archive  -> _archive/Stage-Tasks-Calendar-02-<date>.zip + ARCHIVE-SHA256.txt (written OUTSIDE it)
#   restore  -> extract to a disposable dir, re-hash everything, report mismatches
#
# Source, tests, contracts, docs and the manifest only. No build output, no secrets — refused, not
# filtered. Previous archives are left untouched.
#
# Run:  python docs/tasks-calendar-integration/_generator/generate_tci02_manifest.py
# ==============================================================================================
import csv
import hashlib
import os
import shutil
import sys
import tempfile
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
DOCS = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE_DIR = os.path.join(DOCS, "_archive")
ARCHIVE_NAME = "Stage-Tasks-Calendar-02-2026-08-06.zip"
MANIFEST_CSV = os.path.join(DOCS, "Stage-Tasks-Calendar-02-Disk-State-Manifest.csv")

FILES = [
    # ---- production source, created this increment ----
    ("CrossBuy/BL/TasksCalendar/TaskCalendarEventPublisher.cs", "CREATED", "production-source",
     "Publishes the Task/Calendar contracts as kernel events INSIDE the caller's transaction"),

    # ---- production source, modified this increment ----
    ("CrossBuy/BL/TaskService.cs", "MODIFIED", "production-source",
     "Transition wiring: events in-transaction, notifications post-commit, nothing on a refused write"),
    ("CrossBuy/BL/Platform/EntityRegistry.cs", "MODIFIED", "production-source-kernel",
     "Task + CalendarEvent registered (additive): 2 codes, 2 definitions, 2 search arms, 2 resolve arms"),
    ("CrossBuy/BL/TasksCalendar/TaskCalendarIntegrationContracts.cs", "MODIFIED", "production-source",
     "3 event names renamed to satisfy <EntityCode>.<Action> kernel validation"),
    ("CrossBuy/Program.cs", "MODIFIED", "production-source-shared",
     "+1 AddScoped for the event publisher"),

    # ---- production source carried from increment 01 (unchanged, hashed for completeness) ----
    ("CrossBuy/BL/TasksCalendar/TaskCalendarTime.cs", "UNCHANGED", "production-source",
     "Shared time model — UTC, explicit offsets, all-day as a local date"),
    ("CrossBuy/BL/TasksCalendar/TaskNotificationService.cs", "UNCHANGED", "production-source",
     "Direct Task notifications, idempotent per occurrence"),
    ("CrossBuy/BL/TasksCalendar/TaskOverdueSweepService.cs", "UNCHANGED", "production-source",
     "Overdue detection with one owner (TM-7 worker)"),
    ("CrossBuy/BL/TasksCalendar/WorkspaceAgendaService.cs", "UNCHANGED", "production-source",
     "Read-only Tasks+Calendar agenda union"),
    ("CrossBuy/BL/TaskGeneratorHostedService.cs", "UNCHANGED", "production-source",
     "Hosts the overdue sweep; no third worker"),

    # ---- tests ----
    ("CrossBuy.Tests/TasksCalendarTransitionAndEventTests.cs", "CREATED", "test",
     "17 tests: transitions, registration, transactional publishing, rollback"),
    ("CrossBuy.Tests/TasksCalendarIntegrationTests.cs", "MODIFIED", "test",
     "Employee seed now populates all ten NOT NULL columns"),
    ("CrossBuy.Tests/TasksCalendarTimeAndContractTests.cs", "MODIFIED", "test",
     "Required-set assertion moved with the renamed events"),
    ("CrossBuy.Tests/TasksCalendarAgendaTests.cs", "UNCHANGED", "test",
     "16 agenda tests"),

    # ---- documentation ----
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Notification-Wiring.md", "CREATED", "document", "Phase 1"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Registry-Onboarding.md", "CREATED", "document", "Phase 2"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Business-Event-Evidence.md", "CREATED", "document", "Phase 3"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Agenda-Evidence.md", "CREATED", "document", "Phase 4"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Time-Evidence.md", "CREATED", "document", "Phase 5"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Test-Evidence.md", "CREATED", "document", "Phase 6 + verification"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Delivery-Report.md", "CREATED", "document", "Delivery report and gate"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-02-Preservation-Report.md", "CREATED", "document", "Preservation"),
    ("docs/tasks-calendar-integration/_generator/generate_tci02_manifest.py", "CREATED", "tooling", "This script"),
]

FIELDS = ["Path", "Change", "Role", "Purpose", "Bytes", "Lines", "SHA256"]
FORBIDDEN = ("/bin/", "/obj/", "\\bin\\", "\\obj\\", ".dll", ".pdb", ".exe", ".user")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def forbid_build_output(paths):
    bad = [p for p in paths if any(tok in p for tok in FORBIDDEN)]
    if bad:
        raise RuntimeError(f"refusing to archive build output or binaries: {bad}")


def build_manifest():
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
                     data.count(b"\n") + (0 if not data or data.endswith(b"\n") else 1),
                     sha256(full)])
    with open(MANIFEST_CSV, "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f)
        w.writerow(FIELDS)
        w.writerows(rows)
    return rows, missing


def archive_and_restore(rows):
    forbid_build_output([r[0] for r in rows])
    os.makedirs(ARCHIVE_DIR, exist_ok=True)
    path = os.path.join(ARCHIVE_DIR, ARCHIVE_NAME)
    if os.path.exists(path):
        os.remove(path)

    members = [r[0] for r in rows if r[6] != "MISSING"]
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for rel in members:
            z.write(os.path.join(ROOT, rel), rel)
        z.write(MANIFEST_CSV, os.path.relpath(MANIFEST_CSV, ROOT).replace(os.sep, "/"))

    digest, size = sha256(path), os.path.getsize(path)
    # Outside the archive on purpose: putting it inside would change the hash it records.
    with open(os.path.join(ARCHIVE_DIR, "ARCHIVE-SHA256.txt"), "a", encoding="utf-8") as f:
        f.write(f"{digest}  {size}  {ARCHIVE_NAME}  ({len(members) + 1} files)\n")

    print("== archive ==")
    print(f"  {ARCHIVE_NAME}  files={len(members) + 1}  bytes={size}")
    print(f"  sha256={digest}")

    tmp = tempfile.mkdtemp(prefix="cb_tci02_restore_")
    mismatches = []
    try:
        with zipfile.ZipFile(path) as z:
            bad = z.testzip()
            if bad:
                mismatches.append(f"corrupt entry: {bad}")
            z.extractall(tmp)
        checked = 0
        for r in rows:
            if r[6] == "MISSING":
                continue
            restored = os.path.join(tmp, r[0].replace("/", os.sep))
            if not os.path.isfile(restored):
                mismatches.append(f"not restored: {r[0]}")
            elif sha256(restored) != r[6]:
                mismatches.append(f"hash mismatch: {r[0]}")
            else:
                checked += 1
        print("\n== restore ==")
        print(f"  extracted to a disposable directory: {len(members) + 1} files")
        print(f"  verified {checked} hashes")
        print(f"  mismatches: {len(mismatches)}")
        for m in mismatches:
            print(f"    - {m}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        print("  disposable directory removed")
    return mismatches


def main():
    rows, missing = build_manifest()
    counts = {}
    for r in rows:
        counts[r[1]] = counts.get(r[1], 0) + 1

    print("== manifest ==")
    print(f"  {os.path.relpath(MANIFEST_CSV, ROOT)}")
    print(f"  files={len(rows)}  " + "  ".join(f"{k.lower()}={v}" for k, v in sorted(counts.items())))
    print(f"  bytes={sum(r[4] for r in rows)}")
    if missing:
        print(f"  MISSING: {missing}")
        return 1
    print("  every listed file exists on disk")

    mismatches = archive_and_restore(rows)
    print("\n== RESULT ==")
    if mismatches:
        print(f"  FAILED - {len(mismatches)} mismatch(es)")
        return 1
    print("  PASSED - manifest complete, archive restored with 0 mismatches")
    return 0


if __name__ == "__main__":
    sys.exit(main())
