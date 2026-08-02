# CrossBuy — Documentation Index

## ⚠️ Read first — schema is managed by manual SQL, NOT EF migrations

**Do not run `dotnet ef migrations add`.** The EF snapshot is frozen at April 2025 (26 tables) and knows none of the current schema; a single `migrations add` generates a **197-table recreate (249 destructive ops)** that would fail or destroy the database. See [`../CrossBuy/Migrations/DO-NOT-USE-EF-MIGRATIONS.md`](../CrossBuy/Migrations/DO-NOT-USE-EF-MIGRATIONS.md).

**Every schema change** (new table/column, precision widening, deferred items HM-D8 / HM-D16 / HM-D28 …) is made by an **idempotent SQL script in [`../CrossBuy/deploy/sql/`](../CrossBuy/deploy/sql/)** plus a matching EF model/precision update — never a migration, never editing the snapshot.

Model↔DB precision drift is caught by the permanent `ef_precision_vs_db` guard in `GET /api/dev/inv-test-integrity` (any divergence raises `failedCount`). This is what caught HM-D27 — EF's default `decimal(18,2)` had silently truncated all money to 2dp for 14 months.

## Design & analysis documents

The `docs/` folder holds per-module design and analysis references (Accounting, Inventory, Multi-Currency, POS/Restaurant, Projects, CRM, HR, Manufacturing, Pricing, AI, Communication Hub). Deviations and deferred items are tracked in [`../CrossBuy/deploy/AUDIT-DEVIATIONS.md`](../CrossBuy/deploy/AUDIT-DEVIATIONS.md).
