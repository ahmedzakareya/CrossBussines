-- ============================================================================
-- Pricing 2D — Discount approval ceiling. A sales line discount% above MaxLineDiscountPct
-- needs manager authority (Block) or notifies (Warn). Idempotent. No GL impact.
-- ============================================================================

IF COL_LENGTH('InventorySettings','MaxLineDiscountPct') IS NULL
    ALTER TABLE InventorySettings ADD MaxLineDiscountPct decimal(19,4) NOT NULL CONSTRAINT DF_InvSettings_MaxDisc DEFAULT(0);
GO
IF COL_LENGTH('InventorySettings','DiscountApprovalMode') IS NULL
    ALTER TABLE InventorySettings ADD DiscountApprovalMode nvarchar(10) NOT NULL CONSTRAINT DF_InvSettings_DiscMode DEFAULT('Off');
GO
