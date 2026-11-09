-- Kuwait fixed public holidays for company 1 (its rule pack is Kuwait). Additive and idempotent; deletes nothing.
SET NOCOUNT ON;
INSERT INTO OfficialHolidays (CompanyID, NameAr, NameEn, HolidayDate, IsRecurring, Notes, CreatedAt)
SELECT v.c, v.ar, v.en, v.d, 1, N'عطلة رسمية - الكويت', SYSUTCDATETIME()
FROM (VALUES (1, N'العيد الوطني', N'Kuwait National Day', CAST('2026-02-25' AS date)),
             (1, N'عيد التحرير', N'Kuwait Liberation Day', CAST('2026-02-26' AS date))) v(c, ar, en, d)
WHERE NOT EXISTS (SELECT 1 FROM OfficialHolidays h WHERE h.CompanyID = v.c AND MONTH(h.HolidayDate) = MONTH(v.d) AND DAY(h.HolidayDate) = DAY(v.d));
SELECT @@ROWCOUNT AS inserted;
