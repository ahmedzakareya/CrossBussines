-- Tasks module: add optional Category (tag) column shown as a chip on each task row.
-- Idempotent, additive, nullable. Safe to re-run.
IF COL_LENGTH('TaskItems','Category') IS NULL
    ALTER TABLE TaskItems ADD Category NVARCHAR(80) NULL;
GO
