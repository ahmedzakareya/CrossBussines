-- POS-4d-2 merge tables: the target order a voided source order was merged into (audit link, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrders','MergedIntoOrderId') IS NULL ALTER TABLE PosOrders ADD MergedIntoOrderId INT NULL;
GO
