-- =============================================================================
-- CrossBuy — CRM Ticket English twins for Description + Category (session 2026-07)
-- So English/French UI can show/enter English ticket description & category.
-- Additive + idempotent. Pure DDL — plain UTF-8 is fine, no BOM required.
-- Run:  sqlcmd -S <server> -d <database> -E -C -i ticket_desc_category_en_2026-07.sql
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF COL_LENGTH('dbo.CrmTickets','DescriptionEn') IS NULL
BEGIN
    ALTER TABLE dbo.CrmTickets ADD DescriptionEn NVARCHAR(2000) NULL;
    PRINT 'ADDED CrmTickets.DescriptionEn';
END ELSE PRINT 'CrmTickets.DescriptionEn EXISTS';
GO

IF COL_LENGTH('dbo.CrmTickets','CategoryEn') IS NULL
BEGIN
    ALTER TABLE dbo.CrmTickets ADD CategoryEn NVARCHAR(200) NULL;
    PRINT 'ADDED CrmTickets.CategoryEn';
END ELSE PRINT 'CrmTickets.CategoryEn EXISTS';
GO
