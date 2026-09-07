/* =============================================================================================
   TEST DATA FOR ONE TASK, FILLED OUT — /Tasks/Detail/29505 and /Tasks/Summary/29505
   ---------------------------------------------------------------------------------------------
   The dependency seed gives that task a title and a link and nothing else, so every other
   section of the detail and summary screens renders its empty state. This fills it, so each
   section can be SEEN with real shapes in it rather than argued about:

     description         long enough to wrap, in both languages
     estimate + progress so a "6.25 / 8" style ratio has both halves
     checklist           6 lines, part done, BOTH languages on every line
     time log            4 entries across 3 people and 3 days, Timer and Manual
     dependencies        already seeded (29505 blocks 29435) - not touched here

   NO VISIBLE TAG. The first version of this script put a 'ZZ-T29505-' marker in TitleEn and in
   the timesheet Description so it could find its own rows again - and both of those columns are
   RENDERED, so the English checklist read "ZZ-T29505-List the campaign line items" on screen.
   TaskChecklistItems has no column that is not displayed, so there is nowhere to hide a marker.
   Instead the script declares its rows ONCE in @Lines / @Time and both the delete and the insert
   read from that declaration - the same source of truth, and nothing on screen carries a tag.

   WHAT IT DOES NOT WRITE: comments. Those belong to the Communication platform, which owns its
   threads, its audit rows and its mention resolution. Hand-writing them here would create rows
   the platform never agreed to, with no audit entry to match. Post them through the app.

   EVERY LINE IS BILINGUAL, per the standing rule: a display column with one language cannot be
   fixed at render time.

   IDEMPOTENT, and SCOPED BY TASK ID on every statement. Nothing here uses a predicate that could
   reach another task's rows - that precaution is not theoretical: a blanket
   "WHERE Source='Timer' AND EndedAt IS NULL" written during this work destroyed a live timer
   belonging to somebody else.

   THE DATES ARE NEVER PINNED. Work dates hang off CAST(GETDATE() AS date), so the log still
   reads as "this week" whenever it runs.

   MUST BE RUN WITH -f 65001 or every Arabic string is stored double-encoded:
       sqlcmd -S localhost -d CrossBuyDev -E -f 65001 -i tools\testdata\task_29505_full.sql
   ============================================================================================= */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @Task int = 29505;

DECLARE @Co int, @Owner int;
SELECT @Co = CompanyId, @Owner = AssigneeEmployeeId FROM TaskItems WHERE ID = @Task;

IF @Co IS NULL
BEGIN
    RAISERROR(N'Task %d does not exist - nothing written.', 16, 1, @Task);
    RETURN;
END

/* ---- 1. THE ROWS THIS SCRIPT OWNS, declared once ---------------------------------------- */
DECLARE @Lines TABLE (SortOrder int, Ar nvarchar(400), En nvarchar(400), DoneHoursAgo int, DoneBy int);
INSERT INTO @Lines (SortOrder, Ar, En, DoneHoursAgo, DoneBy) VALUES
 (1, N'حصر بنود الحملة',               N'List the campaign line items',            72,   5),
 (2, N'تجميع عروض الأسعار',             N'Collect the quotations',                  48,  17),
 (3, N'مطابقة الأسعار بالعقود القائمة',  N'Match prices to standing contracts',      20,  18),
 (4, N'فصل البنود بدون سند سعري',        N'Separate lines with no price evidence', NULL, NULL),
 (5, N'مراجعة المدير المالي',            N'Finance manager review',               NULL, NULL),
 (6, N'رفع النسخة النهائية للاعتماد',    N'Submit the final version for approval', NULL, NULL);

DECLARE @Time TABLE (EmployeeId int, DaysAgo int, Hours decimal(9,2), Source nvarchar(20), Note nvarchar(400));
INSERT INTO @Time (EmployeeId, DaysAgo, Hours, Source, Note) VALUES
 ( 5, 3, 2.50, N'Timer',  N'مراجعة أولى لبنود الحملة'),
 (17, 2, 1.75, N'Timer',  N'متابعة عروض الموردين'),
 (18, 1, 0.75, N'Manual', N'مقارنة العقود - مُدخَل يدويًا'),
 ( 5, 0, 1.25, N'Timer',  N'تحرير قائمة الاستثناءات');

/* ---- 2. UNDO a previous run of THIS script, and only that ------------------------------- */
DELETE c FROM TaskChecklistItems c
WHERE c.TaskId = @Task AND EXISTS (SELECT 1 FROM @Lines l WHERE l.En = c.TitleEn);

