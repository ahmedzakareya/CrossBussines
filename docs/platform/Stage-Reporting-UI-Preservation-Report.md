# Stage Reporting UI — Preservation Report (R3)

**Archive:** `engineering/preservation/Stage-Reporting-UI-20260806.zip`
**Digest:** `engineering/preservation/Stage-Reporting-UI-20260806.zip.sha256` (sidecar)
**Manifest:** `docs/platform/Stage-Reporting-UI-Disk-State-Manifest.csv`

---

## 1. What is preserved

Every file this increment created or modified and still on disk: **26 tracked files** — 19 source and
resource files plus the seven prose documents. The manifest is added as a 27th archive member, so **27
members** in total.

Five files are absent because they were **deleted** when the Inventory visual rule superseded the bespoke
design language — `_LayoutReporting.cshtml`, `crossbusiness-reporting.css` and the layout's three resx files.
They are not preserved: preserving a deleted second design language would defeat the point of deleting it.

The manifest records, per file: repo-relative path · `new` or `modified` · the phase it belongs to · byte
length · **SHA-256** · a one-line role.

---

## 2. Why the digest is a sidecar and not a line in this document

Because stamping the archive's own hash into a document *inside* the archive changes the archive, which changes
the hash. That circularity was found and fixed during the previous Reporting increment; the fix is kept here
rather than rediscovered.

So: `Stage-Reporting-UI-20260806.zip.sha256` sits beside the archive and is not a member of it.

The **manifest** is a member, and that is deliberate — it is per-file hashes, none of which is the archive's
own, so there is no circularity and a restored copy carries its own verification data.

---

## 3. Verification performed

Not "the zip was written and looked about the right size". Every member was **read back out of the archive**
and its SHA-256 recomputed and compared against the manifest entry:

```
manifest:   26 files
archive:    27 members   (the 26, plus the manifest itself)
mismatches:  0
```

A member absent from the archive counts as a mismatch, so the check catches omission as well as corruption.

---

## 4. The parked file is preserved

`CrossBuy/Controllers/Api/ReportsCenterWriteEndpoints.cs.pending` is in the archive.

It is the one file in this increment that does not compile, and it is the one most at risk of being lost: it is
invisible to the build, invisible to a `*.cs` search, and it holds a phase of work that is finished but not
activated. Preserving it is most of the point of preserving anything here.

`ReportingWriteAuthorizationTests.The_write_surface_flag_and_the_endpoint_file_agree` fails if the file is
deleted without the endpoints being activated — so losing it breaks a test rather than passing silently.

---

## 5. Restoring

```
unzip Stage-Reporting-UI-20260806.zip -d <repo-root>
```

Paths inside the archive are repo-relative, so it restores in place. Then:

1. `dotnet build CrossBuy.sln -c Debug` → 0 errors
2. `dotnet test CrossBuy.Tests --filter "FullyQualifiedName~Reporting"` → 304 / 304

Three files are **modified**, not new — `BusinessEventsDataset.cs`, `ReportingRegistration.cs`,
`ReportingTestHost.cs`. Restoring over a tree where another tab has since edited them would overwrite that
work. Check the manifest's SHA-256 for those three before extracting over a live tree; the other 23 are new
files and safe.
