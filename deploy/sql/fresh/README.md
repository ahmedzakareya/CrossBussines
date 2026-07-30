# Build CrossBuyDB2 from scratch (clean production database)

These four scripts create a **brand-new, empty-of-test-data** CrossBuyDB2 with only the schema
and the minimum config needed to log in and operate. Generated from — and **verified against** —
the live verified schema (a fresh DB built from these files boots the app and logs in as Admin).

| File | What it does |
|---|---|
| `00_create_database.sql` | Creates `CrossBuyDB2` if missing (run on `master`). |
| `01_schema.sql` | All 136 tables + indexes + PK/defaults/checks (no FKs yet). |
| `03_seed_config.sql` | Config/reference seed: chart of accounts, account types, currencies + rates, fiscal year/periods, cost centers, posting rules, number sequences, settings, tax codes/brackets, UoM, lookups, default CRM pipeline, **primary company (#1)**, and the **Admin** user + its employee. **No** customers/items/vendors/invoices/test users. |
| `02_foreign_keys.sql` | The 31 foreign keys (added last, so data load order never matters). |

> **Order matters:** create DB → schema → seed → foreign keys. All scripts are idempotent
> (`IF NOT EXISTS`), so a partial re-run is safe.

## Run it — option A: one command (sqlcmd)
SQL Server 2022 Express, instance `SQLEXPRESS`:
```cmd
cd deploy\sql\fresh
run_all.cmd
```
`run_all.cmd` defaults to `localhost\SQLEXPRESS`; edit the `SVR` line if your instance differs
(e.g. `set "SVR=."` for a default instance).

## Run it — option B: manual sqlcmd (one by one)
```cmd
set SVR=localhost\SQLEXPRESS
sqlcmd -S %SVR% -E -C -b -i "00_create_database.sql"
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "01_schema.sql"
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "03_seed_config.sql"
sqlcmd -S %SVR% -d CrossBuyDB2 -E -C -b -i "02_foreign_keys.sql"
```
(`-E` = Windows auth, `-C` = trust server cert, `-b` = stop on error.)

## Run it — option C: SSMS
1. Open `00_create_database.sql` → execute (against `master`).
2. In the database dropdown pick **CrossBuyDB2**, then open and execute, in order:
   `01_schema.sql` → `03_seed_config.sql` → `02_foreign_keys.sql`.

## After it runs
- Log in to the app as **Admin / Admin@123**, then change the password and the company details.
- Verified end-to-end: a fresh DB from these scripts boots under `ASPNETCORE_ENVIRONMENT=Production`,
  Admin logs in, and the CRM + Accounting screens load. `/api/dev/*` returns 404 in Production.

> This from-scratch build and the **restore-from-backup** path (see `../../README.md`) are two ways
> to the same place. Restore preserves *all* dev data verbatim; this build gives a clean slate.
