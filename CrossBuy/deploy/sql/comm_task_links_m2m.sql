-- =============================================================================================
-- Communication ↔ Tasks — MESSAGE-LEVEL LINKING. Slice comm_task_links_m2m.
--
-- WHAT THIS CHANGES AND WHY. comm_task_links.sql created ONE live-uniqueness rule:
--
--     UX_CommTaskLinks_Live  UNIQUE (CompanyID, TaskId, ThreadId) WHERE RemovedAt IS NULL
--
-- CommentId sat deliberately OUTSIDE that key, so a task could be attached to a conversation only
-- once however many messages mentioned it — provenance without duplicate relationships. The
-- business requirement has since changed: a task must be linkable to SEVERAL messages in the same
-- conversation, and each of those relationships must be removable on its own.
--
-- The old key cannot express that. It is replaced by TWO filtered unique indexes that keep the
-- original no-duplicates promise WITHIN each kind of link:
--
--     conversation-level  UNIQUE (CompanyID, TaskId, ThreadId)            WHERE RemovedAt IS NULL AND CommentId IS NULL
--     message-level       UNIQUE (CompanyID, TaskId, ThreadId, CommentId) WHERE RemovedAt IS NULL AND CommentId IS NOT NULL
--
-- So a task may hold at most one conversation-level link per thread, at most one link per message,
-- and the two coexist independently. A NULL CommentId is the conversation itself, which is why the
-- two filters partition the table rather than overlap: no row satisfies both.
--
-- NO COLUMN, TABLE OR CONSTRAINT CHANGES. CommTaskLink already carries everything needed. The FK to
-- CommThreads, the CK on Origin and both directional indexes are untouched.
--
-- NOTHING IS DELETED. No row is removed, rewritten or renumbered. The only destructive statement is
-- DROP INDEX, and it drops an index — guarded, and only after the replacement has been proven
-- creatable by the duplicate check below.
--
-- IT REFUSES RATHER THAN REPAIRS. If a live duplicate exists under either new key the script raises
-- and rolls back, because choosing which of two real links survives is a data decision for a human,
-- not a deployment step.
--
-- IDEMPOTENT. Safe to run repeatedly: each object is guarded by a sys-catalogue check, and a second
-- run finds the new indexes present and the old one gone, and does nothing.
--
-- RUN IT WITH sqlcmd -I  (QUOTED_IDENTIFIER ON). SQL Server refuses to create a FILTERED index when
-- QUOTED_IDENTIFIER is OFF, and all three indexes here are filtered.
-- =============================================================================================

SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    -- ---------------------------------------------------------------------------------------------
    -- 1. The table must exist and must be the one this slice was written against.
    -- ---------------------------------------------------------------------------------------------
    IF OBJECT_ID('dbo.CommTaskLinks', 'U') IS NULL
        THROW 51000, 'dbo.CommTaskLinks does not exist. Apply comm_task_links.sql first.', 1;

    IF COL_LENGTH('dbo.CommTaskLinks', 'CommentId') IS NULL
        THROW 51001, 'dbo.CommTaskLinks has no CommentId column; this is not the expected schema.', 1;

    -- ---------------------------------------------------------------------------------------------
    -- 2. REFUSE on live duplicates. Creating the new indexes would fail anyway; failing HERE says
    --    which rule was violated instead of surfacing an opaque index-build error.
    -- ---------------------------------------------------------------------------------------------
    IF EXISTS (
        SELECT 1 FROM dbo.CommTaskLinks
        WHERE RemovedAt IS NULL AND CommentId IS NULL
        GROUP BY CompanyID, TaskId, ThreadId HAVING COUNT(*) > 1)
        THROW 51002, 'Live duplicate conversation-level links exist; a human must decide which survives.', 1;

    IF EXISTS (
        SELECT 1 FROM dbo.CommTaskLinks
        WHERE RemovedAt IS NULL AND CommentId IS NOT NULL
        GROUP BY CompanyID, TaskId, ThreadId, CommentId HAVING COUNT(*) > 1)
        THROW 51003, 'Live duplicate message-level links exist; a human must decide which survives.', 1;

    -- ---------------------------------------------------------------------------------------------
    -- 3. The two replacements, created BEFORE the old index is dropped so a failure leaves the
    --    original rule intact when the transaction rolls back.
    -- ---------------------------------------------------------------------------------------------
    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'UX_CommTaskLinks_LiveConversation'
                     AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
    BEGIN
        CREATE UNIQUE INDEX UX_CommTaskLinks_LiveConversation
            ON dbo.CommTaskLinks (CompanyID, TaskId, ThreadId)
            WHERE RemovedAt IS NULL AND CommentId IS NULL;
        PRINT 'ADDED UX_CommTaskLinks_LiveConversation';
    END
    ELSE PRINT 'UX_CommTaskLinks_LiveConversation EXISTS';

    IF NOT EXISTS (SELECT 1 FROM sys.indexes
                   WHERE name = 'UX_CommTaskLinks_LiveMessage'
                     AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
    BEGIN
        CREATE UNIQUE INDEX UX_CommTaskLinks_LiveMessage
            ON dbo.CommTaskLinks (CompanyID, TaskId, ThreadId, CommentId)
            WHERE RemovedAt IS NULL AND CommentId IS NOT NULL;
        PRINT 'ADDED UX_CommTaskLinks_LiveMessage';
    END
    ELSE PRINT 'UX_CommTaskLinks_LiveMessage EXISTS';

    -- ---------------------------------------------------------------------------------------------
    -- 4. Only now retire the old rule. Guarded: a re-run finds it already gone.
    -- ---------------------------------------------------------------------------------------------
    IF EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_CommTaskLinks_Live'
                 AND object_id = OBJECT_ID('dbo.CommTaskLinks'))
    BEGIN
        DROP INDEX UX_CommTaskLinks_Live ON dbo.CommTaskLinks;
        PRINT 'DROPPED UX_CommTaskLinks_Live (superseded)';
    END
    ELSE PRINT 'UX_CommTaskLinks_Live ALREADY ABSENT';

    COMMIT TRANSACTION;
    PRINT 'comm_task_links_m2m COMMITTED';
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    PRINT 'comm_task_links_m2m ROLLED BACK — ' + ERROR_MESSAGE();
    THROW;
END CATCH
GO
