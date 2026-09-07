-- =================================================================================================
-- Mentions for the Workspace Mentions screen, so its design can be seen with rows in it.
--
-- WHY IT NEEDS SEEDING AT ALL. The screen was empty, and not by accident: all four existing
-- mentions were written BY employee 5 (admin), and CommMentions row 2 targets employee 5 itself
-- and has NO CommMentionRecipients row. That is the self-exclusion rule working exactly as
-- designed - you are never notified of your own mention - so admin could never see this screen
-- populated no matter how many comments admin wrote.
--
-- So every comment below is authored by SOMEBODY ELSE (17, 18, 19) and mentions employee 5.
--
-- ADDITIVE AND REVERSIBLE. It INSERTs only. No existing row is updated or deleted, and every row
-- it creates is stamped with the marker below so the teardown at the bottom can remove exactly
-- what this script made and nothing else.
--
-- The marker lives in DedupKey - a column this screen never renders - and NOT in Body or any
-- title. A previous seed of mine put its tag in a displayed column and the tag appeared on screen.
--
--   sqlcmd -S localhost -d CrossBuyDev -E -C -f 65001 -i tools/testdata/workspace_mentions_testdata.sql
-- =================================================================================================
-- sqlcmd -i runs with QUOTED_IDENTIFIER OFF, and these tables carry filtered indexes, so every
-- INSERT is rejected without this line. It must come before anything else in the batch.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

-- DedupKey carries a UNIQUE filtered index, so the marker is a PREFIX and each row appends its
-- own number. The teardown matches the prefix with LIKE.
DECLARE @marker  nvarchar(40) = N'ZZ-WSMENTION-SEED';
DECLARE @company int = 1;
DECLARE @me      int = 5;          -- admin: the RECIPIENT, never the author
DECLARE @now     datetime2 = SYSUTCDATETIME();

-- The peers who will do the mentioning. Anyone but @me.
DECLARE @peers TABLE (rn int IDENTITY(1,1), EmployeeId int);
INSERT INTO @peers (EmployeeId)
SELECT TOP 3 ID FROM Employee
WHERE EmpCompanyID = @company AND IsActive = 1 AND UserId IS NOT NULL AND ID <> @me
ORDER BY ID;

IF (SELECT COUNT(*) FROM @peers) = 0
BEGIN
    RAISERROR('No peer employee exists to author a mention; nothing seeded.', 16, 1);
    RETURN;
END

-- Threads to hang them on: existing Task discussions, so no new entity is invented.
DECLARE @threads TABLE (rn int IDENTITY(1,1), ThreadId bigint, EntityType nvarchar(64), EntityId int);
INSERT INTO @threads (ThreadId, EntityType, EntityId)
SELECT TOP 4 t.Id, t.EntityType, t.EntityId
FROM CommThreads t
WHERE t.CompanyID = @company AND t.EntityType = N'Task' AND t.DeletedAt IS NULL
ORDER BY t.Id DESC;

IF (SELECT COUNT(*) FROM @threads) = 0
BEGIN
    RAISERROR('No Task discussion thread exists to attach a mention to; nothing seeded.', 16, 1);
    RETURN;
END

-- The rows to create: body text, how long ago, and whether it has been read. A mixed read/unread
-- set matters - the screen styles them differently and an all-unread list proves only half of it.
DECLARE @rows TABLE (
    rn         int IDENTITY(1,1),
    Body       nvarchar(400),
    AgeMinutes int,
    IsRead     bit
);
INSERT INTO @rows (Body, AgeMinutes, IsRead) VALUES
 (N'@[أحمد زكريا](employee:5) راجع كشف المورد قبل ما نقفل الشهر لو سمحت.',              35,     0),
 (N'@[أحمد زكريا](employee:5) الجرد اتأخر يومين — محتاج موافقتك على التمديد.',          190,    0),
 (N'@[أحمد زكريا](employee:5) عدّلت قائمة الأسعار للفرع الجديد، تحب تراجعها؟',          1500,   0),
 (N'@[أحمد زكريا](employee:5) شكرًا على المراجعة، قفلت البند.',                          4400,   1),
 (N'@[أحمد زكريا](employee:5) فاتورة الكهرباء محتاجة اعتماد قبل بكرة.',                  8800,   1);

