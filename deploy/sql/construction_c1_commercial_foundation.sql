-- =============================================================================================
-- CrossBusiness Construction & Contracting — C1 COMMERCIAL FOUNDATION
--   CR-01 BOQ stable identity · CR-02 subcontract certification cap · CR-03 immutable commercial
--   revisions · concurrency tokens · line-level audit.
--
-- ADDITIVE AND IDEMPOTENT. Creates NEW tables only. It ALTERS NOTHING and DROPS NOTHING, and it
-- touches no existing table — deliberately, so that a database on which this script has NOT yet run
-- keeps every existing Projects screen working exactly as before. Only the new C1 code paths fail
-- there, and they fail loudly.
--
-- NOT EXECUTED BY THE FOURTH TAB. No SQL was run against CrossBuyDB2 or any other database in this
-- increment. "SQL before code" — the owner applies this BEFORE deploying the C1 code.
--
-- Apply with sqlcmd -I (QUOTED_IDENTIFIER ON) so the filtered indexes below build.
-- =============================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ---------------------------------------------------------------------------------------------
-- 1. ClientContracts — D-01: a project MAY hold several client contracts, with separate scope.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ClientContracts','U') IS NULL
BEGIN
    CREATE TABLE dbo.ClientContracts (
        ID                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ClientContracts PRIMARY KEY,
        CompanyID         int            NOT NULL,
        ProjectId         int            NOT NULL,
        ContractNo        nvarchar(50)   NOT NULL,
        CustomerId        int            NULL,
        Title             nvarchar(200)  NULL,
        TitleEn           nvarchar(200)  NULL,
        ScopeDescription  nvarchar(2000) NULL,
        CurrencyId        int            NULL,
        ContractValue     decimal(19,4)  NULL,
        SignedDate        datetime2      NULL,
        StartDate         datetime2      NULL,
        EndDate           datetime2      NULL,
        IsPrimary         bit            NOT NULL CONSTRAINT DF_ClientContracts_IsPrimary DEFAULT(0),
        [Status]          nvarchar(30)   NOT NULL CONSTRAINT DF_ClientContracts_Status DEFAULT('Draft'),
        CreatedBy         int            NULL,
        CreatedAt         datetime2      NOT NULL CONSTRAINT DF_ClientContracts_CreatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy         int            NULL,
        UpdatedAt         datetime2      NULL,
        ConcurrencyToken  varbinary(16)  NOT NULL CONSTRAINT DF_ClientContracts_Token DEFAULT(CONVERT(varbinary(16), NEWID())),
        CONSTRAINT FK_ClientContracts_Project FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ID),
        CONSTRAINT CK_ClientContracts_Status CHECK ([Status] IN
            ('Draft','Active','Suspended','Completed','Closed','Terminated')),
        CONSTRAINT CK_ClientContracts_Value CHECK (ContractValue IS NULL OR ContractValue >= 0)
    );
    CREATE UNIQUE INDEX UX_ClientContracts_No ON dbo.ClientContracts (CompanyID, ContractNo);
    CREATE INDEX IX_ClientContracts_Project ON dbo.ClientContracts (CompanyID, ProjectId, [Status]);
    -- D-01: at most ONE primary contract per project. A filtered unique index, so any number of
    -- non-primary contracts coexist and two primaries are impossible.
    CREATE UNIQUE INDEX UX_ClientContracts_OnePrimaryPerProject
        ON dbo.ClientContracts (CompanyID, ProjectId) WHERE IsPrimary = 1;
END
GO

