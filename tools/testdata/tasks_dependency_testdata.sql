/* =============================================================================================
   TEST DATA FOR THE DEPENDENCIES TAB ON /Tasks/Detail
   ---------------------------------------------------------------------------------------------
   This is NOT schema and does NOT belong in deploy/sql - it seeds a company-1 dev database with
   the three cases the pane has to render, so each one can be SEEN rather than argued about:

     1. a PREDECESSOR that is still open      -> "تنتظر" + the "حاجبة" badge
     2. a PREDECESSOR that is finished        -> "تنتظر" with NO badge
     3. a SUCCESSOR                            -> "يعتمد عليها", the arrow the other way

   Case 1 and 2 differ ONLY in the predecessor's Status, which is the point: IsBlocking is
   DERIVED by TaskDependencyService from whether the predecessor is Done, not stored on the link.
   Seeding two links with the same shape and different predecessor states is what proves that.

   IDEMPOTENT and REVERSIBLE. Every task it writes is tagged 'ZZ-DEP-' at the start of
   Description - the same ZZ- convention the existing seeds use (ZZ-TL-, ZZ-CASHIER-BR). The
   links are found through those tasks, so re-running deletes and rewrites its own rows and
   touches nothing else. To remove the seed entirely, run section 1 on its own.

   EVERY SEEDED TASK CARRIES BOTH TITLES. TaskItems.TitleEn exists and is populated for 528 of
   534 real rows; seeding Arabic only would have made this data the odd one out and would have
   made the Dependencies tab look broken on the English page when it was the seed that was.

   The TITLES are readable Arabic rather than the tag, because this data is looked at: a pane
   full of "ZZ-DEP-1" proves the markup renders and nothing about whether it reads correctly in
   an RTL column. The tag lives in Description, where the machine needs it and no screen shows it.

   THE DATE IS NEVER PINNED. Due dates hang off CAST(GETDATE() AS date), so an open predecessor
   is still open whenever this is run - a literal would make case 1 stop blocking next month and
   the pane would quietly lose its badge.

   THE ANCHOR TASK IS A PARAMETER, not a literal. @AnchorTaskId defaults to 29435 because that is
   the task the screen was built against, but it is declared once at the top so this seeds any
   task without a search-and-replace through the file.

   MUST BE RUN WITH -f 65001. This file is UTF-8 and sqlcmd -i otherwise decodes it as the console
   ANSI codepage, which stores every Arabic string double-encoded - "تصميم" arrives as
   "ØªØµÙ…ÙŠÙ…". That is not a display bug and looking at the screen afterwards does not fix it;
   the bytes in the column are wrong. Verify by reading the rows back with UNICODE(SUBSTRING(...)),
   never by trusting the console.

       sqlcmd -S localhost -d CrossBuyDev -E -f 65001 -i tools\testdata\tasks_dependency_testdata.sql

   ============================================================================================= */
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;

DECLARE @AnchorTaskId int = 29435;
DECLARE @Tag nvarchar(20) = N'ZZ-DEP-';

DECLARE @CompanyId int, @Assignee int, @Creator int;
SELECT @CompanyId = CompanyId, @Assignee = AssigneeEmployeeId, @Creator = CreatedByEmployeeId
FROM TaskItems WHERE ID = @AnchorTaskId;

IF @CompanyId IS NULL
BEGIN
    -- Refuse rather than seed into the wrong company. A missing anchor means this database is not
    -- the one the seed was written for, and inventing a company here would put test rows in front
    -- of somebody else's data.
    RAISERROR(N'Anchor task %d does not exist - nothing seeded.', 16, 1, @AnchorTaskId);
    RETURN;
END

/* ---- 1. REMOVE what a previous run of THIS seed wrote --------------------------------------
   The links go first: they reference the tasks, and deleting a task with a link still pointing at
   it would either fail on the constraint or leave an orphan the pane cannot render. */
DELETE d
FROM TaskDependencies d
WHERE EXISTS (SELECT 1 FROM TaskItems t
              WHERE t.Description LIKE @Tag + N'%'
                AND (t.ID = d.PredecessorTaskId OR t.ID = d.SuccessorTaskId));

DELETE FROM TaskItems WHERE Description LIKE @Tag + N'%';

