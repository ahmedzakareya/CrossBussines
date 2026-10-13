-- =============================================================================================
-- i18n: the English twin column for every master-data label that never had one.
--
-- WHY: most named master data in this schema stores a PAIR - an Arabic column that is required
-- and an English one that is not (Items.NameEn, ItemCategories.NameEn, Warehouses.NameEn,
-- Accounts.NameEn, ...). Screens read the English one and fall back to the Arabic. The tables
-- below were the ones with a SINGLE name column, so an English UI had nothing to show for them
-- but the Arabic value - the maintenance-schedule grid on the fixed-asset screen is what made
-- this visible.
--
-- Every column is NULLABLE and there is NO backfill: an existing row keeps NULL and every screen
-- falls back to the Arabic name exactly as it does today. Nothing reads these columns as a key,
-- and nothing branches on them.
--
-- Idempotent, additive, applied BEFORE the code - the project rule. Not an EF migration.
--   sqlcmd -S <server> -d <db> -E -I -i deploy/sql/i18n_english_name_columns.sql
-- =============================================================================================

IF COL_LENGTH('ManufWorkCenters', 'NameEn') IS NULL
    ALTER TABLE dbo.ManufWorkCenters ADD NameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('ManufPlans', 'NameEn') IS NULL
    ALTER TABLE dbo.ManufPlans ADD NameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('MaintenanceSchedules', 'TitleEn') IS NULL
    ALTER TABLE dbo.MaintenanceSchedules ADD TitleEn nvarchar(200) NULL;
GO

IF COL_LENGTH('PosTerminals', 'NameEn') IS NULL
    ALTER TABLE dbo.PosTerminals ADD NameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('BranchPaymentMethods', 'DisplayNameEn') IS NULL
    ALTER TABLE dbo.BranchPaymentMethods ADD DisplayNameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('Drivers', 'NameEn') IS NULL
    ALTER TABLE dbo.Drivers ADD NameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('CrmSlaPolicies', 'NameEn') IS NULL
    ALTER TABLE dbo.CrmSlaPolicies ADD NameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('ManufRoutingOps', 'OperationNameEn') IS NULL
    ALTER TABLE dbo.ManufRoutingOps ADD OperationNameEn nvarchar(200) NULL;
GO

IF COL_LENGTH('TaskChecklistItems', 'TitleEn') IS NULL
    ALTER TABLE dbo.TaskChecklistItems ADD TitleEn nvarchar(400) NULL;
GO

IF COL_LENGTH('Brands', 'TradeNameEn') IS NULL
    ALTER TABLE dbo.Brands ADD TradeNameEn nvarchar(200) NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- DATA REPAIR, not a schema change: MaintenanceSchedules.Type is an ENGLISH CODE
-- (Preventive | Inspection | Calibration | Repair) - the entity default, the only values the
-- form offers, and what any future branch on it would compare against. Five seeded rows hold
-- Arabic prose there instead, so the grid printed "وقائية" in an English UI and the column was
-- outside its own domain. Mapped, not translated on the fly: a display-time translation would
-- leave the invalid value in the table.
-- ---------------------------------------------------------------------------------------------
UPDATE dbo.MaintenanceSchedules SET Type = 'Preventive' WHERE Type = N'وقائية';
UPDATE dbo.MaintenanceSchedules SET Type = 'Inspection' WHERE Type IN (N'دورية', N'فحص');
UPDATE dbo.MaintenanceSchedules SET Type = 'Calibration' WHERE Type = N'معايرة';
UPDATE dbo.MaintenanceSchedules SET Type = 'Repair'     WHERE Type IN (N'إصلاح', N'اصلاح');
GO

-- ---------------------------------------------------------------------------------------------
-- SEED-ONLY BACKFILL. These five maintenance schedules ship with the demo dataset, so their
-- English title is a translation of known text rather than a guess about someone's data. Guarded
-- on TitleEn IS NULL: it never overwrites a title a user has typed, and re-running is a no-op.
-- Real customer rows are deliberately left NULL - the screen falls back to the Arabic title, and
-- filling one in is the operator's call, not this script's.
-- ---------------------------------------------------------------------------------------------
UPDATE dbo.MaintenanceSchedules SET TitleEn = N'Oil and filter change'
    WHERE TitleEn IS NULL AND Title = N'تغيير زيت وفلتر';
UPDATE dbo.MaintenanceSchedules SET TitleEn = N'Routine server inspection'
    WHERE TitleEn IS NULL AND Title = N'فحص دوري للخوادم';
UPDATE dbo.MaintenanceSchedules SET TitleEn = N'Computer equipment maintenance'
    WHERE TitleEn IS NULL AND Title = N'صيانة أجهزة الحاسب';
UPDATE dbo.MaintenanceSchedules SET TitleEn = N'Air-conditioning filter cleaning'
    WHERE TitleEn IS NULL AND Title = N'تنظيف فلاتر التكييف';
UPDATE dbo.MaintenanceSchedules SET TitleEn = N'Office furniture inspection'
    WHERE TitleEn IS NULL AND Title = N'فحص الأثاث المكتبي';
GO

PRINT 'i18n_english_name_columns: English twin columns present; MaintenanceSchedules.Type normalised.';
GO
