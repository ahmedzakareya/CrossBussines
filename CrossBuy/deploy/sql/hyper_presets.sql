-- =============================================================================
-- CrossBuy — HM-0: Hypermarket ActivityPreset + its DEFAULT capability mapping.
-- Reference/config data (not test data). Idempotent — safe to run repeatedly.
-- Matched by Code = 'Hyper' (never by a fixed numeric id).
-- SCOPE GUARD: touches ONLY the 'Hyper' preset. It NEVER inserts or edits any other
-- preset (Restaurant / Cafe / Retail) — existing restaurant branches are hand-tuned and
-- must not be affected by this script, directly or via ApplyPreset.
-- Run with a UTF-8 (BOM) client so the Arabic Name inserts correctly (sqlcmd -f 65001).
-- =============================================================================
SET NOCOUNT ON;

IF NOT EXISTS (SELECT 1 FROM dbo.ActivityPresets WHERE Code = 'Hyper')
BEGIN
    INSERT INTO dbo.ActivityPresets (Code, Name, NameEn, Sort)
    VALUES ('Hyper', N'هايبر ماركت', N'Hypermarket', 3);
    PRINT 'CREATED ActivityPreset Hyper';
END
ELSE PRINT 'ActivityPreset Hyper already EXISTS (left as-is)';

DECLARE @pid INT = (SELECT ID FROM dbo.ActivityPresets WHERE Code = 'Hyper');

-- (Re)define the Hyper preset's default capability mapping to the HYPER catalog keys.
-- Deterministic on every run (delete-then-insert), and scoped to @pid (Hyper) only.
-- Default set: enable the day-one basics; leave the advanced ones off (a branch flips any freely).
DELETE FROM dbo.ActivityPresetCapabilities WHERE PresetId = @pid;

INSERT INTO dbo.ActivityPresetCapabilities (PresetId, CapabilityKey, DefaultEnabled) VALUES
    (@pid, 'BarcodeMulti',  1),   -- basics ON
    (@pid, 'CashDrawer',    1),
    (@pid, 'SuspendResume', 1),
    (@pid, 'PriceCheck',    1),
    (@pid, 'CustomerIdentity', 1),   -- HM-9 slice 1: identity ON (resolves HM-8's taxed-invoice case; independent of loyalty)
    (@pid, 'Weight',        0),   -- advanced OFF (branch enables when needed)
    (@pid, 'ExpiryControl', 0),
    (@pid, 'Promotions',    0),
    (@pid, 'Loyalty',       0),   -- HM-9 slice 2: points earning OFF until redemption ships (no half-feature in production)
    (@pid, 'ShelfLabels',   0);
PRINT 'Hyper preset default capabilities set (5 ON / 5 OFF)';
GO

SELECT p.Code, c.CapabilityKey, c.DefaultEnabled
FROM dbo.ActivityPresets p JOIN dbo.ActivityPresetCapabilities c ON c.PresetId = p.ID
WHERE p.Code = 'Hyper' ORDER BY c.DefaultEnabled DESC, c.CapabilityKey;
GO
