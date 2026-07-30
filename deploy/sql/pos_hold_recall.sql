-- POS-4c Hold/Recall (takeaway parking). A held order stays Status='Open' (NO accounting) but is set aside.
-- Idempotent: safe to re-run.
IF COL_LENGTH('PosOrders','IsHeld') IS NULL
    ALTER TABLE PosOrders ADD IsHeld bit NOT NULL CONSTRAINT DF_PosOrders_IsHeld DEFAULT 0;
GO
IF COL_LENGTH('PosOrders','HeldAt') IS NULL
    ALTER TABLE PosOrders ADD HeldAt datetime2 NULL;
GO
