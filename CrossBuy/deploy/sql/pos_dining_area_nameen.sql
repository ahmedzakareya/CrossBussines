-- POS: optional English name for dining areas (parity with KitchenStation.NameEn / DeliveryZone.NameEn).
-- Lets the floor/tab show English text in an English UI instead of the stored Arabic name. Idempotent.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.DiningAreas') AND name = N'NameEn')
BEGIN
    ALTER TABLE dbo.DiningAreas ADD NameEn NVARCHAR(120) NULL;
END
GO
