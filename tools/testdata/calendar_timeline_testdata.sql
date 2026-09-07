/* =============================================================================================
   TEST DATA FOR /Calendar/Timeline
   ---------------------------------------------------------------------------------------------
   This is NOT schema and does NOT belong in deploy/sql - it seeds a company-1 dev database with
   the cases the timeline screen has to render, so the grouping, the owner-join, the "reaches
   nobody" notice and the empty-branch badge can each be SEEN rather than argued about.

   IDEMPOTENT and REVERSIBLE. Every row it writes is tagged 'ZZ-TL-' in TitleEn - the same ZZ-
   convention the existing test branches already use (ZZ-CASHIER-BR, ZZ-SHIFT-BR). Re-running
   deletes and rewrites its own rows and touches nothing else. To remove the seed entirely, run
   section 1 on its own.

   WHAT IT DELIBERATELY DOES NOT DO: it does not reassign Employee.BranchID. The nine placed
   employees (5 Cairo / 3 Alexandria / 1 HYPER-DEMO) and the sixteen unplaced ones are real dev
   state, and the sixteen are test accounts (Dev*, UAT*, *Chat test*, Orders Test*) - so the
   "no branch on file" bucket is a case worth rendering, not a gap worth papering over.

   THE DATE IS NEVER PINNED. Everything hangs off CAST(GETDATE() AS date), so the seed is still
   about "today" whenever it is run - a literal here would make the screen empty tomorrow.

   MUST BE RUN WITH -f 65001. This file is UTF-8 and sqlcmd -i otherwise decodes it as the console
   ANSI codepage, which stores every Arabic string double-encoded - "تدريب" arrives as "ØªØ¯Ø±ÙŠØ¨".
   That is not a display bug and no amount of looking at the screen fixes it afterwards; the bytes
   in the column are wrong. Verified by reading the rows back, not by trusting the console.

       usage:  sqlcmd -S localhost -d CrossBuyDev -E -f 65001 -i tools/testdata/calendar_timeline_testdata.sql
   ============================================================================================= */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;   -- sqlcmd -i leaves this OFF, and these tables carry filtered indexes
GO

DECLARE @Company int = 1;
DECLARE @Day date = CAST(GETDATE() AS date);

/* StartAt / EndAt are stored as UTC INSTANTS - the screen converts with ToLocalTime() before it
   draws anything. Writing 09:00 into StartAt therefore put the booking on screen at 12:00 and
   pushed the last case clean off the 07-19 grid. Every time below is a LOCAL wall clock and this
   offset is what turns it into the instant to store. Measured, not assumed, and not a literal:
   a machine in another timezone must seed the same screen. */
DECLARE @Off int = DATEDIFF(minute, GETUTCDATE(), GETDATE());

/* ---- 1. REMOVE WHAT THIS SCRIPT WROTE LAST TIME ------------------------------------------- */
DECLARE @mine TABLE (Id int PRIMARY KEY);
INSERT INTO @mine (Id)
SELECT Id FROM dbo.CalendarEvents WHERE CompanyID = @Company AND TitleEn LIKE 'ZZ-TL-%';

DELETE FROM dbo.CalendarEventAttendees WHERE EventId IN (SELECT Id FROM @mine);
DELETE FROM dbo.CalendarEventSchedules WHERE EventId IN (SELECT Id FROM @mine);
DELETE FROM dbo.CalendarEvents         WHERE Id      IN (SELECT Id FROM @mine);

/* ---- 2. THE PEOPLE THE CASES HANG ON ------------------------------------------------------
   Resolved by BRANCH rather than by id, so the seed keeps working if the demo employees are
   renumbered - an id literal here is the same mistake as a date literal. */
