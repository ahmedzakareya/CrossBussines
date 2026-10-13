-- =============================================================================
-- CrossBuy — CRM bilingual free-text columns (session 2026-07)
-- Adds English-twin (*En) display columns so English/French UI shows English
-- content instead of the stored Arabic. Additive + idempotent; NO data dropped.
-- Run:  sqlcmd -S <server> -d <database> -E -C -i crm_bilingual_fields_2026-07.sql
-- Pure DDL (no Arabic literals) — plain UTF-8 is fine, no BOM required.
-- =============================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- 1) CRM account: English industry name (shown when UI is not Arabic)
IF COL_LENGTH('dbo.CrmAccounts','IndustryEn') IS NULL
BEGIN
    ALTER TABLE dbo.CrmAccounts ADD IndustryEn NVARCHAR(200) NULL;
    PRINT 'ADDED CrmAccounts.IndustryEn';
END ELSE PRINT 'CrmAccounts.IndustryEn EXISTS';
GO

-- 2) CRM contact: English name + title
IF COL_LENGTH('dbo.CrmContacts','NameEn') IS NULL
BEGIN
    ALTER TABLE dbo.CrmContacts ADD NameEn NVARCHAR(200) NULL;
    PRINT 'ADDED CrmContacts.NameEn';
END ELSE PRINT 'CrmContacts.NameEn EXISTS';
GO

IF COL_LENGTH('dbo.CrmContacts','TitleEn') IS NULL
BEGIN
    ALTER TABLE dbo.CrmContacts ADD TitleEn NVARCHAR(200) NULL;
    PRINT 'ADDED CrmContacts.TitleEn';
END ELSE PRINT 'CrmContacts.TitleEn EXISTS';
GO

-- =============================================================================
-- NOTE: English *content* backfills for demo data (CrmAccounts.IndustryEn,
-- CrmContacts.NameEn/TitleEn, JournalEntries/JournalEntryLines.DescriptionEn)
-- are DATA, not structure, and are NOT included here. If the target server
-- needs the same English display text, request the separate data-backfill script.
-- =============================================================================
