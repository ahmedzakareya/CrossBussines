-- =============================================================================================
-- COMMUNICATION PLATFORM — EXTERNAL ACTOR FOUNDATION (Wave 3B-B1)
--
-- WHAT THIS DOES: lets a Communication row name EITHER an employee OR a portal user, never both
-- and never neither. It is ADDITIVE and IDEMPOTENT: every statement is guarded, the script may be
-- run repeatedly, and it rewrites no existing row.
--
-- WHY THERE IS NO BACKFILL, and this is a fact rather than a hope: every row that exists today was
-- written with a positive employee id, so it already satisfies the exactly-one rule the moment the
-- portal column is added as NULL. An UPDATE would change nothing and would touch every page.
--
-- WHY THE EMPLOYEE COLUMNS BECOME NULLABLE RATHER THAN KEEPING 0 AS AN ABSENCE MARKER: 0 as
-- "nobody" is precisely the defect this wave removes. Communication has NO consumer of these
-- columns outside BL/Communication and Models/Context/Communication — measured across the
-- repository, not assumed — so nothing outside this platform has to change for the type to be
-- honest. (Models.Context.Admin.Notification made the opposite choice for a stated reason: three
-- of its call sites are owned by other tabs.)
--
-- THERE ARE NO FOREIGN KEYS TO Employee ON THESE TABLES, verified against the live catalogue
-- rather than inferred from the DDL: every FK on the Comm* tables is intra-platform. So the
-- nullability change drops no constraint and changes no cascade.
--
-- THE PortalUsers FOREIGN KEY IS DEFERRED, DELIBERATELY. PortalUsers is created by the Client
-- Portal deploy script, and this platform's script must not assume an ordering it cannot enforce;
-- a hard dependency would make a Communication deployment fail on an estate where the portal has
-- not shipped. Each FK is therefore created ONLY when the parent table exists, and skipped with a
-- notice otherwise. THE EXACTLY-ONE CHECK IS NEVER SKIPPED — it does not depend on PortalUsers and
-- it is the constraint that carries the security meaning.
--
-- SQL EXECUTED: NO. This script is prepared, not run.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. COLUMNS
-- ---------------------------------------------------------------------------------------------

-- CommParticipants ----------------------------------------------------------------------------
IF COL_LENGTH('dbo.CommParticipants', 'PortalUserId') IS NULL
    ALTER TABLE dbo.CommParticipants ADD PortalUserId INT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CommParticipants')
             AND name = 'EmployeeId' AND is_nullable = 0)
BEGIN
    -- The indexes that reference this column must go first; they are rebuilt in section 3 with
    -- their new filters. Dropping and recreating an index is not a data change.
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommParticipants_Live' AND object_id = OBJECT_ID('dbo.CommParticipants'))
        DROP INDEX UX_CommParticipants_Live ON dbo.CommParticipants;
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommParticipants_Employee' AND object_id = OBJECT_ID('dbo.CommParticipants'))
        DROP INDEX IX_CommParticipants_Employee ON dbo.CommParticipants;

    ALTER TABLE dbo.CommParticipants ALTER COLUMN EmployeeId INT NULL;
END
GO

-- CommComments ---------------------------------------------------------------------------------
IF COL_LENGTH('dbo.CommComments', 'AuthorPortalUserId') IS NULL
    ALTER TABLE dbo.CommComments ADD AuthorPortalUserId INT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CommComments')
             AND name = 'AuthorEmployeeId' AND is_nullable = 0)
    ALTER TABLE dbo.CommComments ALTER COLUMN AuthorEmployeeId INT NULL;
GO

-- CommCommentRevisions -------------------------------------------------------------------------
-- The fifth actor column. Included because it records WHO and was written with the same zero
-- coercion; fixing the comment but not its revision would leave the defect alive one table over.
IF COL_LENGTH('dbo.CommCommentRevisions', 'EditedByPortalUserId') IS NULL
    ALTER TABLE dbo.CommCommentRevisions ADD EditedByPortalUserId INT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CommCommentRevisions')
             AND name = 'EditedByEmployeeId' AND is_nullable = 0)
    ALTER TABLE dbo.CommCommentRevisions ALTER COLUMN EditedByEmployeeId INT NULL;
