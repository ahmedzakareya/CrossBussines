-- ==========================================================================================
-- Hospital H46 — the patient portal's "forgot password", by email only (owner rulings 2026-11-05). A portal account
-- gets the address its emails go to and the time its password last changed (a session issued before that is no
-- longer honoured); a reset link is kept only as the SHA-256 of its token, works once, for 30 minutes. The email is
-- sent straight to the patient and stored nowhere — not in the communication outbox (owner ruling).
--   HospitalPortalAccounts  + Email, + PasswordChangedAt
--   HospitalPortalResets    one row per link sent
--
-- Additive only (two nullable columns, one new table). Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalAccounts','U') IS NULL
    THROW 51990, 'Run hospital_h24_portal_accounts.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPortalAccounts', 'Email') IS NULL
    ALTER TABLE dbo.HospitalPortalAccounts ADD Email nvarchar(256) NULL;
IF COL_LENGTH('dbo.HospitalPortalAccounts', 'PasswordChangedAt') IS NULL
    ALTER TABLE dbo.HospitalPortalAccounts ADD PasswordChangedAt datetime2 NULL;

IF OBJECT_ID('dbo.HospitalPortalResets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalResets (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        AccountId   int            NOT NULL,
        TokenHash   nvarchar(64)   NOT NULL,
        Email       nvarchar(256)  NOT NULL,
        CreatedAt   datetime2      NOT NULL,
        ExpiresAt   datetime2      NOT NULL,
        UsedAt      datetime2      NULL,
        CONSTRAINT PK_HospitalPortalResets PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPortalResets_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT CK_HospitalPortalResets_Life CHECK (ExpiresAt > CreatedAt)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalResets_TokenHash ON dbo.HospitalPortalResets (TokenHash);
    CREATE INDEX IX_HospitalPortalResets_AccountId_CreatedAt ON dbo.HospitalPortalResets (AccountId, CreatedAt);
END

COMMIT TRANSACTION;
