-- =============================================================================================
-- Task Management Wave 2 — PLATFORM TAGS.
--
-- Additive + idempotent. Creates two tables and their indexes. Touches no existing table, no GL,
-- no stock, no business data, and alters nothing.
--
-- WHY TWO TABLES AND NOT A COLUMN ON TaskItem
--
-- TaskItem already has `Category`, a single free-text chip. It is not a tag system: it holds one
-- value, it is not a shared vocabulary, two people spell it differently, and CRM cannot use it at
-- all. Tags are many-per-record, chosen from a list the tenant curates, and the explicit
-- requirement is that CRM reuse the SAME vocabulary rather than grow CrmTag beside it. So the
-- definition is company-scoped platform data and the assignment is polymorphic from the outset.
--
-- Category is deliberately left alone. Migrating it is a data decision for whoever owns those
-- values, not a side effect of adding tags.
--
-- WHY THE ASSIGNMENT CARRIES CompanyID
--
-- It could be reached through TagId. It is stored because the row is written for a Task (later a
-- Lead) whose company must be checked against the tag's, and because the platform's global query
-- filter and its write-guard interceptor both need to see a company on the row itself rather than
-- through a join. TagService refuses an assignment whose tag, entity and caller companies disagree.
--
-- WHAT IS DELIBERATELY NOT HERE
--
-- No colour, icon, sort order, parent/child hierarchy, per-user favourites or usage counters. None
-- is required by the current product ask, and each would have to be migrated for CRM as well. A tag
-- is a name.
-- =============================================================================================

IF OBJECT_ID('dbo.Tags', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Tags (
        ID              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Tags PRIMARY KEY,

        -- Tenancy. NOT NULL: a tag that cannot say whose vocabulary it belongs to is unusable, and
        -- company 41 and company 77 may each have their own "Urgent".
        CompanyID       INT            NOT NULL,

        -- What the user typed and what the user sees. Business data: never translated.
        Name            NVARCHAR(60)   NOT NULL,

        -- The comparison key, written only by TagService.Normalize (trim, collapse inner
        -- whitespace, ToUpperInvariant). Uniqueness is on THIS, not on Name.
        NormalizedName  NVARCHAR(60)   NOT NULL,

        -- Archived rather than deleted once used. See TagService.RetireTagAsync.
        IsActive        BIT            NOT NULL CONSTRAINT DF_Tags_IsActive DEFAULT (1),

        -- BaseEntity's audit stamps, the convention for master data of this class.
        CreatedBy       INT            NULL,
        CreatedAt       DATETIME2(3)   NULL,
        updatedBy       INT            NULL,
        UpdatedAt       DATETIME2(3)   NULL,

        CONSTRAINT CK_Tags_Company CHECK (CompanyID > 0),
        CONSTRAINT CK_Tags_Name    CHECK (LEN(LTRIM(RTRIM(Name))) > 0)
    );
END;
GO

-- UNIQUENESS IS PER COMPANY, ON THE NORMALIZED NAME.
--
-- This is the constraint that makes "Urgent", " urgent " and "URGENT" one tag inside one company
-- while leaving company 41 and company 77 free to each have their own. Enforced here and not only
-- in the service, because two concurrent creates both pass an application-only check.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Tags_Company_NormalizedName' AND object_id = OBJECT_ID('dbo.Tags'))
    CREATE UNIQUE INDEX UX_Tags_Company_NormalizedName ON dbo.Tags (CompanyID, NormalizedName);
GO

-- The picker's read path: one company's live tags.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Tags_Company_Active' AND object_id = OBJECT_ID('dbo.Tags'))
    CREATE INDEX IX_Tags_Company_Active ON dbo.Tags (CompanyID, IsActive);
GO

IF OBJECT_ID('dbo.TagAssignments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TagAssignments (
        ID          BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TagAssignments PRIMARY KEY,

        CompanyID   INT            NOT NULL,
        TagId       INT            NOT NULL,

        -- A registered entity code — 'Task' today, CRM codes later. Never free text from a request:
        -- TagService refuses a type it does not know, because an unknown type has no company
        -- resolver and so nothing could verify the target's tenant.
        EntityType  NVARCHAR(60)   NOT NULL,
        EntityId    INT            NOT NULL,

        CreatedBy   INT            NULL,
        CreatedAt   DATETIME2(3)   NULL,
        updatedBy   INT            NULL,
        UpdatedAt   DATETIME2(3)   NULL,

        CONSTRAINT CK_TagAssignments_Company  CHECK (CompanyID > 0),
        CONSTRAINT CK_TagAssignments_EntityId CHECK (EntityId > 0),

        -- RESTRICT, not cascade. A used tag is archived rather than deleted, so a delete that would
        -- orphan assignments must fail loudly instead of quietly erasing what somebody recorded.
        CONSTRAINT FK_TagAssignments_Tag FOREIGN KEY (TagId) REFERENCES dbo.Tags (ID)
    );
END;
GO

-- ONE TAG PER ENTITY, enforced by the database. The service checks first so the caller gets a clean
-- answer, but a double-submit races that check and this is what makes the second row impossible
-- rather than merely unlikely.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TagAssignments_Unique' AND object_id = OBJECT_ID('dbo.TagAssignments'))
    CREATE UNIQUE INDEX UX_TagAssignments_Unique ON dbo.TagAssignments (CompanyID, TagId, EntityType, EntityId);
GO

-- "which tags does this task have?" — the per-entity read, once per screen.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TagAssignments_Entity' AND object_id = OBJECT_ID('dbo.TagAssignments'))
    CREATE INDEX IX_TagAssignments_Entity ON dbo.TagAssignments (CompanyID, EntityType, EntityId);
GO

-- "which tasks carry this tag?" — the FILTER read. Without it a tag filter over a large task list is
-- a scan, which is the performance debt this wave was told not to introduce.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TagAssignments_Tag_Entity' AND object_id = OBJECT_ID('dbo.TagAssignments'))
    CREATE INDEX IX_TagAssignments_Tag_Entity ON dbo.TagAssignments (CompanyID, TagId, EntityType);
GO