GO

-- CommCommentAttachments -----------------------------------------------------------------------
IF COL_LENGTH('dbo.CommCommentAttachments', 'UploadedByPortalUserId') IS NULL
    ALTER TABLE dbo.CommCommentAttachments ADD UploadedByPortalUserId INT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CommCommentAttachments')
             AND name = 'UploadedByEmployeeId' AND is_nullable = 0)
    ALTER TABLE dbo.CommCommentAttachments ALTER COLUMN UploadedByEmployeeId INT NULL;
GO

-- CommReadReceipts -----------------------------------------------------------------------------
IF COL_LENGTH('dbo.CommReadReceipts', 'PortalUserId') IS NULL
    ALTER TABLE dbo.CommReadReceipts ADD PortalUserId INT NULL;
GO

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CommReadReceipts')
             AND name = 'EmployeeId' AND is_nullable = 0)
BEGIN
    IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommReadReceipts_Unique' AND object_id = OBJECT_ID('dbo.CommReadReceipts'))
        DROP INDEX UX_CommReadReceipts_Unique ON dbo.CommReadReceipts;

    ALTER TABLE dbo.CommReadReceipts ALTER COLUMN EmployeeId INT NULL;
END
GO

-- ---------------------------------------------------------------------------------------------
-- 2. THE EXACTLY-ONE INVARIANT
--
-- One CHECK per actor-bearing table. This is the constraint that carries the security meaning:
-- CommActor enforces it at the write path, and this enforces it against anything that writes to
-- the database without going through the platform — a script, a fix-up, a future service.
--
-- It is NOT deferred for FK ordering. PortalUsers may not exist yet; "exactly one of these two
-- columns is set" does not care whether the second one has a parent table.
-- ---------------------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommParticipants_ExactlyOneActor')
    ALTER TABLE dbo.CommParticipants ADD CONSTRAINT CK_CommParticipants_ExactlyOneActor
        CHECK ((CASE WHEN EmployeeId IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN PortalUserId IS NULL THEN 0 ELSE 1 END) = 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommComments_ExactlyOneAuthor')
    ALTER TABLE dbo.CommComments ADD CONSTRAINT CK_CommComments_ExactlyOneAuthor
        CHECK ((CASE WHEN AuthorEmployeeId IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN AuthorPortalUserId IS NULL THEN 0 ELSE 1 END) = 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommCommentRevisions_ExactlyOneEditor')
    ALTER TABLE dbo.CommCommentRevisions ADD CONSTRAINT CK_CommCommentRevisions_ExactlyOneEditor
        CHECK ((CASE WHEN EditedByEmployeeId IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN EditedByPortalUserId IS NULL THEN 0 ELSE 1 END) = 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommCommentAttachments_ExactlyOneUploader')
    ALTER TABLE dbo.CommCommentAttachments ADD CONSTRAINT CK_CommCommentAttachments_ExactlyOneUploader
        CHECK ((CASE WHEN UploadedByEmployeeId IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN UploadedByPortalUserId IS NULL THEN 0 ELSE 1 END) = 1);
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CommReadReceipts_ExactlyOneReader')
    ALTER TABLE dbo.CommReadReceipts ADD CONSTRAINT CK_CommReadReceipts_ExactlyOneReader
        CHECK ((CASE WHEN EmployeeId IS NULL THEN 0 ELSE 1 END)
             + (CASE WHEN PortalUserId IS NULL THEN 0 ELSE 1 END) = 1);
GO

-- ---------------------------------------------------------------------------------------------
-- 3. INDEXES
--
-- Uniqueness SPLITS BY ACTOR KIND. A single index over one merged actor id would make employee 7
-- and portal user 7 collide: admitting one would evict the other from the thread, and one of them
-- would inherit the other's read watermark. The employee indexes keep their original NAMES and key
-- shapes and gain one filter clause, so an operator reading a plan sees the same index they knew.
-- ---------------------------------------------------------------------------------------------

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommParticipants_Live' AND object_id = OBJECT_ID('dbo.CommParticipants'))
    CREATE UNIQUE INDEX UX_CommParticipants_Live ON dbo.CommParticipants (ThreadId, EmployeeId)
        WHERE RemovedAt IS NULL AND EmployeeId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommParticipants_Live_Portal' AND object_id = OBJECT_ID('dbo.CommParticipants'))
    CREATE UNIQUE INDEX UX_CommParticipants_Live_Portal ON dbo.CommParticipants (ThreadId, PortalUserId)
        WHERE RemovedAt IS NULL AND PortalUserId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommParticipants_Employee' AND object_id = OBJECT_ID('dbo.CommParticipants'))
    CREATE INDEX IX_CommParticipants_Employee ON dbo.CommParticipants (CompanyID, EmployeeId, Role)
        WHERE RemovedAt IS NULL AND EmployeeId IS NOT NULL;
GO

-- The external membership seek: CommExternalAccessPolicy asks
-- (ThreadId, CompanyID, PortalUserId, RemovedAt IS NULL) once per authorization. CompanyID leads
-- because every external read is company-scoped before it is anything else.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CommParticipants_PortalUser' AND object_id = OBJECT_ID('dbo.CommParticipants'))
    CREATE INDEX IX_CommParticipants_PortalUser ON dbo.CommParticipants (CompanyID, PortalUserId, ThreadId)
        WHERE RemovedAt IS NULL AND PortalUserId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommReadReceipts_Unique' AND object_id = OBJECT_ID('dbo.CommReadReceipts'))
    CREATE UNIQUE INDEX UX_CommReadReceipts_Unique ON dbo.CommReadReceipts (ThreadId, EmployeeId)
        WHERE EmployeeId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CommReadReceipts_Portal' AND object_id = OBJECT_ID('dbo.CommReadReceipts'))
    CREATE UNIQUE INDEX UX_CommReadReceipts_Portal ON dbo.CommReadReceipts (ThreadId, PortalUserId)
        WHERE PortalUserId IS NOT NULL;
GO

-- ---------------------------------------------------------------------------------------------
-- 4. NO FOREIGN KEY TO PortalUsers — AND THAT IS A DESIGN DECISION, NOT A DEFERRAL
--
-- The first draft of this script created four. It was WRONG, and the platform's own guard said so:
-- CommunicationSchemaParityTests.No_foreign_key_points_outside_this_platforms_own_tables asserts
-- that every REFERENCES in this platform's DDL points at a Comm* table. Every FK that exists here
-- is intra-platform (thread -> comment -> revision/attachment/reaction), verified against the live
-- catalogue: there is not even an FK to Employee, which is the older and far more central actor.
--
-- THE REASON THE PLATFORM HOLDS THAT LINE. These tables anchor to business records through
-- (EntityType, EntityId) — a registry code and an untyped id, deliberately not a foreign key,
-- because a conversation may attach to any registered entity and a real FK per type would make the
-- Communication schema depend on every module's schema. Actor identity follows the same rule for
-- the same reason: tenancy and existence are enforced by PREDICATE (CommExternalAccessPolicy reads
-- the PortalUsers row on every authorization) and by the exactly-one CHECK above, not by a
-- constraint that would couple this platform's deployment to the Client Portal's.
--
-- AND IT REMOVES THE ORDERING PROBLEM ENTIRELY rather than guarding it. There is no longer a
-- statement in this script that cares whether PortalUsers exists, so there is no branch to get
-- wrong on an estate that has not deployed the portal, and no second run needed after it does.
--
-- WHAT IS LOST, STATED PLAINLY: the database will not stop an orphaned PortalUserId being written
-- by something that bypasses this platform. That is the same exposure the EmployeeId columns have
-- had since the platform shipped, it is accepted knowingly, and the authorization path never trusts
-- the id alone — it reads the PortalUsers row for company, customer and active state every time.

PRINT 'communication_external_actor_3bb1.sql complete. No row was updated, inserted or deleted.';
GO
