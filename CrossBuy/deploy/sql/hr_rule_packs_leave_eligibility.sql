-- ==========================================================================================
-- HR3 (2026-11-08): the leave engine's eligibility settings in every country rule pack.
--
-- New fields on each leave line (all editable on /Admin/HrRules; 0/empty = no condition):
--   minServiceMonths   — months of service before the leave may be taken
--   maxTimesPerService — times per career (Hajj = 1; Egyptian maternity = 3)
--   accrual            — "upfront" (the year's days on 1 January) or "monthly" (days ÷ 12 per completed month)
--   religion           — only employees of this religion (Kuwait's Hajj leave: Muslim)
-- Values come from each line's own rule text (the article already quoted there). Lines whose law sets no such
-- condition are left untouched, so nothing is invented.
--
-- Each update is addressed by array index AND guarded by that line's code, and only fills a field that is not yet
-- set, so a company's own edits are never overwritten and a re-run changes nothing. Company 1's copy is a version
-- bump, so the change is visible in its history.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @set TABLE (Country char(2), Idx int, Code nvarchar(20), Field nvarchar(40), NumVal int NULL, TxtVal nvarchar(20) NULL);
INSERT INTO @set VALUES
 ('KW', 0, N'annual',    N'minServiceMonths',   6,    NULL),   -- Art. 70: after 6 months
 ('KW', 0, N'annual',    N'accrual',            NULL, N'monthly'),   -- Art. 70: 2.5 days a month
 ('KW', 3, N'hajj',      N'minServiceMonths',   24,   NULL),   -- Art. 76: after 2 years
 ('KW', 3, N'hajj',      N'maxTimesPerService', 1,    NULL),   -- once per career
 ('KW', 3, N'hajj',      N'religion',           NULL, N'Muslim'),
 ('SA', 5, N'marriage',  N'maxTimesPerService', 1,    NULL),   -- Art. 113: once
 ('SA', 6, N'hajj',      N'minServiceMonths',   24,   NULL),   -- Art. 114: after 2 years
 ('SA', 6, N'hajj',      N'maxTimesPerService', 1,    NULL),
 ('AE', 0, N'annual',    N'minServiceMonths',   6,    NULL),   -- Art. 29: from 6 months
 ('AE', 0, N'annual',    N'accrual',            NULL, N'monthly'),
 ('AE', 5, N'study',     N'minServiceMonths',   24,   NULL),   -- Art. 32: after 2 years
 ('EG', 3, N'maternity', N'maxTimesPerService', 3,    NULL),   -- up to 3 times in a career
 ('EG', 4, N'hajj',      N'minServiceMonths',   60,   NULL),   -- after 5 years
 ('EG', 4, N'hajj',      N'maxTimesPerService', 1,    NULL);

DECLARE @touched TABLE (ID int);
DECLARE @c char(2), @i int, @code nvarchar(20), @f nvarchar(40), @n int, @t nvarchar(20), @path nvarchar(100);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT Country, Idx, Code, Field, NumVal, TxtVal FROM @set;
OPEN cur; FETCH NEXT FROM cur INTO @c, @i, @code, @f, @n, @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @path = N'$.leave[' + CAST(@i AS nvarchar(5)) + N'].' + @f;
    UPDATE p SET RulesJson = CASE WHEN @n IS NOT NULL THEN JSON_MODIFY(p.RulesJson, @path, @n) ELSE JSON_MODIFY(p.RulesJson, @path, @t) END
    OUTPUT inserted.ID INTO @touched (ID)
      FROM dbo.HrRulePacks p
     WHERE p.CountryCode = @c
       AND JSON_VALUE(p.RulesJson, N'$.leave[' + CAST(@i AS nvarchar(5)) + N'].code') = @code
       AND JSON_VALUE(p.RulesJson, @path) IS NULL;
    FETCH NEXT FROM cur INTO @c, @i, @code, @f, @n, @t;
END
CLOSE cur; DEALLOCATE cur;

UPDATE dbo.HrRulePacks SET Version = Version + 1, UpdatedAt = SYSUTCDATETIME()
 WHERE CompanyID IS NOT NULL AND ID IN (SELECT DISTINCT ID FROM @touched);

COMMIT TRANSACTION;
