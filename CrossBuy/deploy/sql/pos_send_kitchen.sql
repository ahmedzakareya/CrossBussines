-- POS-4b send-to-kitchen: per-line sent quantity + timestamp (operational marker, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrderLines','SentQty') IS NULL ALTER TABLE PosOrderLines ADD SentQty DECIMAL(18,3) NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('PosOrderLines','SentAt') IS NULL ALTER TABLE PosOrderLines ADD SentAt DATETIME2 NULL;
GO
