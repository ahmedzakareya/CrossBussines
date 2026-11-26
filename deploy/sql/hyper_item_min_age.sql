-- HM-AGE: the minimum age a buyer must prove for an item.
--
-- The lane could ring up anything. Where tobacco, energy drinks or a restricted medicine are sold,
-- the refusal has to live with the ITEM, not with a cashier's memory: a new cashier on a night shift
-- cannot be expected to know which SKUs carry an age bar, and a chain that cannot show the control
-- existed has no answer for an inspector.
--
-- NULL = no restriction, which is almost every row, so the column is nullable and nothing is
-- backfilled. A number is the age in YEARS rather than a flag, because jurisdictions differ (18 for
-- tobacco, 21 where the licence says so) and a flag would force the number into code.
--
-- Idempotent: safe to run again.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Items') AND name = 'MinAgeYears')
BEGIN
    ALTER TABLE dbo.Items ADD MinAgeYears INT NULL;
    PRINT 'Items.MinAgeYears added';
END
ELSE PRINT 'Items.MinAgeYears already present';
GO

-- A sane bound. Not a business rule - a typo guard, so 180 cannot be stored and silently block a sale.
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Items_MinAgeYears')
BEGIN
    ALTER TABLE dbo.Items WITH NOCHECK
        ADD CONSTRAINT CK_Items_MinAgeYears CHECK (MinAgeYears IS NULL OR (MinAgeYears BETWEEN 1 AND 99));
    PRINT 'CK_Items_MinAgeYears added';
END
ELSE PRINT 'CK_Items_MinAgeYears already present';
GO

-- The proof that the control ran. A gate the cashier can click past leaves nothing behind; these
-- three columns say WHO cleared the sale, WHEN, and against which age bar - which is what an
-- inspector asks for, and what makes a cashier who waves everything through visible in a report.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.PosOrders') AND name = 'AgeVerifiedByEmployeeId')
BEGIN
    ALTER TABLE dbo.PosOrders ADD AgeVerifiedByEmployeeId INT NULL,
                                  AgeVerifiedAtUtc        DATETIME2 NULL,
                                  AgeVerifiedMinYears     INT NULL;
    PRINT 'PosOrders age-verification audit added';
END
ELSE PRINT 'PosOrders age-verification audit already present';
GO
