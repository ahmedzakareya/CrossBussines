-- HM-10 slice A: hyper pay idempotency. Additive, idempotent, ZERO financial impact.
--   HyperPayTokens — one INSERT-keyed row per hyper pay intent (client GUID). The UNIQUE index on (CompanyId, Token) is the
--   race backstop: a concurrent double-submit of the SAME order collapses to ONE invoice because the losing pay fails the
--   INSERT (a field-on-order UPDATE could not — two updates of the same row to the same token make no duplicate row). This is
--   the same primitive as PosSyncLog but a SEPARATE table (the restaurant lane's PosSyncLog is untouched).
-- NOT a migration.
SET NOCOUNT ON;

IF OBJECT_ID('HyperPayTokens','U') IS NULL
BEGIN
    CREATE TABLE HyperPayTokens (
        ID         INT IDENTITY(1,1) PRIMARY KEY,
        CompanyId  INT           NOT NULL,
        Token      NVARCHAR(64)  NOT NULL,
        OrderId    INT           NOT NULL,
        InvoiceId  INT           NULL,
        CreatedAt  DATETIME2     NOT NULL
    );
    -- pre-check: a brand-new table has zero rows, so the unique index can never be blocked by a pre-existing duplicate.
    CREATE UNIQUE INDEX UX_HyperPayTokens_Token ON HyperPayTokens (CompanyId, Token);
END;

SELECT OBJECT_ID('HyperPayTokens','U') AS HyperPayTokensTable,
       (SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_HyperPayTokens_Token') AS UniqueIndex;
