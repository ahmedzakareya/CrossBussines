-- =============================================================================================
-- CLIENT PORTAL DOC1 — PUBLISHED CLIENT DOCUMENTS
--
-- WHAT THIS TABLE IS: a GRANT. One row says "this company deliberately gave THIS document to THIS
-- customer". Nothing else in the product can say that, which is the whole reason it exists.
--
-- WHAT IT IS NOT, and every one of these was a real alternative that was rejected:
--   * NOT a file. No bytes, no path, no URL, no filename. The document platform already stores
--     content under a root OUTSIDE wwwroot, where it has no URL at all; a copy here would be a
--     second lifecycle and a second way to go stale.
--   * NOT a flag on the document. A boolean says "published" but cannot say TO WHOM, so with two
--     customers of one company it degrades to "same CompanyId means visible" - the exact failure
--     this table exists to prevent. It also cannot be revoked per customer.
--   * NOT a new confidentiality tier. Internal | Confidential | Restricted | System answers HOW
--     SENSITIVE, never WHO OUTSIDE THE COMPANY WAS GIVEN IT.
--
-- ADDITIVE AND IDEMPOTENT: every statement is guarded, a second run is a no-op, and there is no
-- DROP, no ALTER of an existing table, no TRUNCATE, no backfill and no seed anywhere in this file.
--
-- RESTRICT, NEVER CASCADE. Removing a publication must never remove the document it published, and
-- deleting a governed document out from under a live grant must fail loudly rather than silently
-- leave a customer pointing at nothing. NO_ACTION closes both directions of that mistake.
--
-- TENANCY IS EXPLICIT. The measured global query filter covers fourteen entities and does not cover
-- this one, so CompanyID is carried on the row and every read states the predicate. A publication
-- visible across tenants would hand one company's documents to another company's customer.
--
-- SQL EXECUTION: NO. Authoring this file and running it are different permissions; TAB-1 remains
-- the deploy authority and DOC1 authorizes no database execution.
-- =============================================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. TABLE
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ClientDocumentPublications', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ClientDocumentPublications
    (
        Id                      BIGINT        IDENTITY(1,1) NOT NULL,

        -- The tenant. NOT NULL and never taken from a request: it comes from the publishing
        -- employee's resolved BusinessContext.
        CompanyID               INT           NOT NULL,

        -- WHO IT WAS GIVEN TO. No foreign key, matching PortalUsers.CustomerId which has none
        -- either: tenancy is enforced by predicate on every read, and a cross-module foreign key
        -- would couple the portal's deployment to the customer master's.
        CustomerId              INT           NOT NULL,

        -- The document being granted. It keeps its own anchor, lifecycle, confidentiality and
        -- versions; this row overrides none of them.
        PlatformDocumentId      BIGINT        NOT NULL,

        -- WHO DECIDED, AND WHEN. Stored rather than reconstructed from an audit trail, because
        -- "who gave the customer this" is a question the business answers directly.
        PublishedByEmployeeId   INT           NOT NULL,
        PublishedAt             DATETIME2(3)  NOT NULL,

        -- REVOCATION, NEVER DELETION. The row survives and the customer loses sight of the document
        -- on the very next request, because every read filters on this being NULL.
        RevokedAt               DATETIME2(3)  NULL,
        RevokedByEmployeeId     INT           NULL,

        -- Staff-side only. Never rendered to the customer: a note written for colleagues is
        -- internal information.
        Note                    NVARCHAR(400) NULL,

        CONSTRAINT PK_ClientDocumentPublications PRIMARY KEY CLUSTERED (Id),

        -- NO_ACTION, stated rather than inherited. See the header: this is the constraint that keeps
        -- "unpublish" from ever meaning "delete the document".
        CONSTRAINT FK_ClientDocumentPublications_Document
            FOREIGN KEY (PlatformDocumentId)
            REFERENCES dbo.PlatformDocuments (Id)
            ON DELETE NO_ACTION
            ON UPDATE NO_ACTION
    );
END
GO

-- ---------------------------------------------------------------------------------------------
-- 2. INDEXES
--
-- Both are FILTERED on RevokedAt so they describe only grants that are actually in force - which is
-- the only set any read of this table cares about.
-- ---------------------------------------------------------------------------------------------

-- ONE LIVE GRANT PER (document, customer). Filtered, so publish -> revoke -> publish keeps the full
-- history while only one row is ever in force; without the filter a re-publication would collide
-- with the withdrawn row and surface as a duplicate-key error nobody would connect to this.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'UX_ClientDocumentPublications_Live'
                 AND object_id = OBJECT_ID('dbo.ClientDocumentPublications'))
    CREATE UNIQUE INDEX UX_ClientDocumentPublications_Live
        ON dbo.ClientDocumentPublications (PlatformDocumentId, CustomerId)
        WHERE RevokedAt IS NULL;
GO

-- THE PORTAL LIST READ, keyed in the order the predicate is applied: company first, because every
-- external read is company-scoped before it is anything else, then customer, then newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = 'IX_ClientDocumentPublications_Customer'
                 AND object_id = OBJECT_ID('dbo.ClientDocumentPublications'))
    CREATE INDEX IX_ClientDocumentPublications_Customer
        ON dbo.ClientDocumentPublications (CompanyID, CustomerId, PublishedAt)
        WHERE RevokedAt IS NULL;
GO

PRINT 'client_document_publication_doc1.sql complete. No existing table was altered, and no row was inserted, updated or deleted.';
GO
