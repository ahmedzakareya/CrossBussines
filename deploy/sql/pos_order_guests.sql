-- PROPOSAL ONLY: NOT APPLIED / NOT RELEASE ACCEPTED.
-- SliceId: R1-POS-ORDER-GUESTS-001
-- Supersedes both inputs after review; originals remain intact.
-- Input: CrossBuy/deploy/sql/pos_order_guests.sql SHA256 72b2af6a5be1f2c5000348cde6c7333bc2d55dec31be99a86ae5460b7e55da2e
-- Input: deploy/sql/pos_order_guests.sql SHA256 722d8f0954cc6ff317ab0968e976ad88f4bb52cca0a6bfe116014d025f543bcd
-- POS-4 guests: number of guests (people) sharing one invoice on a table (operational, no GL). Idempotent. Needs pos_order.sql first.
IF COL_LENGTH('PosOrders','Guests') IS NULL ALTER TABLE PosOrders ADD Guests INT NOT NULL DEFAULT 1;
GO
