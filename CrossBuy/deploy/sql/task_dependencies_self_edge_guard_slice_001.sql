-- ==========================================================================================
-- Tasks dependencies — restore the self-edge invariant on existing installations.
--
-- The historical base creates CK_TaskDependencies_NotSelf only when it creates the table.
-- Existing installations can have the table without that constraint. This successor is guarded,
-- validates existing rows (WITH CHECK), and does not modify task data.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'dbo.TaskDependencies', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.check_constraints
       WHERE parent_object_id = OBJECT_ID(N'dbo.TaskDependencies')
         AND name = N'CK_TaskDependencies_NotSelf'
   )
BEGIN
    ALTER TABLE dbo.TaskDependencies WITH CHECK
        ADD CONSTRAINT CK_TaskDependencies_NotSelf
        CHECK (PredecessorTaskId <> SuccessorTaskId);
END
GO
