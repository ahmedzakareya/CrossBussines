# Stage-Tasks-Calendar-Integration — Preservation Report

**Tab:** TAB 4 · **Increment:** Tasks & Calendar integration · **Date:** 2026-08-06

---

## 1. What is preserved

| Artefact | File |
|---|---|
| Disk-state manifest (SHA-256, bytes, lines, role per file) | `Stage-Tasks-Calendar-Integration-Disk-State-Manifest.csv` |
| Preservation archive | `_archive/Stage-Tasks-Calendar-Integration-2026-08-06.zip` |
| The archive's own hash | `_archive/ARCHIVE-SHA256.txt` |

Produced by `_generator/generate_tci_manifest.py`, which does all three in one run so the manifest and the archive
cannot describe different states.

## 2. Contents — and what is excluded

**Included:** the five created production source files, the two modified production files, the three test files, the
nine documents, the manifest, and the generator itself.

**Excluded, and refused rather than filtered:** build output, `bin/`, `obj/`, `.dll`, `.pdb`, `.exe`, `.user`.
`forbid_build_output()` raises if any listed path matches — a bad entry is a failure, not a silent omission. There
are no secrets: nothing in the archive contains a connection string, token or credential.

**Scope note.** This is a **new, tab-owned** archive. It does not replace or touch the earlier construction
archives, which remain where they were — the instruction to preserve previous archives is satisfied by leaving them
untouched, not by copying them.

## 3. The archive hash is stored outside the archive

Putting the hash inside would change the hash it records. `_archive/` is therefore never itself archived, and
`ARCHIVE-SHA256.txt` sits beside the zip rather than in it.

The **manifest**, by contrast, travels *with* the payload — so a restored copy can verify itself without the
original tree.

## 4. Restore verification

`generate_tci_manifest.py` performs a real restore, not a listing:

1. builds the archive from the live tree;
2. runs `ZipFile.testzip()` — every entry's CRC is checked;
3. extracts into a **disposable temporary directory**;
4. re-hashes every restored file and compares it against the manifest;
5. reports the mismatch count and names every mismatch;
6. removes the temporary directory in a `finally`.

**Required outcome: 0 mismatches.** The script exits non-zero on any mismatch, any missing manifest entry, or any
corrupt entry. The run for this increment reported **0 mismatches**; re-running reproduces the result and rewrites
`ARCHIVE-SHA256.txt`.

## 5. Scratch cleanup

| Scratch | Removed |
|---|---|
| restore directory (`cb_tci_restore_*`) | in a `finally`, whether the comparison passes or fails |
| SQLite test databases | in-memory only; released with the connection on `Dispose` |

Nothing is written outside `docs/tasks-calendar-integration` by this tooling. No scratch database was created, and
**no SQL was executed against CrossBuyDB2 or any other database** at any point in this increment.

## 6. Reproducing it

```
python docs/tasks-calendar-integration/_generator/generate_tci_manifest.py
```

Regenerates the manifest, rebuilds the archive, re-runs the restore comparison and rewrites the archive hash.

## 7. What preservation proves, and what it does not

- A hash proves a file is unchanged since the manifest was written. It says nothing about whether the file is
  **correct** — that is what the 57 tests are for.
- The restore test proves the archive restores on this machine with this Python. The archive is a plain deflate zip
  with no external dependency, which is the closest to a portability guarantee honestly available.
- The manifest covers the files this increment created or modified. It is not a snapshot of the whole repository,
  and it does not claim to be.
