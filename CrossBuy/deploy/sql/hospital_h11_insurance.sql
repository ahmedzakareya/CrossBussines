-- ==========================================================================================
-- Hospital H11 — insurance and claims. Payers' contracts (insurer, corporate, government: discount,
-- co-pay and its cap, price list, the service categories needing prior approval, validity); patients'
-- policies with a payer; pre-authorisations for one service; monthly claim batches per payer, one claim
-- per closed bill (split into the payer's share and the patient's), and claim lines answered by the
-- payer (accepted / rejected with code and reason / resubmitted with a justification).
--
-- SIX NEW TABLES. Nothing existing is altered. Idempotent: safe to run repeatedly.
--
-- STATUS: PREPARED, NOT APPLIED. This file lives in docs/hospital/h11/ because CrossBuy/deploy/sql/ and its
-- manifests belong to TAB-1; moving it there (as hospital_h11_insurance.sql) and registering the slice is
-- an approved step of its own. It has never been run against any database.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalBills','U') IS NULL OR OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
   OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalPriceLists','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h6_billing.sql and hospital_h7_pricelists.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPayers','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPayers (
        ID                int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPayers PRIMARY KEY,
        CompanyId         int            NOT NULL,
        Code              nvarchar(20)   NOT NULL,
        Name              nvarchar(150)  NOT NULL,
        NameEn            nvarchar(150)  NOT NULL,
        Kind              nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalPayers_Kind DEFAULT('Insurer'),
        Classes           nvarchar(100)  NULL,
        DiscountPct       decimal(9,4)   NOT NULL CONSTRAINT DF_HospitalPayers_Discount DEFAULT(0),
        CopayPct          decimal(9,4)   NOT NULL CONSTRAINT DF_HospitalPayers_Copay DEFAULT(0),
        CopayCap          decimal(18,4)  NOT NULL CONSTRAINT DF_HospitalPayers_Cap DEFAULT(0),
        PriceListId       int            NULL,
        PreauthCategories nvarchar(200)  NULL,
        ValidFrom         datetime2      NOT NULL,
        ValidTo           datetime2      NULL,
        IsActive          bit            NOT NULL CONSTRAINT DF_HospitalPayers_Active DEFAULT(1),
        CONSTRAINT CK_HospitalPayers_Kind CHECK (Kind IN ('Insurer','Corporate','Government')),
        CONSTRAINT CK_HospitalPayers_Pct CHECK (DiscountPct BETWEEN 0 AND 100 AND CopayPct BETWEEN 0 AND 100 AND CopayCap >= 0),
        CONSTRAINT CK_HospitalPayers_Dates CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom),
        CONSTRAINT FK_HospitalPayers_PriceList FOREIGN KEY (PriceListId) REFERENCES dbo.HospitalPriceLists(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalPayers_Code ON dbo.HospitalPayers (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalPatientPolicies','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientPolicies (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPatientPolicies PRIMARY KEY,
        CompanyId           int           NOT NULL,
        PatientId           int           NOT NULL,
        PayerId             int           NOT NULL,
        PolicyNo            nvarchar(40)  NOT NULL,
        MemberClass         nvarchar(30)  NULL,
        ValidFrom           datetime2     NOT NULL,
        ValidTo             datetime2     NULL,
        IsActive            bit           NOT NULL CONSTRAINT DF_HospitalPatientPolicies_Active DEFAULT(1),
        CreatedAt           datetime2     NOT NULL,
        CreatedByEmployeeId int           NULL,
        CONSTRAINT CK_HospitalPatientPolicies_Dates CHECK (ValidTo IS NULL OR ValidTo >= ValidFrom),
        CONSTRAINT FK_HospitalPatientPolicies_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPatientPolicies_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID)
    );
    CREATE INDEX IX_HospitalPatientPolicies_Patient ON dbo.HospitalPatientPolicies (CompanyId, PatientId);
    CREATE UNIQUE INDEX UX_HospitalPatientPolicies_No ON dbo.HospitalPatientPolicies (CompanyId, PayerId, PolicyNo);
END

IF OBJECT_ID('dbo.HospitalPreauths','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPreauths (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPreauths PRIMARY KEY,
        CompanyId             int            NOT NULL,
        RequestNo             nvarchar(30)   NOT NULL,
        PatientId             int            NOT NULL,
        PayerId               int            NOT NULL,
        PolicyId              int            NOT NULL,
        ServiceId             int            NOT NULL,
        Amount                decimal(18,4)  NOT NULL,
        Status                nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalPreauths_Status DEFAULT('Pending'),
        ApprovalRef           nvarchar(60)   NULL,
        Reason                nvarchar(300)  NULL,
        RequestedAt           datetime2      NOT NULL,
        RequestedByEmployeeId int            NULL,
        DecidedAt             datetime2      NULL,
        DecidedByEmployeeId   int            NULL,
        CONSTRAINT CK_HospitalPreauths_Status CHECK (Status IN ('Pending','Approved','Rejected')),
        CONSTRAINT CK_HospitalPreauths_Amount CHECK (Amount >= 0),
        -- an answered request carries the payer's reference (approved) or its reason (rejected)
        CONSTRAINT CK_HospitalPreauths_Answer CHECK (Status = 'Pending' OR (DecidedAt IS NOT NULL AND (Status <> 'Approved' OR ApprovalRef IS NOT NULL) AND (Status <> 'Rejected' OR Reason IS NOT NULL))),
        CONSTRAINT FK_HospitalPreauths_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPreauths_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID),
        CONSTRAINT FK_HospitalPreauths_Policy FOREIGN KEY (PolicyId) REFERENCES dbo.HospitalPatientPolicies(ID),
        CONSTRAINT FK_HospitalPreauths_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalPreauths_No ON dbo.HospitalPreauths (CompanyId, RequestNo);
    CREATE INDEX IX_HospitalPreauths_PatientService ON dbo.HospitalPreauths (CompanyId, PatientId, ServiceId);
    -- one waiting request per patient and service: a twin sent at the same moment is refused
    CREATE UNIQUE INDEX UX_HospitalPreauths_Pending ON dbo.HospitalPreauths (CompanyId, PatientId, ServiceId) WHERE Status = N'Pending';
END

IF OBJECT_ID('dbo.HospitalClaimBatches','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClaimBatches (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalClaimBatches PRIMARY KEY,
        CompanyId             int           NOT NULL,
        BatchNo               nvarchar(40)  NOT NULL,
        PayerId               int           NOT NULL,
        Period                char(7)       NOT NULL,
        Status                nvarchar(10)  NOT NULL CONSTRAINT DF_HospitalClaimBatches_Status DEFAULT('Draft'),
        CreatedAt             datetime2     NOT NULL,
        CreatedByEmployeeId   int           NULL,
        SubmittedAt           datetime2     NULL,
        SubmittedByEmployeeId int           NULL,
        SettledAt             datetime2     NULL,
        CONSTRAINT CK_HospitalClaimBatches_Status CHECK (Status IN ('Draft','Submitted','Partial','Paid','Rejected')),
        CONSTRAINT CK_HospitalClaimBatches_Period CHECK (Period LIKE '[12][0-9][0-9][0-9]-[01][0-9]'),
        CONSTRAINT CK_HospitalClaimBatches_Submitted CHECK (Status = 'Draft' OR SubmittedAt IS NOT NULL),
        CONSTRAINT FK_HospitalClaimBatches_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalClaimBatches_No ON dbo.HospitalClaimBatches (CompanyId, BatchNo);
    -- one draft per payer and month, so two builds at the same moment meet in the same batch
    CREATE UNIQUE INDEX UX_HospitalClaimBatches_Draft ON dbo.HospitalClaimBatches (CompanyId, PayerId, Period) WHERE Status = N'Draft';
END

IF OBJECT_ID('dbo.HospitalClaims','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClaims (
        ID           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalClaims PRIMARY KEY,
        CompanyId    int            NOT NULL,
        BatchId      int            NOT NULL,
        BillId       int            NOT NULL,
        PatientId    int            NOT NULL,
        PolicyId     int            NOT NULL,
        Gross        decimal(18,4)  NOT NULL,
        PatientShare decimal(18,4)  NOT NULL,
        InsurerShare decimal(18,4)  NOT NULL,
        CONSTRAINT CK_HospitalClaims_Amounts CHECK (Gross >= 0 AND PatientShare >= 0 AND InsurerShare >= 0),
        CONSTRAINT FK_HospitalClaims_Batch FOREIGN KEY (BatchId) REFERENCES dbo.HospitalClaimBatches(ID),
        CONSTRAINT FK_HospitalClaims_Bill FOREIGN KEY (BillId) REFERENCES dbo.HospitalBills(ID),
        CONSTRAINT FK_HospitalClaims_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalClaims_Policy FOREIGN KEY (PolicyId) REFERENCES dbo.HospitalPatientPolicies(ID)
    );
    -- a bill is claimed once per batch; a resubmitted line brings it into a later batch again
    CREATE UNIQUE INDEX UX_HospitalClaims_BatchBill ON dbo.HospitalClaims (CompanyId, BatchId, BillId);
    CREATE INDEX IX_HospitalClaims_Bill ON dbo.HospitalClaims (CompanyId, BillId);
END

IF OBJECT_ID('dbo.HospitalClaimLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClaimLines (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalClaimLines PRIMARY KEY,
        CompanyId       int            NOT NULL,
        ClaimId         int            NOT NULL,
        BillLineId      int            NULL,
        Code            nvarchar(40)   NOT NULL,
        Description     nvarchar(300)  NOT NULL,
        DescriptionEn   nvarchar(300)  NOT NULL,
        Category        nvarchar(20)   NOT NULL,
        Amount          decimal(18,4)  NOT NULL,
        InsurerAmount   decimal(18,4)  NOT NULL,
        PreauthId       int            NULL,
        MissingPreauth  bit            NOT NULL CONSTRAINT DF_HospitalClaimLines_Missing DEFAULT(0),
        Status          nvarchar(12)   NOT NULL CONSTRAINT DF_HospitalClaimLines_Status DEFAULT('Pending'),
        AcceptedAmount  decimal(18,4)  NULL,
        RejectCode      nvarchar(20)   NULL,
        RejectReason    nvarchar(300)  NULL,
        DecidedAt       datetime2      NULL,
        Justification   nvarchar(500)  NULL,
        ResubmitsLineId int            NULL,
        CONSTRAINT CK_HospitalClaimLines_Status CHECK (Status IN ('Pending','Accepted','Rejected','Resubmitted')),
        CONSTRAINT CK_HospitalClaimLines_Amounts CHECK (Amount >= 0 AND InsurerAmount >= 0 AND InsurerAmount <= Amount AND (AcceptedAmount IS NULL OR (AcceptedAmount >= 0 AND AcceptedAmount <= InsurerAmount))),
        CONSTRAINT CK_HospitalClaimLines_Rejected CHECK (Status NOT IN ('Rejected','Resubmitted') OR (RejectCode IS NOT NULL AND RejectReason IS NOT NULL)),
        CONSTRAINT CK_HospitalClaimLines_Resubmit CHECK (ResubmitsLineId IS NULL OR Justification IS NOT NULL),
        CONSTRAINT FK_HospitalClaimLines_Claim FOREIGN KEY (ClaimId) REFERENCES dbo.HospitalClaims(ID),
        CONSTRAINT FK_HospitalClaimLines_BillLine FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID),
        CONSTRAINT FK_HospitalClaimLines_Preauth FOREIGN KEY (PreauthId) REFERENCES dbo.HospitalPreauths(ID),
        CONSTRAINT FK_HospitalClaimLines_Resubmits FOREIGN KEY (ResubmitsLineId) REFERENCES dbo.HospitalClaimLines(ID)
    );
    CREATE INDEX IX_HospitalClaimLines_Claim ON dbo.HospitalClaimLines (CompanyId, ClaimId);
    -- a rejected line is resubmitted once
    CREATE UNIQUE INDEX UX_HospitalClaimLines_Resubmits ON dbo.HospitalClaimLines (ResubmitsLineId) WHERE ResubmitsLineId IS NOT NULL;
END

COMMIT TRANSACTION;