DECLARE @cairo1 int = (SELECT MIN(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 1 AND BranchID = 15);
DECLARE @cairo2 int = (SELECT MAX(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 1 AND BranchID = 15);
DECLARE @alex1  int = (SELECT MIN(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 1 AND BranchID = 16);
DECLARE @alex2  int = (SELECT MAX(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 1 AND BranchID = 16);
DECLARE @nobr   int = (SELECT MIN(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 1 AND BranchID IS NULL);

/* An owner who is NOT an active employee here - this is the whole point of case (f). Falls back
   to a deliberately impossible id if every employee in this company happens to be active. */
DECLARE @ghost int = ISNULL((SELECT MIN(ID) FROM dbo.Employee WHERE EmpCompanyID = @Company AND IsActive = 0), 999999);

IF @cairo1 IS NULL OR @alex1 IS NULL
BEGIN
    RAISERROR('Company 1 has no active employee in branch 15 or 16 - seed the demo employees first.', 16, 1);
    RETURN;
END

DECLARE @ids TABLE (Tag varchar(20), Id int);

/* ---- 3. THE CASES -------------------------------------------------------------------------
   Every row is bilingual - Title/TitleEn, Description/DescriptionEn, Location/LocationEn. Test
   data that carried Arabic alone would reproduce, in the fixture, the exact defect this screen
   was just fixed for, and the fixture would then hide the regression instead of catching it. */

/* (a) TWO BRANCHES BUSY AT ONCE. Cairo 09:00-10:30 against Alexandria 09:30-11:00, overlapping,
       so two different groups are occupied in the same columns - the fact a flat list of every
       employee in the company could not show. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Description, DescriptionEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'cairo-mtg', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- اجتماع فرع القاهرة', N'ZZ-TL- Cairo branch meeting',
        N'بيانات اختبار لشاشة الجدول الزمني', N'Test data for the timeline screen',
        N'قاعة الاجتماعات - الدور الثالث', N'Meeting room - 3rd floor',
        0, DATEADD(minute,  540 - @Off, CAST(@Day AS datetime2)), DATEADD(minute,  630 - @Off, CAST(@Day AS datetime2)),
        'Company', @cairo1, @cairo1, SYSUTCDATETIME());

INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Description, DescriptionEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'alex-mtg', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- جرد فرع الإسكندرية', N'ZZ-TL- Alexandria stock count',
        N'بيانات اختبار لشاشة الجدول الزمني', N'Test data for the timeline screen',
        N'المستودع الرئيسي', N'Main warehouse',
        0, DATEADD(minute,  570 - @Off, CAST(@Day AS datetime2)), DATEADD(minute,  660 - @Off, CAST(@Day AS datetime2)),
        'Company', @alex1, @cairo1, SYSUTCDATETIME());

/* (b) OWNER ONLY, NO ATTENDEES. The case the attendee-only join used to lose entirely: this must
       show its owner as busy in the grid AND read "Nobody" in the participants column. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'owner-only', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- مكالمة عميل (المالك فقط)', N'ZZ-TL- Client call (owner only)',
        N'مكتب المدير', N'Manager office',
        0, DATEADD(minute,  780 - @Off, CAST(@Day AS datetime2)), DATEADD(minute,  810 - @Off, CAST(@Day AS datetime2)),
        'Personal', @alex2, @cairo1, SYSUTCDATETIME());

/* (c) CROSS-BRANCH ATTENDEES. One booking that lands in three groups at once - Cairo, Alexandria
       and the unplaced bucket - so the participants badge reads 4 and four rows in three separate
       groups light up in the same hour. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'cross', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- مراجعة الأداء الشهرية', N'ZZ-TL- Monthly performance review',
        N'أونلاين - Teams', N'Online - Teams',
        0, DATEADD(minute,  900 - @Off, CAST(@Day AS datetime2)), DATEADD(minute,  960 - @Off, CAST(@Day AS datetime2)),
        'Company', @cairo1, @cairo1, SYSUTCDATETIME());

/* (d) ALL DAY. Must read "All day" in the bookings list instead of a time range, and occupy every
       column of its owner's row. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'allday', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- تدريب (يوم كامل)', N'ZZ-TL- Training (all day)',
        N'مركز التدريب', N'Training centre',
        /* NOT shifted by @Off: AllDay = 1 means the clock is never read, and pushing a whole-day
           marker three hours back would spill it onto the previous day. */
        1, CAST(@Day AS datetime2), DATEADD(day, 1, CAST(@Day AS datetime2)),
        'Company', @cairo2, @cairo1, SYSUTCDATETIME());

/* (e) REPEATING. A daily 08:00 stand-up that STARTED A WEEK AGO, so today's row is produced by the
       expansion path and not by the event's own StartAt - which is what makes IsRepeat true and
       gives the arrow marker and its "Repeating" tooltip something real to render. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'standup', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- الاجتماع اليومي', N'ZZ-TL- Daily stand-up',
        N'أونلاين', N'Online',
        0, DATEADD(minute,  480 - @Off, CAST(DATEADD(day, -7, @Day) AS datetime2)),
           DATEADD(minute,  495 - @Off, CAST(DATEADD(day, -7, @Day) AS datetime2)),
        'Company', @cairo1, @cairo1, SYSUTCDATETIME());

INSERT INTO dbo.CalendarEventSchedules
    (CompanyId, EventId, TimeZoneId, RecurrenceKind, Interval, UntilLocalDate, CreatedAt, CreatedByEmployeeId)
SELECT @Company, Id, 'Asia/Riyadh', 'Daily', 1, DATEADD(day, 30, @Day), SYSUTCDATETIME(), @cairo1
FROM @ids WHERE Tag = 'standup';

/* (f) REACHES NOBODY. Real, counted in the header, and present in no row of the grid - the case
       the blue notice exists to name. No attendees, and an owner who is not an active employee of
       this company. Before that notice this could only be discovered as a mismatch between a
       count and an empty-looking grid. */
INSERT INTO dbo.CalendarEvents
    (CompanyID, Title, TitleEn, Location, LocationEn, AllDay, StartAt, EndAt, Scope, OwnerEmpId, CreatedBy, CreatedAt)
OUTPUT 'orphan', INSERTED.Id INTO @ids
VALUES (@Company,
        N'ZZ-TL- موعد بلا مشاركين', N'ZZ-TL- Booking that reaches nobody',
        N'غير محدد', N'Unspecified',
        0, DATEADD(minute, 1020 - @Off, CAST(@Day AS datetime2)), DATEADD(minute, 1080 - @Off, CAST(@Day AS datetime2)),
        'Company', @ghost, @cairo1, SYSUTCDATETIME());

/* ---- 4. THE ATTENDEES ---------------------------------------------------------------------
   The owner is a participant by virtue of owning the event - that is the service-level rule this
   pass introduced - so no duplicate attendee row is written for an owner. */
INSERT INTO dbo.CalendarEventAttendees (EventId, EmployeeId)
SELECT i.Id, x.EmployeeId
FROM @ids i
CROSS APPLY (VALUES
    ('cairo-mtg', @cairo1), ('cairo-mtg', @cairo2),
    ('alex-mtg',  @alex1),  ('alex-mtg',  @alex2),
    ('cross',     @cairo1), ('cross', @cairo2), ('cross', @alex1), ('cross', @nobr)
) AS x (Tag, EmployeeId)
WHERE x.Tag = i.Tag
  AND x.EmployeeId IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM dbo.CalendarEvents e WHERE e.Id = i.Id AND e.OwnerEmpId = x.EmployeeId);

/* ---- 5. THE ROOMS AND VEHICLES ------------------------------------------------------------
   /Calendar/ResourceView draws the same day against CalendarResources, and it was rendering eleven
   rows of empty dots for the same reason the timeline did: 37 event-resource links existed and not
   one of them belonged to an event on today. Bookings that already name a place in their Location
   text are pointed at a real resource row, so the two screens agree.

   The last pair is a DELIBERATE DOUBLE BOOKING - two overlapping events on one room - because the
   grid has a warning marker for exactly that and nothing in the data ever triggered it. A legend
   that explains a symbol no row can produce is not evidence that the symbol works. */
DECLARE @room  int = (SELECT MIN(ID) FROM dbo.CalendarResources WHERE CompanyId = @Company AND IsActive = 1 AND Kind = 'Room');
DECLARE @room2 int = (SELECT MAX(ID) FROM dbo.CalendarResources WHERE CompanyId = @Company AND IsActive = 1 AND Kind = 'Room');
DECLARE @van   int = (SELECT MIN(ID) FROM dbo.CalendarResources WHERE CompanyId = @Company AND IsActive = 1 AND Kind = 'Vehicle');

DELETE FROM dbo.CalendarEventResources WHERE EventId IN (SELECT Id FROM @ids);

INSERT INTO dbo.CalendarEventResources (CompanyId, EventId, ResourceId)
SELECT @Company, i.Id, x.ResourceId
FROM @ids i
CROSS APPLY (VALUES
    ('cairo-mtg',  @room),    -- "Meeting room - 3rd floor"
    ('alex-mtg',   @van),     -- the stock count needs the van
    ('cross',      @room2),   -- the review sits in a second room
    ('allday',     @room2),   -- ... and the all-day training sits in the SAME room: the ⚠ case
    ('standup',    @room)
) AS x (Tag, ResourceId)
WHERE x.Tag = i.Tag AND x.ResourceId IS NOT NULL;

/* ---- 6. WHAT WAS WRITTEN ------------------------------------------------------------------ */
SELECT e.Id,
       e.TitleEn,
       e.OwnerEmpId,
       CONVERT(varchar(16), e.StartAt, 120) AS StartsAt,
       e.AllDay,
       (SELECT COUNT(*) FROM dbo.CalendarEventAttendees a WHERE a.EventId = e.Id) AS Attendees,
       (SELECT COUNT(*) FROM dbo.CalendarEventResources x WHERE x.EventId = e.Id) AS Resources
FROM dbo.CalendarEvents e
JOIN @ids i ON i.Id = e.Id
ORDER BY e.StartAt;
GO
