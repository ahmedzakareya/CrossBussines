#!/usr/bin/env python3
# ==============================================================================================
# Tasks & Calendar Integration — DISK-STATE MANIFEST + PRESERVATION  (TAB 4)
#
#   generate    -> Stage-Tasks-Calendar-Integration-Disk-State-Manifest.csv  (SHA-256 per file)
#   archive     -> _archive/<name>.zip + ARCHIVE-SHA256.txt (written OUTSIDE the archive)
#   restore     -> extract into a disposable temp dir, compare every hash, report mismatches
#
# The archive contains SOURCE, TESTS, CONTRACTS, DOCS and the MANIFEST only. It contains no build
# output, no bin/obj, and no secrets — asserted, not assumed (see forbid_build_output()).
#
# Run from the repository root:
#   python docs/tasks-calendar-integration/_generator/generate_tci_manifest.py
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
ARCHIVE_NAME = "Stage-Tasks-Calendar-Integration-2026-08-06.zip"
MANIFEST_CSV = os.path.join(DOCS, "Stage-Tasks-Calendar-Integration-Disk-State-Manifest.csv")

# (path, change, role, purpose)
FILES = [
    # ---- production source (created) ----
    ("CrossBuy/BL/TasksCalendar/TaskCalendarTime.cs", "CREATED", "production-source",
     "Shared time model: UTC storage, explicit offsets, all-day as a local date, explicit tz failure"),
    ("CrossBuy/BL/TasksCalendar/TaskCalendarIntegrationContracts.cs", "CREATED", "production-source",
     "Registry onboarding requests (Phases 3-4) + 18 versioned business-event contracts (Phases 5-6)"),
    ("CrossBuy/BL/TasksCalendar/TaskNotificationService.cs", "CREATED", "production-source",
     "Direct Task notifications over the existing INotificationService, idempotent per occurrence"),
    ("CrossBuy/BL/TasksCalendar/TaskOverdueSweepService.cs", "CREATED", "production-source",
     "Overdue detection with ONE owner; hosted by the existing TM-7 worker, no third worker"),
    ("CrossBuy/BL/TasksCalendar/WorkspaceAgendaService.cs", "CREATED", "production-source",
     "Read-only Tasks+Calendar agenda union; no CalendarEvent materialisation, no DbContext"),

    # ---- production source (modified) ----
    ("CrossBuy/BL/TaskGeneratorHostedService.cs", "MODIFIED", "production-source",
     "Hosts the overdue sweep inside its existing per-company scope, in its own try/catch"),
    ("CrossBuy/Program.cs", "MODIFIED", "production-source-shared",
     "3 AddScoped registrations for the TasksCalendar services (additive)"),

    # ---- tests ----
    ("CrossBuy.Tests/TasksCalendarIntegrationTests.cs", "CREATED", "test",
     "17 tests: notifications (11) + overdue sweep (6)"),
    ("CrossBuy.Tests/TasksCalendarTimeAndContractTests.cs", "CREATED", "test",
     "24 tests: time model (13) + registry/event contracts (11)"),
    ("CrossBuy.Tests/TasksCalendarAgendaTests.cs", "CREATED", "test",
     "16 tests: workspace agenda union, redaction, ordering, paging"),

    # ---- documentation ----
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-01-Current-State.md", "CREATED", "document", "Current state and what was not touched"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-02-Notification-Design.md", "CREATED", "document", "Notification design, idempotency, authorization"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-03-Registry-Onboarding.md", "CREATED", "document", "Task + CalendarEvent onboarding contracts"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-04-Business-Events.md", "CREATED", "document", "18 versioned event contracts and their classification"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-05-Time-Model.md", "CREATED", "document", "The twelve time rules and the legacy remediation order"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-06-Workspace-Agenda.md", "CREATED", "document", "Agenda contract and the TAB 3 handover"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-07-Test-Evidence.md", "CREATED", "document", "57 tests mapped to the Phase 10 requirements"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-08-Open-Decisions.md", "CREATED", "document", "7 explicit open decisions"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-Delivery-Report.md", "CREATED", "document", "Delivery report and completion gate"),
    ("docs/tasks-calendar-integration/Stage-Tasks-Calendar-Integration-Preservation-Report.md", "CREATED", "document", "Hashes, archive, restore verification"),
    ("docs/tasks-calendar-integration/_generator/generate_tci_manifest.py", "CREATED", "tooling", "This script"),
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
    """A preservation archive that quietly carried a compiled assembly would be neither reviewable nor
    portable. This refuses rather than filters, so a bad entry is a failure and not a silent omission."""
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
        # the manifest travels WITH the payload, so a restored copy can verify itself
        z.write(MANIFEST_CSV, os.path.relpath(MANIFEST_CSV, ROOT).replace(os.sep, "/"))

    digest, size = sha256(path), os.path.getsize(path)
    # The archive's own hash is stored OUTSIDE the archive: putting it inside would change the hash it
    # records. _archive/ is never itself archived, for the same reason.
    with open(os.path.join(ARCHIVE_DIR, "ARCHIVE-SHA256.txt"), "w", encoding="utf-8") as f:
        f.write("# Tasks & Calendar Integration - TAB 4 preservation archive\n")
        f.write(f"{digest}  {size}  {ARCHIVE_NAME}  ({len(members) + 1} files)\n")

    print("== archive ==")
    print(f"  {ARCHIVE_NAME}  files={len(members) + 1}  bytes={size}")
    print(f"  sha256={digest}  (recorded in _archive/ARCHIVE-SHA256.txt)")

    # ---- restore into a disposable location and compare every hash ----
    tmp = tempfile.mkdtemp(prefix="cb_tci_restore_")
    mismatches = []
    try:
        with zipfile.ZipFile(path) as z:
            bad = z.testzip()
            if bad:
                mismatches.append(f"corrupt entry: {bad}")
            z.extractall(tmp)

        for r in rows:
            if r[6] == "MISSING":
                continue
            restored = os.path.join(tmp, r[0].replace("/", os.sep))
            if not os.path.isfile(restored):
                mismatches.append(f"not restored: {r[0]}")
            elif sha256(restored) != r[6]:
                mismatches.append(f"hash mismatch: {r[0]}")

        print("\n== restore ==")
        print(f"  extracted to a disposable directory: {len(members) + 1} files")
        print(f"  compared {len([r for r in rows if r[6] != 'MISSING'])} manifest hashes")
        print(f"  mismatches: {len(mismatches)}")
        for m in mismatches:
            print(f"    - {m}")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        print("  disposable directory removed")

    return mismatches


def main():
    rows, missing = build_manifest()
    created = sum(1 for r in rows if r[1] == "CREATED")
    modified = sum(1 for r in rows if r[1] == "MODIFIED")

    print("== manifest ==")
    print(f"  {os.path.relpath(MANIFEST_CSV, ROOT)}")
    print(f"  files={len(rows)}  created={created}  modified={modified}  bytes={sum(r[4] for r in rows)}")
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
