-- ==========================================================================================
-- CRM C1 — one customer record: communication consent, record merges, "not a duplicate" decisions.
--   CrmConsents              every consent decision per person and channel, append-only (the latest row is the state)
--   CrmMerges                one row per merged record: which survived, what moved, who merged it (the redirect)
--   CrmDuplicateDismissals   a pair a person has ruled "not the same", so it stops being proposed
--
-- Additive only (three new tables, no change to an existing table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmContacts','U') IS NULL OR OBJECT_ID('dbo.CrmAccounts','U') IS NULL OR OBJECT_ID('dbo.Leads','U') IS NULL
    THROW 51960, 'The CRM base tables (CrmAccounts, CrmContacts, Leads) must exist first.', 1;

IF OBJECT_ID('dbo.CrmConsents','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmConsents (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        SubjectType            nvarchar(10)   NOT NULL,   -- Contact | Lead
        SubjectId              int            NOT NULL,
        Channel                nvarchar(10)   NOT NULL,   -- WhatsApp | Email | Sms | Call
        State                  nvarchar(8)    NOT NULL,   -- OptIn | OptOut
        Source                 nvarchar(16)   NOT NULL,   -- Verbal | Written | WebForm | Unsubscribe | Import | Merge
        Note                   nvarchar(300)  NULL,
        RecordedAt             datetime2      NOT NULL,
        RecordedByEmployeeId   int            NULL,
        CONSTRAINT PK_CrmConsents PRIMARY KEY (ID),
        CONSTRAINT CK_CrmConsents_Subject CHECK (SubjectType IN (N'Contact', N'Lead')),
        CONSTRAINT CK_CrmConsents_Channel CHECK (Channel IN (N'WhatsApp', N'Email', N'Sms', N'Call')),
        CONSTRAINT CK_CrmConsents_State   CHECK (State IN (N'OptIn', N'OptOut')),
        CONSTRAINT CK_CrmConsents_Source  CHECK (Source IN (N'Verbal', N'Written', N'WebForm', N'Unsubscribe', N'Import', N'Merge')),
        -- a withdrawal by the person themself is never recorded as a grant
        CONSTRAINT CK_CrmConsents_Unsub   CHECK (Source <> N'Unsubscribe' OR State = N'OptOut')
    );
    CREATE INDEX IX_CrmConsents_Subject ON dbo.CrmConsents (CompanyId, SubjectType, SubjectId, Channel, RecordedAt);
END;

IF OBJECT_ID('dbo.CrmMerges','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmMerges (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        EntityType             nvarchar(10)   NOT NULL,   -- Account | Contact
        SurvivorId             int            NOT NULL,
        MergedId               int            NOT NULL,
        MergedName             nvarchar(200)  NOT NULL,
        MovedJson              nvarchar(2000) NOT NULL,   -- what moved, by table: {"Opportunities":3,...}
        MergedAt               datetime2      NOT NULL,
        MergedByEmployeeId     int            NULL,
        CONSTRAINT PK_CrmMerges PRIMARY KEY (ID),
        CONSTRAINT CK_CrmMerges_Entity CHECK (EntityType IN (N'Account', N'Contact')),
        CONSTRAINT CK_CrmMerges_NotSelf CHECK (SurvivorId <> MergedId)
    );
    -- a record is merged away once; the row is its redirect
    CREATE UNIQUE INDEX UX_CrmMerges_Merged ON dbo.CrmMerges (CompanyId, EntityType, MergedId);
END;

IF OBJECT_ID('dbo.CrmDuplicateDismissals','U') IS NULL
BEGIN
    CREATE TABLE dbo.CrmDuplicateDismissals (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        EntityType             nvarchar(10)   NOT NULL,   -- Account | Contact
        LowId                  int            NOT NULL,
        HighId                 int            NOT NULL,
        DismissedAt            datetime2      NOT NULL,
        DismissedByEmployeeId  int            NULL,
        CONSTRAINT PK_CrmDuplicateDismissals PRIMARY KEY (ID),
        CONSTRAINT CK_CrmDuplicateDismissals_Entity CHECK (EntityType IN (N'Account', N'Contact')),
        CONSTRAINT CK_CrmDuplicateDismissals_Order CHECK (LowId < HighId)
    );
    CREATE UNIQUE INDEX UX_CrmDuplicateDismissals_Pair ON dbo.CrmDuplicateDismissals (CompanyId, EntityType, LowId, HighId);
END;

-- verification: all three tables exist
IF (SELECT COUNT(*) FROM (VALUES (N'CrmConsents'), (N'CrmMerges'), (N'CrmDuplicateDismissals')) t(n) WHERE OBJECT_ID(N'dbo.' + t.n, 'U') IS NOT NULL) <> 3
    THROW 51961, 'crm_c1_identity: verification failed.', 1;

COMMIT TRANSACTION;
