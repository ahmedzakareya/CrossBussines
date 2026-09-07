#!/usr/bin/env python3
# ==============================================================================================
# Preservation archive + restore verification for the FOURTH TAB construction deliverables.
#
#   1. zip every file under docs/construction (excluding _archive itself)
#   2. extract the zip into a fresh temporary directory
#   3. re-hash every extracted file and compare against MANIFEST-SHA256.txt
#   4. also compare the extracted tree against the live tree file-by-file
#
# Writes only inside docs/construction/_archive and a temp directory. Exit 0 = restore verified.
# ==============================================================================================
import hashlib
import os
import shutil
import sys
import tempfile
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.dirname(HERE)                       # docs/construction
ARCHIVE_DIR = os.path.join(OUT, "_archive")
ARCHIVE_NAME = "Stage-Construction-Deliverables-2026-08-05.zip"
ARCHIVE_PATH = os.path.join(ARCHIVE_DIR, ARCHIVE_NAME)
MANIFEST = os.path.join(OUT, "MANIFEST-SHA256.txt")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def live_files():
    """Every file under docs/construction except the archive folder, as posix-relative paths."""
    out = []
    for root, dirs, names in os.walk(OUT):
        dirs[:] = [d for d in dirs if os.path.join(root, d) != ARCHIVE_DIR]
        for n in names:
            full = os.path.join(root, n)
            out.append(os.path.relpath(full, OUT).replace(os.sep, "/"))
    return sorted(out)


def read_manifest():
    entries = {}
    with open(MANIFEST, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            digest, size, name = line.split("  ", 2)
            entries[name] = (digest, int(size))
    return entries


def main():
    failures = []

    # ---- 1. create the archive --------------------------------------------------------------
    os.makedirs(ARCHIVE_DIR, exist_ok=True)
    if os.path.exists(ARCHIVE_PATH):
        os.remove(ARCHIVE_PATH)
    files = live_files()
    with zipfile.ZipFile(ARCHIVE_PATH, "w", zipfile.ZIP_DEFLATED) as z:
        for rel in files:
            z.write(os.path.join(OUT, rel), rel)
    size = os.path.getsize(ARCHIVE_PATH)
    digest = sha256(ARCHIVE_PATH)
    print("== 1. archive ==")
    print(f"  {ARCHIVE_NAME}")
    print(f"  files: {len(files)}   bytes: {size}   sha256: {digest}")

    # The archive's own hash is recorded OUTSIDE the archived tree (_archive is excluded from the
    # walk above). Writing it into a deliverable would be circular: archiving the file that records
    # the archive's hash changes that hash.
    with open(os.path.join(ARCHIVE_DIR, "ARCHIVE-SHA256.txt"), "w", encoding="utf-8") as f:
        f.write("# Preservation archive of the FOURTH TAB construction deliverables\n")
        f.write(f"{digest}  {size}  {ARCHIVE_NAME}  ({len(files)} files)\n")

    # ---- 2. restore into a fresh temp dir ---------------------------------------------------
    tmp = tempfile.mkdtemp(prefix="cb_construction_restore_")
    try:
        with zipfile.ZipFile(ARCHIVE_PATH) as z:
            bad = z.testzip()
            if bad:
                failures.append(f"corrupt entry in archive: {bad}")
            z.extractall(tmp)
        restored = sorted(
            os.path.relpath(os.path.join(r, n), tmp).replace(os.sep, "/")
            for r, _, ns in os.walk(tmp) for n in ns
        )
        print("\n== 2. restore ==")
        print(f"  extracted to a temporary directory: {len(restored)} files")
        if restored != files:
            missing = set(files) - set(restored)
            extra = set(restored) - set(files)
            for m in sorted(missing):
                failures.append(f"not restored: {m}")
            for e in sorted(extra):
                failures.append(f"unexpected in archive: {e}")

        # ---- 3. compare restored hashes against the manifest --------------------------------
        manifest = read_manifest()
        print("\n== 3. restored files vs MANIFEST-SHA256.txt ==")
        checked = 0
        for name, (digest, msize) in sorted(manifest.items()):
            path = os.path.join(tmp, name)
            if not os.path.isfile(path):
                failures.append(f"manifest entry not present after restore: {name}")
                print(f"  MISSING   {name}")
                continue
            rdigest, rsize = sha256(path), os.path.getsize(path)
            if rdigest != digest or rsize != msize:
                failures.append(f"hash/size mismatch after restore: {name}")
                print(f"  MISMATCH  {name}")
                continue
            checked += 1
        print(f"  {checked}/{len(manifest)} manifest entries restored with identical sha256 and size")

        # ---- 4. compare restored tree against the live tree --------------------------------
        print("\n== 4. restored tree vs live tree ==")
        diffs = 0
        for rel in files:
            a, b = os.path.join(OUT, rel), os.path.join(tmp, rel)
            if not os.path.isfile(b) or sha256(a) != sha256(b):
                failures.append(f"restored copy differs from live file: {rel}")
                diffs += 1
        print(f"  {len(files) - diffs}/{len(files)} files byte-identical to the live tree")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
        print("\n  temporary restore directory removed")

    print("\n== RESULT ==")
    if failures:
        print(f"  RESTORE VERIFICATION FAILED - {len(failures)} problem(s):")
        for x in failures:
            print(f"    - {x}")
        return 1
    print("  RESTORE VERIFICATION PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
