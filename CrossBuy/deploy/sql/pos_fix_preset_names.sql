-- Fix Arabic preset names (must be run with UTF-8 codepage: sqlcmd -f 65001).
-- The initial seed was read by sqlcmd in a non-UTF-8 codepage and stored mojibake.
UPDATE dbo.ActivityPresets SET Name = N'مطعم'          WHERE Code = N'Restaurant';
UPDATE dbo.ActivityPresets SET Name = N'كافيه'         WHERE Code = N'Cafe';
UPDATE dbo.ActivityPresets SET Name = N'هايبر ماركت'   WHERE Code = N'Hyper';
UPDATE dbo.ActivityPresets SET Name = N'تجزئة'         WHERE Code = N'Retail';
GO
