-- ==========================================================================================
-- Hospital H23 — patient portal booking rules: the company's general rules, a rule per outpatient clinic (booking
-- mode, follow-up with the same doctor, referral, video, the visit types offered online) and per doctor (online,
-- taking new patients). A company or clinic without a row books by the defaults in HospitalPortalPolicy.
--
-- THREE NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDepartments','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPortalSettings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalSettings (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalSettings PRIMARY KEY,
        CompanyId            int            NOT NULL,
        WindowDays           int            NOT NULL,
        LeadHours            int            NOT NULL,
        CancelHours          int            NOT NULL,
        MaxActive            int            NOT NULL,
        FamilyBooking        bit            NOT NULL,
        Waitlist             bit            NOT NULL,
        SelfRegistration     bit            NOT NULL,
        ShowFees             bit            NOT NULL,
        InsuranceCheck       bit            NOT NULL,
        Payment              nvarchar(10)   NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalPortalSettings_Ranges CHECK (WindowDays BETWEEN 1 AND 365 AND LeadHours BETWEEN 0 AND 72 AND CancelHours BETWEEN 0 AND 168 AND MaxActive BETWEEN 1 AND 20),
        CONSTRAINT CK_HospitalPortalSettings_Payment CHECK (Payment IN (N'None', N'Deposit', N'Full'))
    );
    CREATE UNIQUE INDEX IX_HospitalPortalSettings_CompanyId ON dbo.HospitalPortalSettings (CompanyId);
END

IF OBJECT_ID('dbo.HospitalPortalClinicRules','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalClinicRules (
        ID                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalClinicRules PRIMARY KEY,
        CompanyId          int            NOT NULL,
        DepartmentId       int            NOT NULL,
        Mode               nvarchar(10)   NOT NULL,
        FollowUpOwnDoctor  bit            NOT NULL,
        ReferralForNew     bit            NOT NULL,
        Video              bit            NOT NULL,
        VisitTypes         nvarchar(100)  NOT NULL,
        CONSTRAINT CK_HospitalPortalClinicRules_Mode CHECK (Mode IN (N'Doctor', N'Next', N'Request', N'Off')),
        CONSTRAINT FK_HospitalPortalClinicRules_Department FOREIGN KEY (DepartmentId) REFERENCES dbo.HospitalDepartments(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalClinicRules_CompanyId_DepartmentId ON dbo.HospitalPortalClinicRules (CompanyId, DepartmentId);
END

IF OBJECT_ID('dbo.HospitalPortalDoctorRules','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalDoctorRules (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalDoctorRules PRIMARY KEY,
        CompanyId       int  NOT NULL,
        PractitionerId  int  NOT NULL,
        Online          bit  NOT NULL,
        NewPatients     bit  NOT NULL,
        CONSTRAINT FK_HospitalPortalDoctorRules_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalDoctorRules_CompanyId_PractitionerId ON dbo.HospitalPortalDoctorRules (CompanyId, PractitionerId);
END

COMMIT TRANSACTION;
