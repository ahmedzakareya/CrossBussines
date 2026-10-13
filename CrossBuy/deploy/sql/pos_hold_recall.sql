-- PROPOSAL ONLY: NOT APPLIED / NOT RELEASE ACCEPTED.
-- SliceId: R1-POS-HOLD-RECALL-001
-- Supersedes both inputs after review; originals remain intact.
-- Input: CrossBuy/deploy/sql/pos_hold_recall.sql SHA256 ced9b5266e1c1f8e0722903017307b4505144d9b5a6a3a90c753b427769ab59f
-- Input: deploy/sql/pos_hold_recall.sql SHA256 f7348e6e28d3822b013332b164941c381047acea99796fd37e2f481d1027479c
-- POS-4c hold/recall: a held (parked) takeaway order flag + timestamp (operational, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrders','IsHeld') IS NULL ALTER TABLE PosOrders ADD IsHeld BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('PosOrders','HeldAt') IS NULL ALTER TABLE PosOrders ADD HeldAt DATETIME2 NULL;
GO
