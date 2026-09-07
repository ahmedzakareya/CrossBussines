# A0 — 12 · Preservation report

**Archive:** `engineering/preservation/Stage-Reporting-A0-20260809.zip`
**Digest:** the `.sha256` sidecar beside it
**Manifest:** `docs/platform/Stage-Reporting-A0-Disk-State-Manifest.csv`

---

## 1. What is preserved

Sixteen files: the Reporting DDL slice, the regenerated manifest, the three test files, and the eleven prose
deliverables. The CSV manifest is added as a seventeenth archive member.

Per file: repo-relative path · `new`/`modified` · kind · byte length · **SHA-256** · role.

## 2. The digest is a sidecar

Stamping the archive's own hash into a document *inside* the archive changes the archive, which changes the
hash. That circularity was found and fixed in an earlier increment; the fix is carried forward rather than
rediscovered. The per-file manifest is a member (none of its hashes is the archive's own, so no circularity).

## 3. Verification performed

Every member was **read back out of the archive** and its SHA-256 recomputed against the manifest entry. A
member absent from the archive counts as a mismatch, so omission is caught as well as corruption.

## 4. The slice is the point

`CrossBuy/deploy/sql/reporting_platform.sql` is the artifact this whole increment exists to make trustworthy.
Its hash appears in three places that must agree — the manifest, this preservation manifest, and the runbook's
apply instruction. `The_manifest_registers_the_slice_with_its_current_hash` fails if the first two drift.

## 5. Restoring

```
unzip Stage-Reporting-A0-20260809.zip -d <repo-root>
```

Paths are repo-relative. Then:

1. `dotnet build CrossBuy.sln -c Debug` → 0 `CS` errors
2. `dotnet test CrossBuy.Tests --filter "FullyQualifiedName~ReportingDeploymentGovernance"` → 7/7, no database needed
3. With `CROSSBUY_TEST_SQL` set: `--filter "FullyQualifiedName~ReportingSchemaDeployment"` → 13/13

**Three files are `modified`, not new** — `reporting_platform.sql`, `manifest.json`, `ReportingTestHost.cs`.
`manifest.json` in particular is shared: other tabs add scripts to it, so restoring an older copy would drop
their entries. Check the manifest's SHA-256 for those three before extracting over a live tree.
