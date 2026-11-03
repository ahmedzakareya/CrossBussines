-- ==========================================================================================
-- Hospital H12 — estimates and packages. Packages (a procedure at a fixed price: its main service, the
-- nights it includes in a room class, what it covers); estimates of a planned admission for one patient
-- (a package or services, the room class, the expected nights), split between the payer and the patient
-- by the patient's cover, with the deposit to ask; and the estimate's lines. An estimate moves no money.
--
-- THREE NEW TABLES. Nothing existing is altered. Idempotent: safe to run repeatedly.
--
-- STATUS: PREPARED, NOT APPLIED. Lives in docs/hospital/h12/ because CrossBuy/deploy/sql/ belongs to TAB-1;
-- it needs hospital_h11_insurance.sql first (an issued estimate may point at its approval request).
-- It has never been run against any database.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalRoomTypes','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
   OR OBJECT_ID('dbo.HospitalPayers','U') IS NULL OR OBJECT_ID('dbo.HospitalPatientPolicies','U') IS NULL OR OBJECT_ID('dbo.HospitalPreauths','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql, hospital_h2_patients.sql, hospital_h6_billing.sql and hospital_h11_insurance.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPackages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPackages (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPackages PRIMARY KEY,
        CompanyId           int             NOT NULL,
        Code                nvarchar(20)    NOT NULL,
        Name                nvarchar(150)   NOT NULL,
        NameEn              nvarchar(150)   NOT NULL,
        ServiceId           int             NOT NULL,
        Price               decimal(18,4)   NOT NULL,
        IncludedNights      int             NOT NULL CONSTRAINT DF_HospitalPackages_Nights DEFAULT(0),
        RoomTypeId          int             NULL,
        Inclusions          nvarchar(1000)  NULL,
        InclusionsEn        nvarchar(1000)  NULL,
        IsActive            bit             NOT NULL CONSTRAINT DF_HospitalPackages_Active DEFAULT(1),
        CreatedAt           datetime2       NOT NULL,
        CreatedByEmployeeId int             NULL,
        CONSTRAINT CK_HospitalPackages_Values CHECK (Price >= 0 AND IncludedNights BETWEEN 0 AND 365),
        CONSTRAINT FK_HospitalPackages_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT FK_HospitalPackages_RoomType FOREIGN KEY (RoomTypeId) REFERENCES dbo.HospitalRoomTypes(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalPackages_Code ON dbo.HospitalPackages (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalEstimates','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEstimates (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalEstimates PRIMARY KEY,
        CompanyId           int            NOT NULL,
        EstimateNo          nvarchar(30)   NOT NULL,
        PatientId           int            NOT NULL,
        PackageId           int            NULL,
        RoomTypeId          int            NULL,
        ExpectedNights      int            NOT NULL CONSTRAINT DF_HospitalEstimates_Nights DEFAULT(0),
        PolicyId            int            NULL,
        PayerId             int            NULL,
        Gross               decimal(18,4)  NOT NULL,
        PayerShare          decimal(18,4)  NOT NULL,
        PatientShare        decimal(18,4)  NOT NULL,
        MarginPct           decimal(9,4)   NOT NULL CONSTRAINT DF_HospitalEstimates_Margin DEFAULT(20),
        Deposit             decimal(18,4)  NOT NULL,
        Status              nvarchar(10)   NOT NULL CONSTRAINT DF_HospitalEstimates_Status DEFAULT('Draft'),
        Note                nvarchar(500)  NULL,
        PreauthId           int            NULL,
        CreatedAt           datetime2      NOT NULL,
        CreatedByEmployeeId int            NULL,
        IssuedAt            datetime2      NULL,
        IssuedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalEstimates_Status CHECK (Status IN ('Draft','Issued','Cancelled')),
        CONSTRAINT CK_HospitalEstimates_Values CHECK (ExpectedNights BETWEEN 0 AND 365 AND Gross >= 0 AND PayerShare >= 0 AND PatientShare >= 0
            AND PayerShare + PatientShare = Gross AND MarginPct BETWEEN 0 AND 200 AND Deposit >= 0),
        CONSTRAINT CK_HospitalEstimates_Issued CHECK (Status <> 'Issued' OR IssuedAt IS NOT NULL),
        CONSTRAINT CK_HospitalEstimates_Cover CHECK ((PolicyId IS NULL AND PayerId IS NULL AND PayerShare = 0) OR (PolicyId IS NOT NULL AND PayerId IS NOT NULL)),
        CONSTRAINT FK_HospitalEstimates_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalEstimates_Package FOREIGN KEY (PackageId) REFERENCES dbo.HospitalPackages(ID),
        CONSTRAINT FK_HospitalEstimates_RoomType FOREIGN KEY (RoomTypeId) REFERENCES dbo.HospitalRoomTypes(ID),
        CONSTRAINT FK_HospitalEstimates_Policy FOREIGN KEY (PolicyId) REFERENCES dbo.HospitalPatientPolicies(ID),
        CONSTRAINT FK_HospitalEstimates_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID),
        CONSTRAINT FK_HospitalEstimates_Preauth FOREIGN KEY (PreauthId) REFERENCES dbo.HospitalPreauths(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalEstimates_No ON dbo.HospitalEstimates (CompanyId, EstimateNo);
    CREATE INDEX IX_HospitalEstimates_Patient ON dbo.HospitalEstimates (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalEstimateLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalEstimateLines (
        ID            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalEstimateLines PRIMARY KEY,
        CompanyId     int            NOT NULL,
        EstimateId    int            NOT NULL,
        Kind          nvarchar(10)   NOT NULL,
        ServiceId     int            NULL,
        Code          nvarchar(40)   NOT NULL,
        Category      nvarchar(20)   NOT NULL,
        Description   nvarchar(300)  NOT NULL,
        DescriptionEn nvarchar(300)  NOT NULL,
        Quantity      decimal(18,4)  NOT NULL,
        UnitPrice     decimal(18,4)  NOT NULL,
        Amount        decimal(18,4)  NOT NULL,
        Covered       bit            NOT NULL,
        PayerAmount   decimal(18,4)  NOT NULL,
        CONSTRAINT CK_HospitalEstimateLines_Kind CHECK (Kind IN ('package','stay','service')),
        CONSTRAINT CK_HospitalEstimateLines_Values CHECK (Quantity > 0 AND UnitPrice >= 0 AND Amount >= 0 AND PayerAmount >= 0 AND PayerAmount <= Amount AND (Covered = 1 OR PayerAmount = 0)),
        CONSTRAINT FK_HospitalEstimateLines_Estimate FOREIGN KEY (EstimateId) REFERENCES dbo.HospitalEstimates(ID),
        CONSTRAINT FK_HospitalEstimateLines_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID)
    );
    CREATE INDEX IX_HospitalEstimateLines_Estimate ON dbo.HospitalEstimateLines (CompanyId, EstimateId);
END

COMMIT TRANSACTION;
