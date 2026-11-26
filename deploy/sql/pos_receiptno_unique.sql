-- A receipt number is a TAX DOCUMENT number, and two orders carrying the same one on the same
-- terminal make the series unauditable.
--
-- There was no unique index here: it had been cancelled, leaving only the detective `receiptno_dup`
-- integrity check, which reads the damage after the fact. The offline-sync path wrote the device's
-- receipt number onto the order with no uniqueness test at all, which is how eight duplicate groups
-- were created in July 2026 and why nothing prevented a ninth.
--
-- FILTERED, and that is the whole point. Those eight groups are PAID orders with invoices, journal
-- entries and stock movements behind them - a tax document is not deleted to make an index build.
-- So the constraint binds everything from order 3387 onward (the last duplicate) and leaves the
-- documented history exactly as it is. New rows cannot collide; old rows are not rewritten.
--
-- The application guard in PosOrderService refuses a duplicate BEFORE it reaches here and records a
-- PosSyncConflict instead, so this index is the floor, not the error message.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PosOrders_Terminal_ReceiptNo_New' AND object_id = OBJECT_ID('dbo.PosOrders'))
BEGIN
    IF EXISTS (
        SELECT 1 FROM dbo.PosOrders o
        WHERE o.ID > 3387 AND o.ReceiptNo IS NOT NULL AND o.TerminalId IS NOT NULL
        GROUP BY o.TerminalId, o.ReceiptNo HAVING COUNT(*) > 1)
    BEGIN
        PRINT 'REFUSED: duplicates exist above the cutoff - clean them before creating the index';
    END
    ELSE
    BEGIN
        CREATE UNIQUE NONCLUSTERED INDEX UX_PosOrders_Terminal_ReceiptNo_New
            ON dbo.PosOrders (TerminalId, ReceiptNo)
            WHERE ReceiptNo IS NOT NULL AND TerminalId IS NOT NULL AND ID > 3387;
        PRINT 'UX_PosOrders_Terminal_ReceiptNo_New created';
    END
END
ELSE PRINT 'UX_PosOrders_Terminal_ReceiptNo_New already present';
GO
