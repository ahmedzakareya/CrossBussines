-- Projects & Contracting — P2 (contract: advance + retention). Adds two GL accounts + contract-term columns.
-- Money is posted later via JournalEntryService (no new writer). Idempotent. Company 1.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- 1) Advance-from-customers = current LIABILITY (2104), under 21 (التزامات متداولة)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='2104')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CashFlowCategory, CreatedAt)
    SELECT 1, '2104', N'دفعات مقدمة من عملاء', N'Advances from customers',
           (SELECT ID FROM AccountTypes WHERE Code='LIAB'),
           (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='21'),
           1, 1, 0, 0, 'Operating', SYSUTCDATETIME();

-- 2) Retention held by customers = current ASSET / receivable (1104 — 1103 is المخزون, so use the free 1104), under 11 (أصول متداولة)
IF NOT EXISTS (SELECT 1 FROM Accounts WHERE CompanyID=1 AND Code='1104')
    INSERT INTO Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CashFlowCategory, CreatedAt)
    SELECT 1, '1104', N'أرصدة محتجزة لدى العملاء', N'Retention receivable',
           (SELECT ID FROM AccountTypes WHERE Code='ASSET'),
           (SELECT ID FROM Accounts WHERE CompanyID=1 AND Code='11'),
           1, 1, 0, 0, 'Operating', SYSUTCDATETIME();

-- 3) contract-term columns on Projects (nullable, additive — no GL effect by themselves)
IF COL_LENGTH('dbo.Projects','AdvancePercent')   IS NULL ALTER TABLE dbo.Projects ADD AdvancePercent   decimal(9,4) NULL;
IF COL_LENGTH('dbo.Projects','RetentionPercent') IS NULL ALTER TABLE dbo.Projects ADD RetentionPercent decimal(9,4) NULL;

SELECT Code, Name, IsPostable FROM Accounts WHERE CompanyID=1 AND Code IN ('2104','1104') ORDER BY Code;