DELETE e FROM TimesheetEntries e
WHERE e.TaskId = @Task AND EXISTS (SELECT 1 FROM @Time tm WHERE tm.Note = e.Description);

/* ---- 3. THE TASK ITSELF ------------------------------------------------------------------ */
UPDATE TaskItems
SET Description = N'مراجعة تقديرات الميزانية لكل بند من بنود حملة الإطلاق، والتأكد من أن كل رقم مسنود بعرض سعر أو عقد قائم قبل رفعها للاعتماد. البنود التي لا يوجد لها سند سعري تُرفَع منفصلة مع سبب واضح.'
                  + NCHAR(10) + NCHAR(10) +
                  N'Review the budget estimate for every line of the launch campaign and confirm each figure is backed by a quotation or a standing contract before it goes up for approval. Lines with no price evidence are raised separately, each with a stated reason.',
    EstimatedHours = 8,
    ProgressPct = 40
WHERE ID = @Task;

/* ---- 4. THE CHECKLIST, both languages on every line ------------------------------------- */
INSERT INTO TaskChecklistItems (CompanyId, TaskId, Title, TitleEn, IsDone, DoneAt, DoneByEmployeeId, SortOrder, CreatedAt, CreatedByEmployeeId)
SELECT @Co, @Task, l.Ar, l.En,
       CASE WHEN l.DoneHoursAgo IS NULL THEN 0 ELSE 1 END,
       CASE WHEN l.DoneHoursAgo IS NULL THEN NULL ELSE DATEADD(hour, -l.DoneHoursAgo, GETUTCDATE()) END,
       l.DoneBy, l.SortOrder, DATEADD(day, -4, GETUTCDATE()), @Owner
FROM @Lines l;

/* ---- 5. THE TIME LOG --------------------------------------------------------------------
   Three people, three days, and BOTH sources - a log with one shape in it proves the row
   renders and nothing about whether the screen distinguishes anything. A Manual entry carries
   no StartedAt, which is the case the timer path never produces. StartedAt/EndedAt are UTC
   because that is what the application writes; the screens pin the Kind before converting. */
INSERT INTO TimesheetEntries (CompanyId, TaskId, EmployeeId, WorkDate, Hours, Source, IsBillable, CreatedAt, StartedAt, EndedAt, Description)
SELECT @Co, @Task, tm.EmployeeId,
       DATEADD(day, -tm.DaysAgo, CAST(GETDATE() AS date)), tm.Hours, tm.Source, 0, GETDATE(),
       CASE WHEN tm.Source = N'Manual' THEN NULL
            ELSE DATEADD(day, -tm.DaysAgo, DATEADD(minute, -CAST(tm.Hours * 60 AS int), GETUTCDATE())) END,
       CASE WHEN tm.Source = N'Manual' THEN NULL
            ELSE DATEADD(day, -tm.DaysAgo, GETUTCDATE()) END,
       tm.Note
FROM @Time tm;

/* ---- 6. ActualHours, recomputed the way the application does it ------------------------- */
UPDATE TaskItems
SET ActualHours = (SELECT ISNULL(SUM(Hours), 0) FROM TimesheetEntries WHERE TaskId = @Task)
WHERE ID = @Task;

/* ---- 7. READ IT BACK -------------------------------------------------------------------
   The Arabic is reported as a CODEPOINT, not as glyphs: 1605 is a correctly stored meem, and
   anything in the 216 range means sqlcmd decoded this file as the console codepage. The console
   cannot show the difference, and no amount of looking at the screen fixes it afterwards. */
SELECT ID, TitleEn, EstimatedHours, ActualHours, ProgressPct,
       UNICODE(SUBSTRING(Description, 1, 1)) AS DescrFirstCp_1605_is_meem
FROM TaskItems WHERE ID = @Task;

SELECT COUNT(*) AS checklist_lines,
       SUM(CASE WHEN IsDone = 1 THEN 1 ELSE 0 END) AS done,
       SUM(CASE WHEN TitleEn IS NULL OR TitleEn = N'' THEN 1 ELSE 0 END) AS missing_english,
       SUM(CASE WHEN TitleEn LIKE N'ZZ%' OR Title LIKE N'ZZ%' THEN 1 ELSE 0 END) AS visible_tags
FROM TaskChecklistItems WHERE TaskId = @Task;

SELECT COUNT(*) AS time_entries, SUM(Hours) AS total_hours,
       COUNT(DISTINCT EmployeeId) AS people, COUNT(DISTINCT Source) AS sources,
       SUM(CASE WHEN Description LIKE N'ZZ%' THEN 1 ELSE 0 END) AS visible_tags
FROM TimesheetEntries WHERE TaskId = @Task;
