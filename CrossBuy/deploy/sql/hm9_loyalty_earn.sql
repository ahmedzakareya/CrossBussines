-- HM-9 slice 2: loyalty points EARN. Additive, idempotent, ZERO financial impact (points are a memo — no GL, no stock).
--   PointsMovements                 — the ledger; the balance is DERIVED (SUM of signed Points), NOT a stored column.
--   Companies.LoyaltyPointsPerCurrencyUnit          — company-DEFAULT earn rate (points per document-currency unit, on net).
--   BranchPosSettings.LoyaltyPointsPerCurrencyUnit  — branch OVERRIDE (null = inherit the company default).
--   ItemCategories.LoyaltyEligible  — does this category earn? default 1 (exclusions set at category level).
--   Items.LoyaltyEligible           — per-item override (null = inherit the category).
-- NOT a migration.
SET NOCOUNT ON;

IF OBJECT_ID('PointsMovements','U') IS NULL
BEGIN
    CREATE TABLE PointsMovements (
        ID              INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID       INT           NOT NULL,
        CustomerId      INT           NOT NULL,
        Kind            NVARCHAR(30)  NOT NULL,          -- Earn | ReturnReversal | CancelReversal
        SourceInvoiceId INT           NULL,              -- the sale that earned (trace + reversal anchor)
        SourceDocType   NVARCHAR(40)  NULL,              -- reversal provenance, e.g. SalesReturn
        SourceDocId     INT           NULL,
        Points          BIGINT        NOT NULL,          -- signed WHOLE points: +earn (floor), -reversal
        CreatedAt       DATETIME2     NOT NULL,
        CreatedBy       INT           NULL
    );
    CREATE INDEX IX_PointsMovements_Customer ON PointsMovements (CompanyID, CustomerId);
    CREATE INDEX IX_PointsMovements_Invoice  ON PointsMovements (SourceInvoiceId);
END;

IF COL_LENGTH('Companies','LoyaltyPointsPerCurrencyUnit') IS NULL
    ALTER TABLE Companies ADD LoyaltyPointsPerCurrencyUnit DECIMAL(18,6) NULL;
IF COL_LENGTH('BranchPosSettings','LoyaltyPointsPerCurrencyUnit') IS NULL
    ALTER TABLE BranchPosSettings ADD LoyaltyPointsPerCurrencyUnit DECIMAL(18,6) NULL;
IF COL_LENGTH('ItemCategories','LoyaltyEligible') IS NULL
    ALTER TABLE ItemCategories ADD LoyaltyEligible BIT NOT NULL CONSTRAINT DF_ItemCategories_LoyaltyEligible DEFAULT (1);
IF COL_LENGTH('Items','LoyaltyEligible') IS NULL
    ALTER TABLE Items ADD LoyaltyEligible BIT NULL;

SELECT OBJECT_ID('PointsMovements','U') AS PointsMovementsTable,
       COL_LENGTH('Companies','LoyaltyPointsPerCurrencyUnit') AS CoRate,
       COL_LENGTH('BranchPosSettings','LoyaltyPointsPerCurrencyUnit') AS BranchRate,
       COL_LENGTH('ItemCategories','LoyaltyEligible') AS CatFlag,
       COL_LENGTH('Items','LoyaltyEligible') AS ItemFlag;
