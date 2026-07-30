-- ============================================================================
-- Pricing 2A — Gross-margin floor (min-price)
-- Idempotent. Adds the margin-floor settings + per-item override.
-- Comparison is always in the FUNCTIONAL currency (foreign doc price is converted
-- before comparing to cost×(1+margin%)). Modes: Off (default) | Warn | Block.
-- ============================================================================

-- Global floor + enforcement mode on inventory settings
IF COL_LENGTH('InventorySettings','MinMarginPct') IS NULL
    ALTER TABLE InventorySettings ADD MinMarginPct decimal(19,4) NOT NULL CONSTRAINT DF_InvSettings_MinMarginPct DEFAULT(0);
GO
IF COL_LENGTH('InventorySettings','MinMarginMode') IS NULL
    ALTER TABLE InventorySettings ADD MinMarginMode nvarchar(10) NOT NULL CONSTRAINT DF_InvSettings_MinMarginMode DEFAULT('Off');
GO

-- Per-item override (NULL → fall back to the global setting)
IF COL_LENGTH('Items','MinMarginPct') IS NULL
    ALTER TABLE Items ADD MinMarginPct decimal(19,4) NULL;
GO
