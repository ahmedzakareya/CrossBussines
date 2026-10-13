-- =============================================================================
-- CrossBuy — landed-cost charge English description (session 2026-09)
-- Adds DescriptionEn to LandedCostCharges so an English UI shows English line
-- text instead of the stored Arabic. /Inventory/LandedCostDetails read "شحن" on
-- an English screen because Description was the only column there was.
-- Same shape and reasoning as invoice_line_description_en_2026-07.sql.
-- Additive + idempotent.
-- Run:  sqlcmd -S <server> -d <database> -E -C -i landed_cost_charge_description_en_2026-09.sql
-- Pure DDL — plain UTF-8 is fine, no BOM required.
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH('dbo.LandedCostCharges','DescriptionEn') IS NULL
BEGIN
    ALTER TABLE dbo.LandedCostCharges ADD DescriptionEn NVARCHAR(400) NULL;
    PRINT 'ADDED LandedCostCharges.DescriptionEn';
END ELSE PRINT 'LandedCostCharges.DescriptionEn EXISTS';
GO
