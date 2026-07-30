-- =============================================================================
-- CrossBuy — CRM Lead English-name column (session 2026-07)
-- Adds Leads.NameEn so English/French UI shows the English lead name.
-- Additive + idempotent. Pure DDL — plain UTF-8 is fine, no BOM required.
-- Run:  sqlcmd -S <server> -d <database> -E -C -i lead_name_en_2026-07.sql
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH('dbo.Leads','NameEn') IS NULL
BEGIN
    ALTER TABLE dbo.Leads ADD NameEn NVARCHAR(200) NULL;
    PRINT 'ADDED Leads.NameEn';
END ELSE PRINT 'Leads.NameEn EXISTS';
GO
