-- =================================================================================================
-- A month of attendance for the ESS "My attendance" screen (People/Attendance).
--
-- WRITTEN AGAINST THE SERVICE'S OWN QUERY, not a guess at the screen:
--
--   AttendanceService.ForMonthAsync
--       CompanyID = @company AND EmployeeID = @me AND WorkDate BETWEEN first AND last of month
--
--   MonthlySummaryAsync counts a day as attended only when
--       Status = 'Present' OR Status = 'Late'
--   (AttendanceService.cs: `r.Status == "Present" || r.Status == "Late"`)
--
-- So the Status STRING is what drives the four tiles. A row with any other status is a row the
-- summary will not count as present - which is exactly how the screen currently reads 22 absent.
--
-- WEEKENDS ARE SKIPPED. Friday and Saturday get no row at all, because a weekend is not an
-- absence and seeding one would make the "absent" tile lie.
--
-- ADDITIVE AND REVERSIBLE. INSERT only, and only for days that have no row yet - so re-running it
-- cannot duplicate a day or overwrite a real record. Every row is marked in Notes, and the
-- teardown at the bottom removes exactly those.
--
--   sqlcmd -S localhost -d CrossBuyDev -E -C -f 65001 -i tools/testdata/attendance_month_testdata.sql
-- =================================================================================================
-- sqlcmd -i runs with QUOTED_IDENTIFIER OFF and these tables carry filtered indexes, so without
-- this line every INSERT is rejected. It must come first in the batch.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @marker  nvarchar(40) = N'[ZZ-ATT-SEED]';
DECLARE @me      int;
DECLARE @company int;

-- The signed-in ESS user. Resolved from the login, not hardcoded.
SELECT TOP 1 @me = e.ID, @company = e.EmpCompanyID
FROM Employee e JOIN AspNetUsers u ON u.Id = e.UserId
WHERE u.UserName = N'admin';

IF @me IS NULL
BEGIN
    RAISERROR('Could not resolve the admin employee; nothing seeded.', 16, 1);
    RETURN;
END

-- The month the screen is showing.
DECLARE @year int = 2026, @month int = 9;
DECLARE @first date = DATEFROMPARTS(@year, @month, 1);
DECLARE @last  date = EOMONTH(@first);

BEGIN TRAN;

DECLARE @d date = @first;
DECLARE @made int = 0, @late int = 0, @absent int = 0;

WHILE @d <= @last
BEGIN
    -- DATEPART(WEEKDAY) depends on DATEFIRST, so the day name is derived instead - a seed whose
    -- weekends move with a server setting is a seed that reports different numbers per machine.
    DECLARE @dow nvarchar(10) = DATENAME(WEEKDAY, @d);
    DECLARE @isWeekend bit = CASE WHEN @dow IN (N'Friday', N'Saturday') THEN 1 ELSE 0 END;

    -- Only days in the past or today: a future working day is not yet an attendance fact.
    IF @isWeekend = 0 AND @d <= CAST(SYSUTCDATETIME() AS date)
       AND NOT EXISTS (SELECT 1 FROM AttendanceRecords
                       WHERE CompanyID = @company AND EmployeeID = @me AND WorkDate = @d)
    BEGIN
        DECLARE @day int = DAY(@d);

        -- KEYED TO THE WORKING-DAY SEQUENCE, not to the calendar day. Keying it to DAY(@d) put the
        -- late and absent cases on a Friday and a Saturday, which are skipped - so the first run
        -- produced five identical Present rows and proved only that the screen renders. Every 4th
        -- working day is late and every 7th is absent, so even a five-day window shows all three.
        DECLARE @kind nvarchar(12) =
            CASE
                WHEN (@made + 1) % 7 = 0 THEN N'Absent'
                WHEN (@made + 1) % 4 = 0 THEN N'Late'
                ELSE N'Present'
            END;

        DECLARE @lateMin int = CASE WHEN @kind = N'Late' THEN 12 + (@made % 3) * 9 ELSE 0 END;
        DECLARE @inAt  datetime2 = DATEADD(MINUTE, 9 * 60 + @lateMin, CAST(@d AS datetime2));
        DECLARE @outAt datetime2 = DATEADD(MINUTE, 17 * 60 + CASE WHEN (@made + 1) % 5 = 0 THEN 50 ELSE 5 END, CAST(@d AS datetime2));
        DECLARE @otMin int = CASE WHEN (@made + 1) % 5 = 0 THEN 45 ELSE 0 END;
        DECLARE @worked decimal(9,2) = CAST(DATEDIFF(MINUTE, @inAt, @outAt) AS decimal(9,2)) / 60.0;

        INSERT INTO AttendanceRecords
            (CompanyID, EmployeeID, WorkDate, CheckIn, CheckOut, Source,
             LateMinutes, EarlyLeaveMinutes, OvertimeMinutes, WorkedHours, Status, Notes, CreatedBy, CreatedAt)
        VALUES
            (@company, @me, @d,
             CASE WHEN @kind = N'Absent' THEN NULL ELSE @inAt  END,
             CASE WHEN @kind = N'Absent' THEN NULL ELSE @outAt END,
             N'Seed',
             @lateMin, 0,
             CASE WHEN @kind = N'Absent' THEN 0 ELSE @otMin END, 0,
             @kind,
             CASE WHEN @kind = N'Absent' THEN N'غياب دون إشعار ' + @marker
                  WHEN @kind = N'Late'   THEN N'تأخير صباحي ' + @marker
                  ELSE @marker END,
             N'seed', SYSUTCDATETIME());

        -- WorkedHours is set in a second pass because the column is NOT NULL and the expression
        -- above needs the row's own CheckIn/CheckOut, which SQL Server will not read mid-INSERT.
        UPDATE AttendanceRecords
        SET WorkedHours = CASE WHEN Status = N'Absent' THEN 0
                               ELSE CAST(DATEDIFF(MINUTE, CheckIn, CheckOut) AS decimal(9,2)) / 60.0 END
        WHERE CompanyID = @company AND EmployeeID = @me AND WorkDate = @d;

        SET @made = @made + 1;
        IF @kind = N'Late'   SET @late   = @late + 1;
        IF @kind = N'Absent' SET @absent = @absent + 1;
    END

    SET @d = DATEADD(DAY, 1, @d);
END

COMMIT;

SELECT CAST(@made AS varchar) + N' day(s) seeded for employee ' + CAST(@me AS varchar)
     + N' in ' + CAST(@month AS varchar) + N'/' + CAST(@year AS varchar)
     + N'  (late: ' + CAST(@late AS varchar) + N', absent: ' + CAST(@absent AS varchar)
     + N', present: ' + CAST(@made - @late - @absent AS varchar) + N')' AS result;

-- =================================================================================================
-- TEARDOWN - removes exactly what this script created.
--
--   DELETE FROM AttendanceRecords WHERE Source = N'Seed' AND Notes LIKE N'%[[]ZZ-ATT-SEED]%';
--
-- The marker rides in Notes. That column CAN be shown, so it is bracketed and at the end to read
-- as an obvious test tag - this table has no unused column to hide it in, and a seed nobody can
-- identify and remove is worse than a visible tag.
-- =================================================================================================
