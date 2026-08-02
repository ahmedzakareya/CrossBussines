-- HM-D18 (minimal): a DOCUMENT-CURRENCY (KWD) price list for the hypermarket, consumed by the cashier path.
-- The shelf price is entered directly in KWD (0.750), so PricingService.GetPriceAsync returns it from the list
-- (Source="list") with NO functional(EGP)→document(KWD) conversion. The list is linked to the hyper branch via
-- BranchPosSetting.DefaultPriceListId; the cashier path passes that id to GetPriceAsync and REJECTS any item that is
-- not in the list (never silently converts). Idempotent (lookups by natural keys; guarded inserts). NOT a migration.

SET NOCOUNT ON;

DECLARE @co     INT = 1;
DECLARE @kwd    INT = (SELECT ID FROM Currencies WHERE Code = 'KWD');
DECLARE @item   INT = (SELECT ID FROM Items      WHERE CompanyID = @co AND ItemCode = 'HM-DEMO-001');
DECLARE @branch INT = (SELECT ID FROM Branches   WHERE Name = 'HYPER-DEMO');

IF @kwd IS NULL OR @item IS NULL OR @branch IS NULL
BEGIN
    PRINT 'HM-D18 seed skipped: KWD currency / HM-DEMO-001 item / HYPER-DEMO branch not all present.';
    RETURN;
END

DECLARE @list INT = (SELECT TOP 1 ID FROM PriceLists WHERE CompanyID = @co AND Name = 'Hyper KWD' AND CurrencyId = @kwd);
IF @list IS NULL
BEGIN
    INSERT INTO PriceLists (CompanyID, Code, Name, NameEn, CurrencyId, Priority, IsActive)
    VALUES (@co, 'HYPER-KWD', 'Hyper KWD', 'Hyper KWD', @kwd, 0, 1);
    SET @list = SCOPE_IDENTITY();
END

IF NOT EXISTS (SELECT 1 FROM PriceListLines WHERE PriceListId = @list AND ItemId = @item)
    INSERT INTO PriceListLines (PriceListId, ItemId, MinQty, UnitPrice, DiscountPercent, PricingMode, MarkupPercent)
    VALUES (@list, @item, 1, 0.750, 0, 'Fixed', 0);
ELSE
    UPDATE PriceListLines SET UnitPrice = 0.750, PricingMode = 'Fixed' WHERE PriceListId = @list AND ItemId = @item;

UPDATE BranchPosSettings SET DefaultPriceListId = @list
WHERE BranchId = @branch AND (DefaultPriceListId IS NULL OR DefaultPriceListId <> @list);

-- Verify (expect: 1 list, 1 line at 0.750, branch linked)
SELECT (SELECT COUNT(*) FROM PriceLists WHERE ID = @list) AS Lists,
       (SELECT UnitPrice FROM PriceListLines WHERE PriceListId = @list AND ItemId = @item) AS LinePriceKwd,
       (SELECT DefaultPriceListId FROM BranchPosSettings WHERE BranchId = @branch) AS BranchLinkedList;
