-- POS-4 guests: number of guests (people) sharing one invoice on a table (operational, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrders','Guests') IS NULL ALTER TABLE PosOrders ADD Guests INT NOT NULL DEFAULT 1;
GO
