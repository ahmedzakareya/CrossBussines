# Stage-Construction-C1 — Preservation Report

**Increment:** C1 commercial foundation · **Tab:** FOURTH · **Date:** 2026-08-06

Everything here is produced by scripts that can be re-run at any time; nothing in this report is transcribed by hand
from a console. The exact figures for the current tree are in the files named below, which is deliberate — a hash
copied into prose goes stale the moment the file it describes is re-saved.

---

## 1. What is preserved, and where

| Artefact | File | Produced by |
|---|---|---|
| Deliverable hashes (docs, catalogs, tooling) | `MANIFEST-SHA256.txt` | `_generator/verify_deliverables.py` |
| **C1 disk-state** — every source, SQL, test and document file this increment created or modified, with SHA-256, bytes and line count | `Stage-Construction-C1-Disk-State-Manifest.csv` | `_generator/generate_c1_manifest.py` |
| Raw mutation-proof results | `mutation-proof-results.json` | `_generator/run_mutation_proofs.py` |
| Preservation archive | `_archive/Stage-Construction-Deliverables-2026-08-05.zip` | `_generator/archive_and_restore_test.py` |
| The archive's own hash | `_archive/ARCHIVE-SHA256.txt` | same |

The archive's SHA-256 is recorded **outside** the archived tree on purpose: embedding it in a document that is itself
archived is circular — archiving the file that records the hash changes the hash. `_archive/` is excluded from the
archived tree for the same reason.

## 2. The two manifests, and why there are two

They answer different questions and must not be merged.

- **`MANIFEST-SHA256.txt`** — *"is every deliverable present and unchanged?"* It covers `docs/construction` only, and
  is what the archive/restore test compares against.
- **`Stage-Construction-C1-Disk-State-Manifest.csv`** — *"what did this increment do to the repository?"* It covers
  the production source, the deployment SQL and the tests as well as the documents, and it records `CREATED` vs
  `MODIFIED` per file. A reviewer who wants to know the blast radius reads this one.

The disk-state manifest reports **MISSING** for any listed file that is not on disk and exits non-zero. It cannot
quietly omit a file it expected.

## 3. Restore verification

`python docs/construction/_generator/archive_and_restore_test.py` performs a real restore, not a listing:

1. builds the archive from the live tree (excluding `_archive/`);
2. runs `ZipFile.testzip()` — every entry's CRC is checked;
3. extracts into a **fresh temporary directory**;
4. re-hashes every extracted file and compares against `MANIFEST-SHA256.txt`;
5. compares every extracted file against the **live** file byte-for-byte;
6. deletes the temporary directory in a `finally`.

**Required outcome: 0 mismatches on both comparisons.** The script exits non-zero on any mismatch, any missing
manifest entry, any unexpected archive member, or any corrupt entry. The run recorded for this increment passed all
of it; re-running reproduces the result and rewrites `_archive/ARCHIVE-SHA256.txt`.

## 4. Scratch cleanup

| Scratch | Created by | Removed |
|---|---|---|
| Mutation backups (`cb_c1_mutation_backup_*`) | `run_mutation_proofs.py` | in a `finally`, whether the proofs pass or fail |
| Restore directory (`cb_construction_restore_*`) | `archive_and_restore_test.py` | in a `finally` |
| SQLite test databases | `ConstructionTestFixture` | in-memory only; released with the connection on `Dispose` |

Nothing is written to `/tmp` or to the user's project outside `docs/construction`. No scratch file survives a run.

**The mutation backups are the safety net that matters.** Each mutated file is copied byte-exactly *before* the
mutation, restored from that copy, and the SHA-256 is compared before and after. If a restore had not matched, the
script would have reported `NOT PROVEN` for that mutation and said so in the results JSON rather than continuing.

## 5. Reproducing every artefact

```
# catalogs + the six generated design documents
python docs/construction/_generator/generate_construction_catalogs.py

# C1 disk-state manifest (exits non-zero if any listed file is missing)
python docs/construction/_generator/generate_c1_manifest.py

# deliverable existence, cited-path resolution, MANIFEST-SHA256.txt
python docs/construction/_generator/verify_deliverables.py

# mutation proofs C-01/C-02/C-03 + marker sweep  (rebuilds and runs tests; several minutes)
python docs/construction/_generator/run_mutation_proofs.py

# archive + restore verification
python docs/construction/_generator/archive_and_restore_test.py
```

The catalog generator is **idempotent**: a re-run produces byte-identical output, which was verified by hashing the
generated documents before and after a re-run.

## 6. Integrity limits — what preservation does and does not prove

Stated so the evidence is not read as more than it is:

- The archive preserves **`docs/construction`**. The C1 **source and SQL** are preserved by *hash* in the disk-state
  manifest, not by copy — they live in git, and duplicating them into a zip would create a second copy that can
  drift from the tree.
- A hash proves a file is unchanged since the manifest was written. It proves nothing about whether the file is
  *correct* — that is what the 38 tests and the three mutation proofs are for.
- The restore test proves the archive can be restored on **this** machine with **this** Python. It is not a
  cross-platform guarantee; the archive is a plain deflate zip with no external dependency, which is the closest to
  one that is honestly available.