-- ---------------------------------------------------------------------------------------------
-- 2. BoqLineStates — CR-01. The 1:1 satellite that makes a BOQ line RETIRABLE instead of deletable.
--    BoqItems itself is untouched, so its identities — the ones certificates point at — cannot move.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.BoqLineStates','U') IS NULL
BEGIN
    CREATE TABLE dbo.BoqLineStates (
        ID                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BoqLineStates PRIMARY KEY,
        CompanyID         int            NOT NULL,
        ProjectId         int            NOT NULL,
        BoqItemId         int            NOT NULL,
        ClientContractId  int            NULL,
        [Status]          nvarchar(20)   NOT NULL CONSTRAINT DF_BoqLineStates_Status DEFAULT('Active'),
        CurrentRevisionId int            NULL,
        RetiredAt         datetime2      NULL,
        RetiredBy         int            NULL,
        RetiredReason     nvarchar(500)  NULL,
        CreatedBy         int            NULL,
        CreatedAt         datetime2      NOT NULL CONSTRAINT DF_BoqLineStates_CreatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy         int            NULL,
        UpdatedAt         datetime2      NULL,
        ConcurrencyToken  varbinary(16)  NOT NULL CONSTRAINT DF_BoqLineStates_Token DEFAULT(CONVERT(varbinary(16), NEWID())),
        CONSTRAINT FK_BoqLineStates_BoqItem FOREIGN KEY (BoqItemId) REFERENCES dbo.BoqItems(ID),
        CONSTRAINT FK_BoqLineStates_Project FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ID),
        CONSTRAINT FK_BoqLineStates_Contract FOREIGN KEY (ClientContractId) REFERENCES dbo.ClientContracts(ID),
        CONSTRAINT CK_BoqLineStates_Status CHECK ([Status] IN ('Active','Retired')),
        -- A retired line must say when and why. A retirement with no reason is not a record.
        CONSTRAINT CK_BoqLineStates_Retirement CHECK
            ([Status] = 'Active' OR (RetiredAt IS NOT NULL AND RetiredReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX UX_BoqLineStates_Item ON dbo.BoqLineStates (BoqItemId);
    CREATE INDEX IX_BoqLineStates_Project ON dbo.BoqLineStates (CompanyID, ProjectId, [Status]);
    CREATE INDEX IX_BoqLineStates_Contract ON dbo.BoqLineStates (CompanyID, ClientContractId);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 3. CommercialRevisions / CommercialRevisionLines — CR-03.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CommercialRevisions','U') IS NULL
BEGIN
    CREATE TABLE dbo.CommercialRevisions (
        ID                      int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CommercialRevisions PRIMARY KEY,
        CompanyID               int            NOT NULL,
        ProjectId               int            NOT NULL,
        ClientContractId        int            NULL,
        RevisionNo              int            NOT NULL,
        Kind                    nvarchar(20)   NOT NULL CONSTRAINT DF_CommercialRevisions_Kind DEFAULT('Revised'),
        [Source]                nvarchar(20)   NOT NULL CONSTRAINT DF_CommercialRevisions_Source DEFAULT('Contract'),
        SourceVariationOrderId  int            NULL,
        EffectiveDate           datetime2      NOT NULL,
        [Status]                nvarchar(20)   NOT NULL CONSTRAINT DF_CommercialRevisions_Status DEFAULT('Draft'),
        TotalValueBefore        decimal(19,4)  NOT NULL CONSTRAINT DF_CommercialRevisions_Before DEFAULT(0),
        TotalValueAfter         decimal(19,4)  NOT NULL CONSTRAINT DF_CommercialRevisions_After DEFAULT(0),
        ValueImpact             decimal(19,4)  NOT NULL CONSTRAINT DF_CommercialRevisions_Impact DEFAULT(0),
        Reason                  nvarchar(1000) NULL,
        ApprovedBy              int            NULL,
        ApprovedAt              datetime2      NULL,
        SupersededByRevisionId  int            NULL,
        CreatedBy               int            NULL,
        CreatedAt               datetime2      NOT NULL CONSTRAINT DF_CommercialRevisions_CreatedAt DEFAULT(SYSUTCDATETIME()),
        ConcurrencyToken        varbinary(16)  NOT NULL CONSTRAINT DF_CommercialRevisions_Token DEFAULT(CONVERT(varbinary(16), NEWID())),
        CONSTRAINT FK_CommercialRevisions_Project FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ID),
        CONSTRAINT FK_CommercialRevisions_Contract FOREIGN KEY (ClientContractId) REFERENCES dbo.ClientContracts(ID),
        CONSTRAINT FK_CommercialRevisions_Variation FOREIGN KEY (SourceVariationOrderId) REFERENCES dbo.VariationOrders(ID),
        CONSTRAINT CK_CommercialRevisions_Kind CHECK (Kind IN ('Original','Revised')),
        CONSTRAINT CK_CommercialRevisions_Source CHECK ([Source] IN ('Contract','Variation','Correction')),
        CONSTRAINT CK_CommercialRevisions_Status CHECK ([Status] IN ('Draft','Approved','Superseded')),
        -- An approved revision must carry its approver and its reason. This is the DB-level half of
        -- the rule the service enforces; a commercial change with no stated reason is refused twice.
        CONSTRAINT CK_CommercialRevisions_Approval CHECK
            ([Status] = 'Draft' OR (ApprovedAt IS NOT NULL AND Reason IS NOT NULL)),
        CONSTRAINT CK_CommercialRevisions_Variation_Source CHECK
            ([Source] <> 'Variation' OR SourceVariationOrderId IS NOT NULL)
    );
    CREATE UNIQUE INDEX UX_CommercialRevisions_No
        ON dbo.CommercialRevisions (CompanyID, ProjectId, ClientContractId, RevisionNo);
    CREATE INDEX IX_CommercialRevisions_Status ON dbo.CommercialRevisions (CompanyID, ProjectId, [Status]);
    -- At most ONE draft revision open per project at a time: a second concurrent draft would make
    -- "the revision in force" ambiguous the moment both were approved.
    CREATE UNIQUE INDEX UX_CommercialRevisions_OneDraftPerProject
        ON dbo.CommercialRevisions (CompanyID, ProjectId) WHERE [Status] = 'Draft';
END
GO

IF OBJECT_ID('dbo.CommercialRevisionLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.CommercialRevisionLines (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CommercialRevisionLines PRIMARY KEY,
        CommercialRevisionId  int            NOT NULL,
        CompanyID             int            NOT NULL,
        BoqItemId             int            NULL,
        LineCode              nvarchar(30)   NULL,
        [Description]         nvarchar(500)  NULL,
        Unit                  nvarchar(30)   NULL,
        PreviousQuantity      decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_PrevQty DEFAULT(0),
        NewQuantity           decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_NewQty DEFAULT(0),
        PreviousRate          decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_PrevRate DEFAULT(0),
        NewRate               decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_NewRate DEFAULT(0),
        QuantityImpact        decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_QtyImpact DEFAULT(0),
        ValueImpact           decimal(19,4)  NOT NULL CONSTRAINT DF_CRL_ValImpact DEFAULT(0),
        ChangeKind            nvarchar(20)   NOT NULL CONSTRAINT DF_CRL_ChangeKind DEFAULT('Adjusted'),
        Note                  nvarchar(1000) NULL,
        CONSTRAINT FK_CommercialRevisionLines_Header FOREIGN KEY (CommercialRevisionId)
            REFERENCES dbo.CommercialRevisions(ID) ON DELETE CASCADE,
        CONSTRAINT FK_CommercialRevisionLines_BoqItem FOREIGN KEY (BoqItemId) REFERENCES dbo.BoqItems(ID),
        CONSTRAINT CK_CommercialRevisionLines_Kind CHECK (ChangeKind IN ('Added','Adjusted','Retired','Unchanged')),
        CONSTRAINT CK_CommercialRevisionLines_NonNegative CHECK (NewQuantity >= 0 AND NewRate >= 0)
    );
    CREATE INDEX IX_CommercialRevisionLines_Header ON dbo.CommercialRevisionLines (CommercialRevisionId, BoqItemId);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 4. SubcontractScopes — CR-02 / D-07. THE CAP.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.SubcontractScopes','U') IS NULL
BEGIN
    CREATE TABLE dbo.SubcontractScopes (
        ID                        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubcontractScopes PRIMARY KEY,
        CompanyID                 int            NOT NULL,
        ProjectId                 int            NOT NULL,
        SubcontractId             int            NOT NULL,
        BoqItemId                 int            NULL,
        ScopeCode                 nvarchar(30)   NULL,
        [Description]             nvarchar(500)  NULL,
        Unit                      nvarchar(30)   NULL,
        AssignedQuantity          decimal(19,4)  NOT NULL CONSTRAINT DF_SubcontractScopes_Assigned DEFAULT(0),
        ApprovedVariationQuantity decimal(19,4)  NOT NULL CONSTRAINT DF_SubcontractScopes_VarQty DEFAULT(0),
        SubRate                   decimal(19,4)  NOT NULL CONSTRAINT DF_SubcontractScopes_Rate DEFAULT(0),
        LastCapVariationOrderId   int            NULL,
        [Status]                  nvarchar(20)   NOT NULL CONSTRAINT DF_SubcontractScopes_Status DEFAULT('Active'),
        CreatedBy                 int            NULL,
        CreatedAt                 datetime2      NOT NULL CONSTRAINT DF_SubcontractScopes_CreatedAt DEFAULT(SYSUTCDATETIME()),
        UpdatedBy                 int            NULL,
        UpdatedAt                 datetime2      NULL,
        ConcurrencyToken          varbinary(16)  NOT NULL CONSTRAINT DF_SubcontractScopes_Token DEFAULT(CONVERT(varbinary(16), NEWID())),
        CONSTRAINT FK_SubcontractScopes_Subcontract FOREIGN KEY (SubcontractId) REFERENCES dbo.Subcontracts(ID),
        CONSTRAINT FK_SubcontractScopes_Project FOREIGN KEY (ProjectId) REFERENCES dbo.Projects(ID),
        CONSTRAINT FK_SubcontractScopes_BoqItem FOREIGN KEY (BoqItemId) REFERENCES dbo.BoqItems(ID),
        CONSTRAINT FK_SubcontractScopes_Variation FOREIGN KEY (LastCapVariationOrderId) REFERENCES dbo.VariationOrders(ID),
        CONSTRAINT CK_SubcontractScopes_Status CHECK ([Status] IN ('Active','Retired')),
        CONSTRAINT CK_SubcontractScopes_Assigned CHECK (AssignedQuantity > 0),
        CONSTRAINT CK_SubcontractScopes_Rate CHECK (SubRate >= 0),
        -- The variation-authorised quantity may never be negative, and any non-zero value must name
        -- the variation that authorised it. That is D-07 expressed in the schema.
        CONSTRAINT CK_SubcontractScopes_VariationQty CHECK
            (ApprovedVariationQuantity >= 0
             AND (ApprovedVariationQuantity = 0 OR LastCapVariationOrderId IS NOT NULL))
    );
    CREATE INDEX IX_SubcontractScopes_Sub ON dbo.SubcontractScopes (CompanyID, SubcontractId, [Status]);
    CREATE INDEX IX_SubcontractScopes_Boq ON dbo.SubcontractScopes (CompanyID, ProjectId, BoqItemId);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 5. SubcontractCertificateLines — CR-02. The lines SubcontractBillings never had.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.SubcontractCertificateLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.SubcontractCertificateLines (
        ID                        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SubcontractCertificateLines PRIMARY KEY,
        CompanyID                 int            NOT NULL,
        SubcontractBillingId      int            NOT NULL,
        SubcontractId             int            NOT NULL,
        SubcontractScopeId        int            NOT NULL,
        BoqItemId                 int            NULL,
        CommercialRevisionId      int            NULL,
        ApprovedVariationOrderId  int            NULL,
        RateSnapshot              decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_Rate DEFAULT(0),
        PreviousQuantity          decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_PrevQty DEFAULT(0),
        PreviousValue             decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_PrevVal DEFAULT(0),
        CurrentQuantity           decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_CurQty DEFAULT(0),
        CurrentValue              decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_CurVal DEFAULT(0),
        CumulativeQuantity        decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_CumQty DEFAULT(0),
        CumulativeValue           decimal(19,4)  NOT NULL CONSTRAINT DF_SCL_CumVal DEFAULT(0),
        AdjustsLineId             int            NULL,
        CreatedAt                 datetime2      NOT NULL CONSTRAINT DF_SCL_CreatedAt DEFAULT(SYSUTCDATETIME()),
        CreatedBy                 int            NULL,
        ConcurrencyToken          varbinary(16)  NOT NULL CONSTRAINT DF_SCL_Token DEFAULT(CONVERT(varbinary(16), NEWID())),
        CONSTRAINT FK_SCL_Header FOREIGN KEY (SubcontractBillingId) REFERENCES dbo.SubcontractBillings(ID),
        CONSTRAINT FK_SCL_Subcontract FOREIGN KEY (SubcontractId) REFERENCES dbo.Subcontracts(ID),
        CONSTRAINT FK_SCL_Scope FOREIGN KEY (SubcontractScopeId) REFERENCES dbo.SubcontractScopes(ID),
        CONSTRAINT FK_SCL_BoqItem FOREIGN KEY (BoqItemId) REFERENCES dbo.BoqItems(ID),
        CONSTRAINT FK_SCL_Revision FOREIGN KEY (CommercialRevisionId) REFERENCES dbo.CommercialRevisions(ID),
        CONSTRAINT FK_SCL_Adjusts FOREIGN KEY (AdjustsLineId) REFERENCES dbo.SubcontractCertificateLines(ID),
        CONSTRAINT CK_SCL_NonZero CHECK (CurrentQuantity <> 0),
        -- A negative movement is a CORRECTION and must name the line it corrects.
        CONSTRAINT CK_SCL_NegativeIsAdjustment CHECK (CurrentQuantity > 0 OR AdjustsLineId IS NOT NULL),
        CONSTRAINT CK_SCL_CumulativeNonNegative CHECK (CumulativeQuantity >= 0)
    );
    CREATE INDEX IX_SCL_Header ON dbo.SubcontractCertificateLines (CompanyID, SubcontractBillingId);
    CREATE INDEX IX_SCL_Scope ON dbo.SubcontractCertificateLines (CompanyID, SubcontractScopeId);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 6. CertificateLineSnapshots — CR-03. What a CLIENT certificate line was certified against.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.CertificateLineSnapshots','U') IS NULL
BEGIN
    CREATE TABLE dbo.CertificateLineSnapshots (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CertificateLineSnapshots PRIMARY KEY,
        CompanyID             int            NOT NULL,
        ProjectId             int            NOT NULL,
        ProgressBillingLineId int            NOT NULL,
        ProgressBillingId     int            NOT NULL,
        BoqItemId             int            NULL,
        ClientContractId      int            NULL,
        CommercialRevisionId  int            NULL,
        VariationOrderId      int            NULL,
        ContractedQuantity    decimal(19,4)  NOT NULL CONSTRAINT DF_CLS_ContractedQty DEFAULT(0),
        ContractedRate        decimal(19,4)  NOT NULL CONSTRAINT DF_CLS_ContractedRate DEFAULT(0),
        CertifiedQuantity     decimal(19,4)  NOT NULL CONSTRAINT DF_CLS_CertQty DEFAULT(0),
        CertifiedValue        decimal(19,4)  NOT NULL CONSTRAINT DF_CLS_CertVal DEFAULT(0),
        CapturedAt            datetime2      NOT NULL CONSTRAINT DF_CLS_CapturedAt DEFAULT(SYSUTCDATETIME()),
        CapturedBy            int            NULL,
        CONSTRAINT FK_CLS_Line FOREIGN KEY (ProgressBillingLineId) REFERENCES dbo.ProgressBillingLines(ID),
        CONSTRAINT FK_CLS_Header FOREIGN KEY (ProgressBillingId) REFERENCES dbo.ProgressBillings(ID),
        CONSTRAINT FK_CLS_BoqItem FOREIGN KEY (BoqItemId) REFERENCES dbo.BoqItems(ID),
        CONSTRAINT FK_CLS_Revision FOREIGN KEY (CommercialRevisionId) REFERENCES dbo.CommercialRevisions(ID),
        CONSTRAINT FK_CLS_Contract FOREIGN KEY (ClientContractId) REFERENCES dbo.ClientContracts(ID)
    );
    -- One snapshot per certificate line, forever. A snapshot is never overwritten — that is what
    -- makes it evidence rather than a cache.
    CREATE UNIQUE INDEX UX_CLS_Line ON dbo.CertificateLineSnapshots (ProgressBillingLineId);
    CREATE INDEX IX_CLS_Header ON dbo.CertificateLineSnapshots (CompanyID, ProgressBillingId);
END
GO

-- ---------------------------------------------------------------------------------------------
-- 7. ConstructionAuditEntries — append-only, field-level history.
-- ---------------------------------------------------------------------------------------------
IF OBJECT_ID('dbo.ConstructionAuditEntries','U') IS NULL
BEGIN
    CREATE TABLE dbo.ConstructionAuditEntries (
        ID              bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionAuditEntries PRIMARY KEY,
        CompanyID       int            NOT NULL,
        ProjectId       int            NULL,
        EntityType      nvarchar(50)   NOT NULL,
        EntityId        int            NOT NULL,
        LineId          int            NULL,
        FieldName       nvarchar(100)  NULL,
        OldValue        nvarchar(1000) NULL,
        NewValue        nvarchar(1000) NULL,
        OldNumeric      decimal(19,4)  NULL,
        NewNumeric      decimal(19,4)  NULL,
        ChangeKind      nvarchar(30)   NOT NULL CONSTRAINT DF_CAE_ChangeKind DEFAULT('Updated'),
        Reason          nvarchar(1000) NULL,
        ActorEmployeeId int            NULL,
        ActorUserId     nvarchar(450)  NULL,
        OccurredAt      datetime2      NOT NULL CONSTRAINT DF_CAE_OccurredAt DEFAULT(SYSUTCDATETIME()),
        SourceContext   nvarchar(200)  NULL,
        CorrelationId   uniqueidentifier NOT NULL,
        RevisionId      int            NULL,
        CONSTRAINT CK_CAE_ChangeKind CHECK (ChangeKind IN
            ('Created','Updated','Retired','Approved','Posted','Reversed','Cancelled','CapRaised','Restructured'))
    );
    CREATE INDEX IX_CAE_Entity ON dbo.ConstructionAuditEntries (CompanyID, EntityType, EntityId);
    CREATE INDEX IX_CAE_Correlation ON dbo.ConstructionAuditEntries (CorrelationId);
    CREATE INDEX IX_CAE_When ON dbo.ConstructionAuditEntries (CompanyID, OccurredAt);
END
GO

-- =============================================================================================
-- 8. BACKFILL — NOT RUN HERE, ON PURPOSE.
--
-- Two backfills are needed before the C1 code is fully effective on existing data:
--   (a) one BoqLineStates row per existing BoqItems row (Status = 'Active');
--   (b) one ClientContracts row per contract-bearing project, marked Primary, and BoqLineStates
--       .ClientContractId pointed at it.
--
-- Neither is included as an executable statement because the C1 brief requires the contract mapping
-- to be MEASURED first and the work to STOP if it cannot be made deterministic. Run
-- deploy/sql/construction_c1_contract_mapping_measurement.sql, record M3 (must return ZERO rows) and
-- M9, then run the backfill below as a separate, reviewed step.
--
-- The service layer is written so that a MISSING state row is created on the next save rather than
-- assumed — so (a) is a performance and completeness step, not a correctness prerequisite.
--
-- -- (a) state rows for existing BOQ lines:
-- -- INSERT INTO dbo.BoqLineStates (CompanyID, ProjectId, BoqItemId, [Status], CreatedAt)
-- -- SELECT b.CompanyID, b.ProjectId, b.ID, 'Active', SYSUTCDATETIME()
-- -- FROM dbo.BoqItems b
-- -- WHERE NOT EXISTS (SELECT 1 FROM dbo.BoqLineStates s WHERE s.BoqItemId = b.ID);
--
-- -- (b) one primary contract per contract-bearing project:
-- -- INSERT INTO dbo.ClientContracts (CompanyID, ProjectId, ContractNo, CustomerId, Title,
-- --                                  ContractValue, IsPrimary, [Status], CreatedAt)
-- -- SELECT p.CompanyID, p.ID, CONCAT('MIG-', p.CompanyID, '-', p.ID), p.CustomerId, p.Name,
-- --        p.ContractValue, 1, 'Active', SYSUTCDATETIME()
-- -- FROM dbo.Projects p
-- -- WHERE (p.CustomerId IS NOT NULL OR p.ContractValue IS NOT NULL
-- --        OR p.AdvancePercent IS NOT NULL OR p.RetentionPercent IS NOT NULL)
-- --   AND NOT EXISTS (SELECT 1 FROM dbo.ClientContracts c
-- --                   WHERE c.ProjectId = p.ID AND c.CompanyID = p.CompanyID);
-- =============================================================================================
