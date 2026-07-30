-- TM-4: mark when a task's labor was posted to its linked work order (prevents double-posting). Additive + idempotent.
IF COL_LENGTH('TaskItems','LaborPostedAt') IS NULL ALTER TABLE TaskItems ADD LaborPostedAt DATETIME2 NULL;
GO