-- =================================================================================================
-- Insert: one comment, one mention, one recipient per row. Thread and peer rotate.
-- =================================================================================================
BEGIN TRAN;

-- ORDER MATTERS HERE. GetHistoryAsync sorts by recipient row id DESC, not by timestamp - its
-- cursor paging needs a monotonic key. In production ids ascend with time so the two agree, but a
-- seed that backdates rows only agrees if it INSERTS OLDEST FIRST. Walking the list backwards
-- gives the oldest row the lowest id, so descending id puts the newest mention at the top.
DECLARE @rn int = (SELECT COUNT(*) FROM @rows), @stop int = 1;
DECLARE @nPeers int = (SELECT COUNT(*) FROM @peers);
DECLARE @nThreads int = (SELECT COUNT(*) FROM @threads);
DECLARE @made int = 0;

WHILE @rn >= @stop
BEGIN
    DECLARE @body nvarchar(400), @age int, @isRead bit;
    SELECT @body = Body, @age = AgeMinutes, @isRead = IsRead FROM @rows WHERE rn = @rn;

    DECLARE @author int  = (SELECT EmployeeId FROM @peers   WHERE rn = ((@rn - 1) % @nPeers) + 1);
    DECLARE @thread bigint, @etype nvarchar(64), @eid int;
    SELECT @thread = ThreadId, @etype = EntityType, @eid = EntityId
      FROM @threads WHERE rn = ((@rn - 1) % @nThreads) + 1;

    DECLARE @at datetime2 = DATEADD(MINUTE, -@age, @now);

    INSERT INTO CommComments
        (CompanyID, ThreadId, EntityType, EntityId, ParentCommentId, Depth, Body, BodyFormat,
         Visibility, AuthorEmployeeId, RevisionCount, MentionCount, AttachmentCount, ReactionCount,
         ReplyCount, DedupKey, CreatedBy, CreatedAt)
    VALUES
        (@company, @thread, @etype, @eid, NULL, 1, @body, N'Markdown',
         N'Internal', @author, 0, 1, 0, 0,
         0, @marker + N'-' + CAST(@rn AS nvarchar(8)), @author, @at);

    DECLARE @commentId bigint = SCOPE_IDENTITY();

    INSERT INTO CommMentions
        (CompanyID, ThreadId, CommentId, EntityType, EntityId, TargetKind, TargetId, TargetKey,
         LabelAr, LabelEn, ResolvedRecipientCount, MentionedByEmployeeId, CreatedBy, CreatedAt)
    VALUES
        (@company, @thread, @commentId, @etype, @eid, N'Employee', @me, NULL,
         N'أحمد زكريا', N'Ahmed Zakareya', 1, @author, @author, @at);

    DECLARE @mentionId bigint = SCOPE_IDENTITY();

    INSERT INTO CommMentionRecipients
        (CompanyID, MentionId, CommentId, ThreadId, EmployeeId, ViaKind, ReadAt, CreatedBy, CreatedAt)
    VALUES
        (@company, @mentionId, @commentId, @thread, @me, N'Employee',
         CASE WHEN @isRead = 1 THEN DATEADD(MINUTE, -(@age / 2), @now) ELSE NULL END,
         @author, @at);

    SET @made = @made + 1;
    SET @rn = @rn - 1;
END

COMMIT;

SELECT CAST(@made AS varchar) + N' mention(s) seeded for employee ' + CAST(@me AS varchar)
     + N' (' + CAST((SELECT COUNT(*) FROM CommMentionRecipients WHERE EmployeeId = @me AND ReadAt IS NULL) AS varchar)
     + N' unread)' AS result;

-- =================================================================================================
-- TEARDOWN - removes exactly what this script created, matched on the marker. Children first.
--
--   DELETE r FROM CommMentionRecipients r
--     JOIN CommComments c ON c.Id = r.CommentId WHERE c.DedupKey LIKE N'ZZ-WSMENTION-SEED%';
--   DELETE m FROM CommMentions m
--     JOIN CommComments c ON c.Id = m.CommentId WHERE c.DedupKey LIKE N'ZZ-WSMENTION-SEED%';
--   DELETE FROM CommComments WHERE DedupKey LIKE N'ZZ-WSMENTION-SEED%';
-- =================================================================================================
