# Stage-Tasks-Calendar-02 — Preservation Report

**Tab:** TAB 4 · **Increment:** Tasks and Calendar integration 02 · **Date:** 2026-08-06

---

## 1. What is preserved

| Artefact | File |
|---|---|
| Disk-state manifest (SHA-256, bytes, lines, role per file) | `Stage-Tasks-Calendar-02-Disk-State-Manifest.csv` |
| Preservation archive | `_archive/Stage-Tasks-Calendar-02-2026-08-06.zip` |
| Archive hashes (this and the previous increment) | `_archive/ARCHIVE-SHA256.txt` |

All three are produced by `_generator/generate_tci02_manifest.py` in one run, so the manifest and the archive cannot
describe different states.

## 2. Contents

**Included:** the file created this increment, the five production files modified, the five carried unchanged (hashed
so the whole TAB 4 surface is covered, not just the delta), the four test files, the eight documents, the manifest
and the generator.

**Excluded, and refused rather than filtered:** build output, `bin/`, `obj/`, `.dll`, `.pdb`, `.exe`, `.user`.
`forbid_build_output()` raises if any listed path matches — a bad entry is a failure, not a silent omission. Nothing
in the archive contains a connection string, token or credential.

## 3. Previous archives preserved

The increment-01 archive `Stage-Tasks-Calendar-Integration-2026-08-06.zip` is **untouched**, and its hash line
remains in `ARCHIVE-SHA256.txt` — the new line is **appended**, not overwritten. The construction-track archives are
likewise untouched.

## 4. The archive hash is stored outside the archive

Putting it inside would change the hash it records. `_archive/` is never itself archived. The **manifest**, by
contrast, travels *with* the payload so a restored copy can verify itself without the original tree.

## 5. Restore verification

A real restore, not a listing:

1. build the archive from the live tree;
2. `ZipFile.testzip()` — every entry's CRC checked;
3. extract into a **disposable temporary directory**;
4. re-hash every restored file and compare against the manifest;
5. report the mismatch count and name every mismatch;
6. remove the temporary directory in a `finally`.

**Required outcome: 0 mismatches.** The script exits non-zero on any mismatch, missing entry or corrupt entry.
Result for this increment: **0 mismatches**.

## 6. Scratch cleanup

| Scratch | Removed |
|---|---|
| restore directory (`cb_tci02_restore_*`) | in a `finally`, pass or fail |
| SQLite test databases | in-memory only; released with the connection on `Dispose` |

**No scratch database was created and no SQL was executed against CrossBuyDB2 or any other database** at any point in
this increment.

## 7. Reproducing it

```
python docs/tasks-calendar-integration/_generator/generate_tci02_manifest.py
```

Regenerates the manifest, rebuilds the archive, re-runs the restore comparison and appends the archive hash.

## 8. What preservation proves, and what it does not

- A hash proves a file is unchanged since the manifest was written. It says nothing about whether the file is
  **correct** — that is what the 74 focused tests and the 1472-test suite are for.
- The restore proves the archive restores on this machine with this Python. It is a plain deflate zip with no
  external dependency, which is the closest to a portability guarantee honestly available.
- The manifest covers the TAB 4 surface. It is not a snapshot of the repository and does not claim to be.
