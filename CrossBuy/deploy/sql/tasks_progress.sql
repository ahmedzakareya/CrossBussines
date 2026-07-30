-- Task progress percent (0..100). Additive + idempotent.
IF COL_LENGTH('TaskItems','ProgressPct') IS NULL ALTER TABLE TaskItems ADD ProgressPct INT NOT NULL DEFAULT 0;
GO
