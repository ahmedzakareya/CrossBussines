-- ============================================================================
-- WHAT THE TICKET IS ABOUT — the record in this system that the caller means.
--
-- WHY: a support desk call is never about nothing. It is about THIS delivery
-- order, THIS invoice, THIS item that came out of the hyper with a fault. The
-- ticket carried a free-text subject and nothing else, so the department it
-- reached had to ask the customer all over again for the number they had
-- already read out on the phone — and nobody could ever count how many tickets
-- a branch, a product or a sales channel was generating.
--
-- THE REFERENCE IS A REGISTRY CODE, not a free-text type. IEntityRegistry owns
-- the frozen vocabulary (PosOrder, SalesInvoice, Item, ManufWorkOrder, ...) and
-- already resolves a code+id to a label and a deep link for the record picker,
-- so the ticket stores exactly what that picker returns and nothing of its own.
--
-- NO FOREIGN KEY: the column is polymorphic by design, and a document removed
-- next year must not take the ticket's history with it.
--
-- Idempotent: safe to re-run.
-- ============================================================================

-- SET QUOTED_IDENTIFIER ON PER BATCH: every GO starts a new batch with the session
-- default, and sqlcmd -i runs with it OFF - under which CREATE INDEX on a FILTERED
-- index fails while the columns land, which reads like a half-applied slice.

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'SourceType')
BEGIN
    ALTER TABLE dbo.CrmTickets ADD SourceType NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'SourceId')
BEGIN
    ALTER TABLE dbo.CrmTickets ADD SourceId INT NULL;
END
GO

-- The call keeps its own copy. A call is logged before anyone decides whether it
-- deserves a ticket, and "what was this call about" has to survive that decision.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'SourceType')
BEGIN
    ALTER TABLE dbo.CrmCalls ADD SourceType NVARCHAR(50) NULL;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.CrmCalls') AND name = N'SourceId')
BEGIN
    ALTER TABLE dbo.CrmCalls ADD SourceId INT NULL;
END
GO

-- The read this exists for: "every ticket raised about this order / this item".
SET QUOTED_IDENTIFIER ON;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE object_id = OBJECT_ID(N'dbo.CrmTickets') AND name = N'IX_CrmTickets_Source')
BEGIN
    CREATE INDEX IX_CrmTickets_Source ON dbo.CrmTickets (CompanyID, SourceType, SourceId)
        WHERE SourceType IS NOT NULL;
END
GO
