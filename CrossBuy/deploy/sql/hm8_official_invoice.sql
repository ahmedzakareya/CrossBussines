-- HM-8: official A4 invoice. Additive, idempotent, ALL nullable, ZERO financial impact.
--   Logo: NO new column — Companies.CompanyImage (already nullable) IS the logo; the template reuses it (no duplication).
--   SalesInvoices.CustomerNameOverride / CustomerTaxNoOverride — a DISPLAY-ONLY beneficiary for a walk-in sale that a
--     customer asks an invoice for. The posting is UNTOUCHED (CustomerId stays the walk-in, control account + JE + amounts
--     unchanged). Set-once + audited (By/At). Allowed ONLY when the invoice's tax is 0 (a taxed invoice's beneficiary must
--     equal the ledger account holder — enforced in code, HM-8 rule).
-- NOT a migration.
SET NOCOUNT ON;

IF COL_LENGTH('SalesInvoices','CustomerNameOverride') IS NULL
    ALTER TABLE SalesInvoices ADD CustomerNameOverride NVARCHAR(200) NULL;
IF COL_LENGTH('SalesInvoices','CustomerTaxNoOverride') IS NULL
    ALTER TABLE SalesInvoices ADD CustomerTaxNoOverride NVARCHAR(50) NULL;
IF COL_LENGTH('SalesInvoices','CustomerOverrideBy') IS NULL
    ALTER TABLE SalesInvoices ADD CustomerOverrideBy NVARCHAR(100) NULL;
IF COL_LENGTH('SalesInvoices','CustomerOverrideAt') IS NULL
    ALTER TABLE SalesInvoices ADD CustomerOverrideAt DATETIME2 NULL;

SELECT COL_LENGTH('SalesInvoices','CustomerNameOverride') AS NameOv,
       COL_LENGTH('SalesInvoices','CustomerTaxNoOverride') AS TaxOv,
       COL_LENGTH('SalesInvoices','CustomerOverrideBy') AS OvBy,
       COL_LENGTH('SalesInvoices','CustomerOverrideAt') AS OvAt;
