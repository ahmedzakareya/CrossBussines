# ⛔ DO NOT RUN `dotnet ef migrations add` AGAINST THIS PROJECT

**The EF Core migration mechanism in this repo is DEAD. The schema is managed by hand-written SQL.**

## Why

- The model snapshot (`CrossDbContextModelSnapshot.cs`) is **frozen at April 2025** and knows only **26 tables**. It has NONE of the current schema — no POS, no ExchangeRates, no SalesInvoices, no TaxCodes, no Comm, no CRM/Projects/Manufacturing tables.
- The live database has **~200 tables**, all created and evolved through **idempotent SQL scripts in [`deploy/sql/`](../deploy/sql/)** — never through EF migrations.
- The application **does not call `Database.Migrate()`** at startup. Migrations are never applied.

## What happens if you run `migrations add` anyway

Verified 2026-08-01: a single `dotnet ef migrations add` generates a **full-schema recreate** —
**197 `CreateTable` + 197 `DropTable` + 52 `AddColumn` + 52 `DropColumn` + 12 `RenameColumn` = 249 destructive / data-loss operations.**
Applying it would either fail (tables already exist) or **destroy the database**. It is NOT a clean "add one column" migration — the stale snapshot makes EF think the entire schema is new.

## The rule for ALL schema changes (including deferred items: HM-D8, HM-D16, HM-D28/StoreOldPrice, …)

1. Write an **idempotent SQL script** in `deploy/sql/` (guard every change with `IF NOT EXISTS` / `IF COL_LENGTH(...) IS NULL` etc.).
2. Update the EF **model** (entity class + any `CrossDbContext` precision pin) to match the new column.
3. **Never** generate or apply an EF migration, and **never** edit `CrossDbContextModelSnapshot.cs`.

## The guard that replaces migrations

Because there is no migration to catch model↔DB drift, `GET /api/dev/inv-test-integrity` runs a **permanent precision check** (`ef_precision_vs_db`): every one of the ~352 `decimal` properties must have a model `(precision, scale)` equal to its real DB column. Any divergence raises `failedCount`. This is what caught HM-D27 (EF's default `(18,2)` had been silently truncating money for 14 months). Keep it green.

## Deferred (large, standalone, NOT scheduled)

Re-baseline the snapshot from the live database (scaffold/reverse-engineer, then a `Migrations/0000_Baseline` marked as already-applied) so EF migrations could be revived. Big effort; do not attempt casually.
