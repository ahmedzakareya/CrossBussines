-- POS-4c hold/recall: a held (parked) takeaway order flag + timestamp (operational, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrders','IsHeld') IS NULL ALTER TABLE PosOrders ADD IsHeld BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('PosOrders','HeldAt') IS NULL ALTER TABLE PosOrders ADD HeldAt DATETIME2 NULL;
GO
