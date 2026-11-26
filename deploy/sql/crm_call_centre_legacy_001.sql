-- ============================================================================
-- CALL CENTRE — the channel a contact arrived through, and the calls themselves.
--
-- WHY: CrmTickets recorded WHAT was asked and never HOW it arrived, so the first
-- question a support desk is asked — where is the volume coming from — had no
-- answer in the data. And there was no record of a call at all: a module called
-- a call centre that could not say a call had happened.
--
-- A CALL IS NOT A TICKET. One ticket takes many calls, and most calls never
-- become a ticket (no answer, wrong number, a question answered on the spot).
-- Folding one into the other would lose both facts, so CrmCalls is its own table.
--
-- EVERY LINK IS NULLABLE, deliberately. An inbound call starts as a ringing
-- phone and nothing else; the agent identifies the caller during it, and
-- sometimes never does. A schema that required a customer up front would be one
-- nobody could log a real call into.
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- 1) The ticket learns its channel and the number it came from.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'Channel')
BEGIN
    ALTER TABLE dbo.CrmTickets ADD Channel NVARCHAR(20) NULL;
END
GO

-- Account, Contact and Lead each carry a Phone; a ticket did not, so a call from a
-- number matching none of them could not be written down.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'CallerPhone')
BEGIN
    ALTER TABLE dbo.CrmTickets ADD CallerPhone NVARCHAR(40) NULL;
END
GO

-- 2) The calls.
IF OBJECT_ID(N'dbo.CrmCalls', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmCalls
    (
        ID                INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmCalls PRIMARY KEY,
        CompanyID         INT            NOT NULL,
        Direction         NVARCHAR(10)   NOT NULL CONSTRAINT DF_CrmCalls_Direction DEFAULT N'Inbound',
        PhoneNumber       NVARCHAR(40)   NULL,
        TicketId          INT            NULL,
        AccountId         INT            NULL,
        ContactId         INT            NULL,
        CustomerId        INT            NULL,
        LeadId            INT            NULL,
        AgentEmployeeId   INT            NULL,
        StartedAt         DATETIME       NOT NULL,
        EndedAt           DATETIME       NULL,
        -- stored, not derived: a call logged after the fact carries the duration the
        -- agent states, which EndedAt - StartedAt would contradict.
        DurationSeconds   INT            NULL,
        Outcome           NVARCHAR(20)   NOT NULL CONSTRAINT DF_CrmCalls_Outcome DEFAULT N'Answered',
        Notes             NVARCHAR(MAX)  NULL,
        CreatedBy         NVARCHAR(256)  NULL,
        CreatedAt         DATETIME       NULL
    );
END
GO

-- No foreign keys, matching the rest of the CRM tables: a ticket or an account may
-- be removed while the call that mentioned it stays readable as history.

-- The two reads this table exists for: a company's calls newest-first, and
-- "who has called from this number before".
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'IX_CrmCalls_Company_Started')
BEGIN
    CREATE INDEX IX_CrmCalls_Company_Started ON dbo.CrmCalls (CompanyID, StartedAt DESC);
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'IX_CrmCalls_Phone')
BEGIN
    CREATE INDEX IX_CrmCalls_Phone ON dbo.CrmCalls (CompanyID, PhoneNumber)
        WHERE PhoneNumber IS NOT NULL;
END
GO

-- A ticket's own calls, for the timeline on the ticket screen.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'IX_CrmCalls_Ticket')
BEGIN
    CREATE INDEX IX_CrmCalls_Ticket ON dbo.CrmCalls (TicketId)
        WHERE TicketId IS NOT NULL;
END
GO
