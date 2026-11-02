# R1 — restore and authored-slice authority

Status: authored locally under the coordinator's delegated R1 scope; not promoted to another environment. This document records the operating choice, not proof that existing environments satisfy it.

## Authority

`CrossBuy/deploy/sql` is the authoritative source for versioned schema changes. Repository-level `deploy/sql` contains generated compatibility packaging and explicitly retained legacy exceptions. Apply a canonical slice once; never apply its package copy as another change.

A backup/restore is a provisioning mechanism, not a replacement for authored-slice governance. An existing `CrossBuyDB2` image can initialize a new environment only with a verified backup, a recorded source identity and reconciliation of the restored image against the authored manifest. Restoring a database does not prove that every current source script ran.

## Admission after restore

1. Verify the backup and record the actual source environment and application version.
2. Confirm `PlatformSchemaHistory` exists before recording or applying subsequent slices. Preserve any restored history; do not replace it.
3. Compare exact script paths and raw file hashes with recorded history. Inspect schema and retained execution evidence for gaps. Presence of a table alone does not prove an entire slice or its backfill was applied.
4. Record a baseline only for individually verified identities. Preserve the evidence and make the baseline operation explicit. Do not bulk-stamp pending scripts or substitute a canonical path for a differently hashed external execution.
5. Apply only reviewed, release-accepted canonical slices in dependency order. Excluded and unqualified scripts remain blocked. A changed applied identity requires a reviewed successor or restoration of the approved source; never silently reapply it.
6. Re-run reconciliation before promotion. Missing evidence, failed applications, changed identities and unmet dependencies prevent promotion.

## Local evidence and limits — 2026-10-13

Read-only reconciliation of the linked local `CrossBuyDB2` reported 3 canonical identities applied, 146 without successful canonical execution records, zero changed canonical records and zero failed canonical records. Six external logical history paths have retained files whose exact raw hashes match their successful records. Those six are preserved execution evidence, not automatically applied canonical identities.

The pending count is a history gap, not a claim that 146 schemas are absent. No automatic baseline stamping or restoration was performed for this decision.

Accounting and HR successor deployment contracts passed 33 isolated real SQL Server cases after the owner authorized a separate test database. The cases cover fresh/input-first execution, reruns, evidence preservation, refusal and concurrency. Temporary test databases were deleted and cleanup verified; evidence is recorded in `governance/evidence/r1-accounting-hr-sql-matrix.json`. This is not linked-database activation or qualification of the whole SQL integration suite. The root scratch script remains excluded. Test, prelive and production promotion is not proven by these local isolated checks.