/* ---- 2. THE THREE NEIGHBOURS ----------------------------------------------------------------
   ProgressPct and Status are kept consistent with each other. A task marked Done at 40% would be
   test data that contradicts itself, and the first person to read the pane would rightly distrust
   everything else in it. */
DECLARE @OpenPred int, @DonePred int, @Succ int;

INSERT INTO TaskItems (CompanyId, Title, TitleEn, Description, AssigneeEmployeeId, CreatedByEmployeeId,
                       Priority, Status, DueDate, ActualHours, CreatedAt, IsBillable, ProgressPct, IsScheduled)
VALUES (@CompanyId, N'اعتماد الميزانية', N'Budget approval', @Tag + N'open predecessor - still blocking',
        @Assignee, @Creator, N'High', N'InProgress',
        DATEADD(day, 3, CAST(GETDATE() AS date)), 0, GETDATE(), 0, 40, 0);
SET @OpenPred = SCOPE_IDENTITY();

INSERT INTO TaskItems (CompanyId, Title, TitleEn, Description, AssigneeEmployeeId, CreatedByEmployeeId,
                       Priority, Status, DueDate, ActualHours, CreatedAt, IsBillable, ProgressPct, IsScheduled)
VALUES (@CompanyId, N'تجهيز قائمة الموردين', N'Prepare the supplier list', @Tag + N'finished predecessor - must NOT block',
        @Assignee, @Creator, N'Medium', N'Done',
        DATEADD(day, -2, CAST(GETDATE() AS date)), 4, GETDATE(), 0, 100, 0);
SET @DonePred = SCOPE_IDENTITY();

INSERT INTO TaskItems (CompanyId, Title, TitleEn, Description, AssigneeEmployeeId, CreatedByEmployeeId,
                       Priority, Status, DueDate, ActualHours, CreatedAt, IsBillable, ProgressPct, IsScheduled)
VALUES (@CompanyId, N'إطلاق الحملة الإعلانية', N'Launch the ad campaign', @Tag + N'successor - depends on the anchor',
        @Assignee, @Creator, N'High', N'New',
        DATEADD(day, 10, CAST(GETDATE() AS date)), 0, GETDATE(), 0, 0, 0);
SET @Succ = SCOPE_IDENTITY();

/* ---- 3. THE LINKS ---------------------------------------------------------------------------
   FinishToStart with no lag, which is the only kind the pane's composer creates today. */
INSERT INTO TaskDependencies (CompanyId, PredecessorTaskId, SuccessorTaskId, Kind, LagDays, CreatedAt, CreatedByEmployeeId)
VALUES (@CompanyId, @OpenPred,      @AnchorTaskId, N'FinishToStart', 0, GETDATE(), @Creator),
       (@CompanyId, @DonePred,      @AnchorTaskId, N'FinishToStart', 0, GETDATE(), @Creator),
       (@CompanyId, @AnchorTaskId,  @Succ,         N'FinishToStart', 0, GETDATE(), @Creator);

/* ---- 4. READ IT BACK ------------------------------------------------------------------------
   The seed reports what it wrote, and reports the ARABIC as a codepoint rather than as glyphs:
   UNICODE() of the first character is 1575 for a correctly stored alef and something in the 216
   range when the file was decoded as ANSI. The console cannot be trusted to show the difference. */
SELECT t.ID, t.Title, t.TitleEn, t.Status, t.ProgressPct,
       UNICODE(SUBSTRING(t.Title, 1, 1)) AS FirstCodepoint_1575_is_correct,
       CASE WHEN d.SuccessorTaskId = @AnchorTaskId THEN N'predecessor' ELSE N'successor' END AS Role,
       CASE WHEN d.SuccessorTaskId = @AnchorTaskId AND t.Status <> N'Done'
            THEN N'expects the BLOCKING badge' ELSE N'expects no badge' END AS Expectation
FROM TaskItems t
JOIN TaskDependencies d ON d.PredecessorTaskId = t.ID OR d.SuccessorTaskId = t.ID
WHERE t.Description LIKE @Tag + N'%'
  AND (d.PredecessorTaskId = @AnchorTaskId OR d.SuccessorTaskId = @AnchorTaskId)
ORDER BY t.ID;
