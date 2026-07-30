-- POS-4b send-to-kitchen (operational only; no GL/stock).
-- Per-line: how much of the line quantity has already gone to the kitchen, and when.
-- Idempotent: safe to re-run.
IF COL_LENGTH('PosOrderLines','SentQty') IS NULL
    ALTER TABLE PosOrderLines ADD SentQty decimal(18,3) NOT NULL CONSTRAINT DF_PosOrderLines_SentQty DEFAULT 0;
GO
IF COL_LENGTH('PosOrderLines','SentAt') IS NULL
    ALTER TABLE PosOrderLines ADD SentAt datetime2 NULL;
GO
