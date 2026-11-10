-- ==========================================================================================
-- Hospital H24 — the patient portal's people and bookings: accounts (verified = linked to a patient file),
-- family links, the bookings made from the portal (each pointing at its H4 appointment) and the requests the
-- call centre books or declines.
--
-- FOUR NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalDepartments','U') IS NULL OR OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPortalAccounts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalAccounts (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalAccounts PRIMARY KEY,
        CompanyId             int            NOT NULL,
        UserId                nvarchar(450)  NULL,
        PatientId             int            NULL,
        Status                nvarchar(12)   NOT NULL,
        Name                  nvarchar(200)  NOT NULL,
        NameEn                nvarchar(200)  NULL,
        Phone                 nvarchar(40)   NULL,
        NationalId            nvarchar(20)   NULL,
        BirthDate             datetime2      NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        VerifiedAt            datetime2      NULL,
        VerifiedByEmployeeId  int            NULL,
        LastSeenAt            datetime2      NULL,
        CONSTRAINT CK_HospitalPortalAccounts_Status CHECK (Status IN (N'Unverified', N'Verified', N'Disabled')),
        -- a verified account always has its file
        CONSTRAINT CK_HospitalPortalAccounts_Verified CHECK (Status <> N'Verified' OR PatientId IS NOT NULL),
        CONSTRAINT FK_HospitalPortalAccounts_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX UX_HospitalPortalAccounts_UserId ON dbo.HospitalPortalAccounts (UserId) WHERE [UserId] IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalPortalAccounts_Patient ON dbo.HospitalPortalAccounts (CompanyId, PatientId) WHERE [PatientId] IS NOT NULL;
END

IF OBJECT_ID('dbo.HospitalPortalFamilyLinks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalFamilyLinks (
        ID                 int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalFamilyLinks PRIMARY KEY,
        CompanyId          int            NOT NULL,
        AccountId          int            NOT NULL,
        PatientId          int            NOT NULL,
        Relation           nvarchar(10)   NOT NULL,
        AddedAt            datetime2      NOT NULL,
        AddedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalPortalFamilyLinks_Relation CHECK (Relation IN (N'Spouse', N'Child', N'Parent', N'Other')),
        CONSTRAINT FK_HospitalPortalFamilyLinks_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT FK_HospitalPortalFamilyLinks_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalFamilyLinks_AccountId_PatientId ON dbo.HospitalPortalFamilyLinks (AccountId, PatientId);
END

IF OBJECT_ID('dbo.HospitalPortalBookings','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalBookings (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalBookings PRIMARY KEY,
        CompanyId      int            NOT NULL,
        AppointmentId  int            NOT NULL,
        AccountId      int            NOT NULL,
        PatientId      int            NOT NULL,
        Referral       nvarchar(60)   NULL,
        BookedAt       datetime2      NOT NULL,
        CancelledAt    datetime2      NULL,
        CONSTRAINT FK_HospitalPortalBookings_Appointment FOREIGN KEY (AppointmentId) REFERENCES dbo.HospitalAppointments(ID),
        CONSTRAINT FK_HospitalPortalBookings_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT FK_HospitalPortalBookings_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalBookings_AppointmentId ON dbo.HospitalPortalBookings (AppointmentId);
    CREATE INDEX IX_HospitalPortalBookings_CompanyId_AccountId ON dbo.HospitalPortalBookings (CompanyId, AccountId);
END

IF OBJECT_ID('dbo.HospitalPortalRequests','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalRequests (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPortalRequests PRIMARY KEY,
        CompanyId            int            NOT NULL,
        RequestNo            nvarchar(20)   NOT NULL,
        AccountId            int            NOT NULL,
        PatientId            int            NULL,
        DepartmentId         int            NOT NULL,
        VisitType            nvarchar(20)   NOT NULL,
        PreferredDays        nvarchar(120)  NULL,
        TimeOfDay            nvarchar(10)   NOT NULL,
        Note                 nvarchar(400)  NULL,
        Status               nvarchar(12)   NOT NULL,
        AppointmentId        int            NULL,
        DeclineReason        nvarchar(300)  NULL,
        CreatedAt            datetime2      NOT NULL,
        HandledAt            datetime2      NULL,
        HandledByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalPortalRequests_Status CHECK (Status IN (N'Open', N'Booked', N'Declined', N'Withdrawn')),
        CONSTRAINT CK_HospitalPortalRequests_TimeOfDay CHECK (TimeOfDay IN (N'Morning', N'Evening', N'Any')),
        CONSTRAINT CK_HospitalPortalRequests_Booked CHECK (Status <> N'Booked' OR AppointmentId IS NOT NULL),
        CONSTRAINT FK_HospitalPortalRequests_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT FK_HospitalPortalRequests_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalPortalRequests_Department FOREIGN KEY (DepartmentId) REFERENCES dbo.HospitalDepartments(ID),
        CONSTRAINT FK_HospitalPortalRequests_Appointment FOREIGN KEY (AppointmentId) REFERENCES dbo.HospitalAppointments(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalPortalRequests_CompanyId_RequestNo ON dbo.HospitalPortalRequests (CompanyId, RequestNo);
    CREATE INDEX IX_HospitalPortalRequests_CompanyId_Status ON dbo.HospitalPortalRequests (CompanyId, Status);
END

COMMIT TRANSACTION;
