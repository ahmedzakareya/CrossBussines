-- =============================================================================================
-- CRM — CALL CENTRE AND TICKET ROUTING
--
-- WHAT THIS DOES: adds the call log (CrmCalls) and the five CrmTickets columns the support desk
-- needs — the channel a ticket arrived on, the caller's number, the unit it is routed to, and the
-- system record it is about.
--
-- WHY IT EXISTS AS A FILE AT ALL. These objects were created directly against one database during
-- the call-centre work and the scripts that made them were never kept, so every other estate was
-- missing them with no way to catch up: /Crm/Calls and ticket routing fail with "Invalid column
-- name 'DepartmentId'". The shape here was read from the catalogue of a database that already
-- carries them, column by column, rather than reconstructed from the C# — the entity is the
-- application's opinion, the table is the fact.
--
-- ADDITIVE AND IDEMPOTENT: every statement is guarded, nothing is rewritten, and the script may be
-- run repeatedly. It writes no rows.
--
-- WHY NO FOREIGN KEYS. The reference database has none on these columns either, and inventing them
-- here would make this script disagree with the estate it was read from. DepartmentId points at
-- Hierarchicals (an org unit, not a person — the owner's rule for routing), SourceType/SourceId are
-- a registry entity code and its id, and neither is a relational anchor the platform enforces.
-- =============================================================================================

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ---- the call log ---------------------------------------------------------------------------
IF OBJECT_ID('dbo.CrmCalls') IS NULL
BEGIN
    CREATE TABLE dbo.CrmCalls
    (
        ID                INT IDENTITY(1,1) NOT NULL,
        CompanyID         INT NOT NULL,
        Direction         NVARCHAR(10)  NOT NULL CONSTRAINT DF_CrmCalls_Direction DEFAULT (N'Inbound'),
        PhoneNumber       NVARCHAR(40)  NULL,
        TicketId          INT NULL,
        AccountId         INT NULL,
        ContactId         INT NULL,
        CustomerId        INT NULL,
        LeadId            INT NULL,
        AgentEmployeeId   INT NULL,
        StartedAt         DATETIME NOT NULL,
        EndedAt           DATETIME NULL,
        DurationSeconds   INT NULL,
        Outcome           NVARCHAR(20)  NOT NULL CONSTRAINT DF_CrmCalls_Outcome DEFAULT (N'Answered'),
        Notes             NVARCHAR(MAX) NULL,
        CreatedBy         NVARCHAR(256) NULL,
        CreatedAt         DATETIME NULL,
        -- Routing and the record the call is about. Both nullable: a call that was simply logged
        -- belongs to nobody in particular and is about nothing in particular.
        DepartmentId      INT NULL,
        SourceType        NVARCHAR(50)  NULL,
        SourceId          INT NULL,
        CONSTRAINT PK_CrmCalls PRIMARY KEY CLUSTERED (ID)
    );
    PRINT 'CrmCalls created.';
END
ELSE PRINT 'CrmCalls already present.';
GO

IF OBJECT_ID('dbo.CrmCalls') IS NOT NULL AND NOT EXISTS
   (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmCalls_Company_Started' AND object_id = OBJECT_ID('dbo.CrmCalls'))
    CREATE INDEX IX_CrmCalls_Company_Started ON dbo.CrmCalls (CompanyID, StartedAt);
GO
IF OBJECT_ID('dbo.CrmCalls') IS NOT NULL AND NOT EXISTS
   (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmCalls_Phone' AND object_id = OBJECT_ID('dbo.CrmCalls'))
    CREATE INDEX IX_CrmCalls_Phone ON dbo.CrmCalls (CompanyID, PhoneNumber);
GO
IF OBJECT_ID('dbo.CrmCalls') IS NOT NULL AND NOT EXISTS
   (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmCalls_Ticket' AND object_id = OBJECT_ID('dbo.CrmCalls'))
    CREATE INDEX IX_CrmCalls_Ticket ON dbo.CrmCalls (TicketId);
GO
IF OBJECT_ID('dbo.CrmCalls') IS NOT NULL AND NOT EXISTS
   (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmCalls_Company_Dept' AND object_id = OBJECT_ID('dbo.CrmCalls'))
    CREATE INDEX IX_CrmCalls_Company_Dept ON dbo.CrmCalls (CompanyID, DepartmentId);
GO

-- ---- the ticket's channel, caller, routing and anchor -----------------------------------------
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'Channel') IS NULL
    ALTER TABLE dbo.CrmTickets ADD Channel NVARCHAR(20) NULL;
GO
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'CallerPhone') IS NULL
    ALTER TABLE dbo.CrmTickets ADD CallerPhone NVARCHAR(40) NULL;
GO
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'DepartmentId') IS NULL
    ALTER TABLE dbo.CrmTickets ADD DepartmentId INT NULL;
GO
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'SourceType') IS NULL
    ALTER TABLE dbo.CrmTickets ADD SourceType NVARCHAR(50) NULL;
GO
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'SourceId') IS NULL
    ALTER TABLE dbo.CrmTickets ADD SourceId INT NULL;
GO

IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'DepartmentId') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmTickets_Company_Dept' AND object_id = OBJECT_ID('dbo.CrmTickets'))
    CREATE INDEX IX_CrmTickets_Company_Dept ON dbo.CrmTickets (CompanyID, DepartmentId);
GO
IF OBJECT_ID('dbo.CrmTickets') IS NOT NULL AND COL_LENGTH('dbo.CrmTickets', 'SourceType') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmTickets_Source' AND object_id = OBJECT_ID('dbo.CrmTickets'))
    CREATE INDEX IX_CrmTickets_Source ON dbo.CrmTickets (CompanyID, SourceType, SourceId);
GO

PRINT 'crm_call_centre.sql complete. No row was updated, inserted or deleted.';
GO
