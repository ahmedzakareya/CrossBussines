-- ============================================================================
-- CALL ROUTING — the department a call, or the problem behind it, was sent to.
--
-- WHY: the call centre could record WHO called and WHAT happened, but not WHERE
-- the problem went next. An agent who cannot hand a problem to a department has
-- to do it outside the system — a phone call, a message — and the handover then
-- exists nowhere, so nobody can ask how long a department takes to pick a call up.
--
-- THE DEPARTMENT IS AN ORG NODE, not a new table. Hierarchicals already carries
-- the administrative bodies (H_Type = 3) under each company root
-- (H_Type = 1 AND H_ObjectID = <company>), which is the same anchor
-- AdministrativeStructureService.GetByCompanyAsync uses. A second list of
-- departments would be a second truth about the same org.
--
-- NO FOREIGN KEY, matching the rest of the CRM tables: a department can be
-- reorganised out of the tree while the call that was routed to it stays
-- readable as history.
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- SET QUOTED_IDENTIFIER ON PER BATCH, not once at the top: every GO starts a new batch with
-- the session default, and sqlcmd -i runs with it OFF - under which CREATE INDEX on a FILTERED
-- index fails outright. The slice then reports an error for the indexes while the columns land,
-- which reads like a half-applied slice and is really just the connection setting.

-- 1) The call records where it was sent.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'DepartmentId')
BEGIN
    ALTER TABLE dbo.CrmCalls ADD DepartmentId INT NULL;
END
GO

-- 2) And so does the ticket, because the problem outlives the call. A ticket
--    routed on the phone and a ticket routed from its own screen are the same
--    fact and belong in the same column.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'DepartmentId')
BEGIN
    ALTER TABLE dbo.CrmTickets ADD DepartmentId INT NULL;
END
GO

-- The read this column exists for: "what has been sent to this department".
SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'IX_CrmCalls_Company_Dept')
BEGIN
    CREATE INDEX IX_CrmCalls_Company_Dept ON dbo.CrmCalls (CompanyID, DepartmentId)
        WHERE DepartmentId IS NOT NULL;
END
GO

SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'IX_CrmTickets_Company_Dept')
BEGIN
    CREATE INDEX IX_CrmTickets_Company_Dept ON dbo.CrmTickets (CompanyID, DepartmentId)
        WHERE DepartmentId IS NOT NULL;
END
GO
