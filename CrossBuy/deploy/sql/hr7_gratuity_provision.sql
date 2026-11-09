-- ==========================================================================================
-- HR (2026-11-08): the end-of-service gratuity provision account, so the liability is booked monthly instead of
-- landing in one month's expense when somebody leaves. Company 1 (the others get it when their chart is set up; the
-- provision screen refuses with a message naming the missing account). Idempotent.
--   2106  End-of-service gratuity provision  (current liabilities, beside 2103 Payroll payable)
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF EXISTS (SELECT 1 FROM dbo.Accounts WHERE CompanyID = 1 AND Code = N'21')
   AND NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE CompanyID = 1 AND Code = N'2106')
    INSERT INTO dbo.Accounts (CompanyID, Code, Name, NameEn, AccountTypeId, ParentId, IsPostable, IsActive, RequireCostCenter, RequireProject, CreatedAt)
    SELECT 1, N'2106', N'مخصص مكافأة نهاية الخدمة', N'End-of-service gratuity provision', p.AccountTypeId, p.ID, 1, 1, 0, 0, SYSUTCDATETIME()
      FROM dbo.Accounts p WHERE p.CompanyID = 1 AND p.Code = N'21';
