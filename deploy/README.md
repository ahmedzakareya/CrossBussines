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

**The authoritative way to stand up the schema is BACKUP / RESTORE of `CrossBuyDB2`, not
re-running SQL scripts.** Reasons, stated plainly:

1. **EF Core migrations are broken** in this project — there is no `dotnet ef database update`
   path and no single canonical migration history.
2. The schema was built **incrementally with ~90 ad-hoc `sqlcmd` scripts** in `C:/temp`
   (e.g. `acc_phase*.sql`, `inv_phase*.sql`, `cb_mc_*.sql`, `cb_crm_*.sql`). That pile also
   contains throwaway checks, `DROP`s, fixes, and seed scripts. It **cannot be safely or
   verifiably linearized** into a "clean ordered build" — and I cannot test such a build
   without a blank SQL instance to run it against. Shipping it would risk a broken/partial
   schema on day one.
3. A `RESTORE` reproduces the **exact, already-verified** schema (every table, index, the
   `AppCache` session table, etc.) in one step.

So the flow is:
1. **Locally:** `BACKUP DATABASE CrossBuyDB2` → `CrossBuyDB2.bak` (see `00_backup_dev_db.sql`).
2. **On the server:** `RESTORE` the `.bak`.
3. **On the server:** run `10_purge_for_production.sql` to wipe demo/test data while keeping
   masters & config → clean production DB.

> The per-module delta scripts in `C:/temp` (`cb_mc_*.sql`, `cb_crm_3_*.sql`, …) are kept as a
> historical record of schema changes. You only need them if you ever rebuild the dev DB by
> hand; for deployment they are **not** used.

### Alternative (also verified): build from scratch — `sql/fresh/`
A clean from-scratch build is now available **and tested** in `deploy/sql/fresh/` (generated from
the verified live schema, not the 90 ad-hoc files). Four ordered scripts (create DB → schema →
config seed → FKs) produce an empty-of-test-data CrossBuyDB2 with only the config needed to log in.
A fresh DB built from them boots the app and logs in as Admin (verified). See `sql/fresh/README.md`.
Run with `sql/fresh/run_all.cmd`.

**Pick one:** *restore* = exact copy of dev data; *fresh build* = clean slate (you create your own
company/branches/customers/items). Both end at a working CrossBuyDB2.
