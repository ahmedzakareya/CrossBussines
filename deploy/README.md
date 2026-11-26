# CrossBuy — Deployment package (prepared locally)

This folder is everything you carry to the server. The step-by-step server guide is
**docs/DEPLOYMENT.md** — read it top to bottom. This README only explains *what's here*
and the **database strategy decision**.

## What's in the box
| Item | Where | Purpose |
|---|---|---|
| Published app | `CrossBuy/publish/` | Release build: DLLs + `wwwroot` + `web.config` (IIS in-process). Copy this whole folder to the server. |
| Production settings (placeholders) | `CrossBuy/publish/appsettings.Production.json` | Real values go on the server (or as env vars in `web.config`). No secrets committed. |
| DB backup | you create it → `CrossBuyDB2.bak` | The schema + data source (see below). |
| Production purge | `deploy/sql/10_purge_for_production.sql` | Run once after restore to get a clean (no-demo-data) DB. |
| Backup command | `deploy/sql/00_backup_dev_db.sql` | Generates `CrossBuyDB2.bak` from the dev DB. |

## Database strategy — READ THIS

**Schema authority is the canonical authored slices in `CrossBuy/deploy/sql`, reconciled with
`dbo.PlatformSchemaHistory`.** Backup/restore and the generated fresh-database package are
provisioning mechanisms; neither proves that every current authored slice is present or recorded.
Do not replay the historical `C:/temp` scripts. Reasons, stated plainly:

1. **EF Core migrations are not the deployment path** in this project. Versioned SQL slices and
   `PlatformSchemaHistory` provide the authored and applied records instead.
2. The schema was built **incrementally with ~90 ad-hoc `sqlcmd` scripts** in `C:/temp`
   (e.g. `acc_phase*.sql`, `inv_phase*.sql`, `cb_mc_*.sql`, `cb_crm_*.sql`). That pile also
   contains throwaway checks, `DROP`s, fixes, and seed scripts. It **cannot be safely or
   verifiably linearized** into a "clean ordered build" — and I cannot test such a build
   without a blank SQL instance to run it against. Shipping it would risk a broken/partial
   schema on day one.
3. A verified `RESTORE` reproduces the schema and data captured in that backup, but does not
   certify newer repository slices. Preserve its history and reconcile exact paths and raw hashes
   after restore.

For an environment provisioned from a backup:
1. Verify the backup and record its source environment and application version.
2. Restore it without replacing or deleting `PlatformSchemaHistory`.
3. Reconcile exact applied paths and raw hashes with the canonical manifest. Record a baseline
   only for individually verified slice identities; never bulk-stamp pending entries.
4. Apply only reviewed, release-accepted canonical slices in dependency order. Reconcile again
   before promotion.

`10_purge_for_production.sql` is a separate data-purge operation, not schema reconciliation; it
must follow the production data-handling procedure.

> The per-module delta scripts in `C:/temp` (`cb_mc_*.sql`, `cb_crm_3_*.sql`, …) are kept as a
> historical record of schema changes. You only need them if you ever rebuild the dev DB by
> hand; for deployment they are **not** used.

### Clean bootstrap package — `sql/fresh/`
A clean from-scratch bootstrap is available and was tested in `deploy/sql/fresh/` (generated from
the verified live schema, not the 90 ad-hoc files). Four ordered scripts (create DB → schema →
config seed → FKs) produce an empty-of-test-data database with only the config needed to log in.
A fresh DB built from them boots the app and logs in as Admin (verified). See `sql/fresh/README.md`.
Run with `sql/fresh/run_all.cmd`.

This package is a bootstrap snapshot for an empty development/test environment. It is not a second
authored-slice history and does not replace canonical SQL governance. Reconcile its resulting
schema to individually verified slice identities before promoting that environment. A backup
restore remains available when a verified source image is required, under the admission steps above.
