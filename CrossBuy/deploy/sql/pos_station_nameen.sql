-- KDS station bilingual name: add NameEn to KitchenStations + seed existing rows (idempotent).
IF COL_LENGTH('KitchenStations','NameEn') IS NULL
    ALTER TABLE KitchenStations ADD NameEn NVARCHAR(200) NULL;
GO
-- seed English names for stations that don't have one yet (by code hints, else station type)
UPDATE KitchenStations SET NameEn = 'Grill'   WHERE (NameEn IS NULL OR NameEn = '') AND Code LIKE '%GRL%';
UPDATE KitchenStations SET NameEn = 'Bar'     WHERE (NameEn IS NULL OR NameEn = '') AND (Code LIKE '%BAR%' OR StationType = 'Bar');
UPDATE KitchenStations SET NameEn = 'Prep'    WHERE (NameEn IS NULL OR NameEn = '') AND StationType = 'Prep';
UPDATE KitchenStations SET NameEn = 'Kitchen' WHERE (NameEn IS NULL OR NameEn = '') AND StationType = 'Kitchen';
GO
