-- =============================================================================================
-- Client Portal — slice comm_portal_002_contact_request.
--
-- AUTHORED, NOT EXECUTED. No DDL ran for this batch: no production write and no CrossBuyDev change.
--
-- THE comm_ PREFIX IS A GOVERNANCE ARTEFACT, NOT A CLAIM, exactly as in comm_portal_001_identity:
-- CrossBuy/deploy/sql/comm_* is the SQL glob this work stream owns. The table is
-- PortalContactRequests, which is what it is.
--
-- WHAT THIS TABLE IS. An external customer asking a human to get in touch. It is the FIRST row in
-- this product that an outsider creates, which is why every column exists to answer "who asked, on
-- whose behalf, about what, and has anyone dealt with it".
--
-- WHY NOT CRM Activity, measured rather than assumed. Activity has CompanyID, Type, Subject,
-- CustomerId, OwnerEmployeeId, EntityType, EntityId, Done and Notes — but NO PortalUserId, so it
-- cannot say which external login asked; its EntityType vocabulary is Lead, Opportunity, Account,
-- Customer and Contact, which does not include Quotation; and OwnerEmployeeId is a STAFF owner, so a
-- customer-originated row would be indistinguishable from work a colleague created. Activity models
-- staff follow-up. Writing a customer request into it would make the provenance of the row a lie,
-- and provenance is the only thing this row is for.
--
-- NO LOOKUP TABLE FOR Status. The vocabulary is two values — REQUESTED and HANDLED — and the
-- platform already expresses small closed vocabularies as constants. A lookup here would be two rows
-- nobody edits, one more thing to seed, and one more thing to keep in step with the code.
--
-- NO FK TO Quotations. QuotationId is nullable and the reference is OWNERSHIP-CHECKED in the service
-- before the row is written (company AND customer AND customer-visible status). A hard FK would add
-- no safety the check does not already give and would couple an external-facing table to the
-- lifecycle of a sales document that may later be archived.
--
-- ADDITIVE + IDEMPOTENT. Safe to run any number of times. It creates ONE new table and its two
-- indexes:
--   * NO existing table, column, index or constraint is altered or dropped.
--   * NO existing row is read, written or migrated.
--   * NO Customer, Employee, Identity, GL, stock, accounting, CRM or POS object is modified.
--   * NO seed data of any kind. A status vocabulary needs no rows, and a seeded contact request
--     would be a fabricated customer asking a real salesperson to call them.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID('dbo.PortalContactRequests') IS NULL
BEGIN
    CREATE TABLE dbo.PortalContactRequests
    (
        Id                  int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_PortalContactRequests PRIMARY KEY,

        -- The tenant and the customer within it. BOTH, for the same reason PortalUsers carries both:
        -- company alone is not a boundary once a company has two customers.
        CompanyID           int            NOT NULL,
        CustomerId          int            NOT NULL,

        -- WHICH EXTERNAL LOGIN ASKED. This column is the reason this is a new table rather than a
        -- CRM Activity, and it is what lets staff answer "who wants us to call" rather than only
        -- "which account".
        PortalUserId        int            NOT NULL,

        -- What it is about, when it is about something. Nullable: a customer may simply want to talk.
        QuotationId         int                NULL,

        RequestedAt         datetime2      NOT NULL
            CONSTRAINT DF_PortalContactRequests_RequestedAt DEFAULT (SYSUTCDATETIME()),

        -- The customer's own words. The ONE field an outsider fully controls anywhere in this
        -- schema, so it is bounded here as well as in the service.
        Message             nvarchar(1000)     NULL,

        -- REQUESTED | HANDLED. Two, because the product has exactly two: nobody has looked at it
        -- yet, or somebody has.
        Status              nvarchar(32)   NOT NULL
            CONSTRAINT DF_PortalContactRequests_Status DEFAULT ('REQUESTED'),

        -- Who closed it and when. A STAFF id on purpose: the customer raises it, staff close it, and
        -- that asymmetry is the whole workflow.
        HandledAt           datetime2          NULL,
        HandledByEmployeeId int                NULL
    );
    PRINT 'PortalContactRequests created.';
END
ELSE PRINT 'PortalContactRequests already present.';
GO

IF OBJECT_ID('dbo.PortalContactRequests') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PortalContactRequests_Company_Status')
BEGIN
    -- The staff queue: open requests for one company, newest first. The open set is the working set,
    -- and it stays small however many handled rows accumulate behind it.
    CREATE INDEX IX_PortalContactRequests_Company_Status
        ON dbo.PortalContactRequests (CompanyID, Status, RequestedAt)
        INCLUDE (CustomerId, QuotationId, PortalUserId);
    PRINT 'IX_PortalContactRequests_Company_Status created.';
END
GO

IF OBJECT_ID('dbo.PortalContactRequests') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_PortalContactRequests_Company_Customer_Quotation')
BEGIN
    -- The customer's own history, which is ALSO the predicate behind "you already asked about this":
    -- the service refuses a second open request for the same subject rather than writing it twice.
    CREATE INDEX IX_PortalContactRequests_Company_Customer_Quotation
        ON dbo.PortalContactRequests (CompanyID, CustomerId, QuotationId)
        INCLUDE (Status);
    PRINT 'IX_PortalContactRequests_Company_Customer_Quotation created.';
END
GO

PRINT 'comm_portal_002_contact_request complete. Structure only: no seed data, no existing row touched.';
GO
