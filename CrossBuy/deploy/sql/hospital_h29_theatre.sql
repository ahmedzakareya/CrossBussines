-- ==========================================================================================
-- Hospital H29 — the theatre schedule: operations (a patient, a procedure, a theatre, a surgeon and an
-- anaesthetist, at a time for a length; Planned → InProgress → Done, or Cancelled) and the WHO surgical safety
-- checklist ticked on each.
--
-- TWO NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalRooms','U') IS NULL OR OBJECT_ID('dbo.HospitalServices','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h1 to hospital_h6 first.', 1;

IF OBJECT_ID('dbo.HospitalSurgeries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurgeries (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSurgeries PRIMARY KEY,
        CompanyId             int            NOT NULL,
        CaseNo                nvarchar(30)   NOT NULL,
        PatientId             int            NOT NULL,
        AdmissionId           int            NULL,
        RoomId                int            NOT NULL,
        ServiceId             int            NOT NULL,
        Code                  nvarchar(50)   NOT NULL,
        Name                  nvarchar(200)  NOT NULL,
        NameEn                nvarchar(200)  NOT NULL,
        SurgeonId             int            NOT NULL,
        AnaesthetistId        int            NULL,
        CaseDate              datetime2      NOT NULL,
        StartMinute           int            NOT NULL,
        DurationMinutes       int            NOT NULL,
        Status                nvarchar(20)   NOT NULL,
        StartedAt             datetime2      NULL,
        EndedAt               datetime2      NULL,
        CancelReason          nvarchar(300)  NULL,
        Note                  nvarchar(500)  NULL,
        BillLineId            int            NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        UpdatedAt             datetime2      NULL,
        UpdatedByEmployeeId   int            NULL,
        CONSTRAINT CK_HospitalSurgeries_Status CHECK (Status IN (N'Planned', N'InProgress', N'Done', N'Cancelled')),
        CONSTRAINT CK_HospitalSurgeries_Times CHECK (StartMinute >= 0 AND StartMinute < 1440 AND DurationMinutes BETWEEN 15 AND 720),
        CONSTRAINT FK_HospitalSurgeries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalSurgeries_Room FOREIGN KEY (RoomId) REFERENCES dbo.HospitalRooms(ID),
        CONSTRAINT FK_HospitalSurgeries_Service FOREIGN KEY (ServiceId) REFERENCES dbo.HospitalServices(ID),
        CONSTRAINT FK_HospitalSurgeries_Surgeon FOREIGN KEY (SurgeonId) REFERENCES dbo.HospitalPractitioners(ID),
        CONSTRAINT FK_HospitalSurgeries_Anaesthetist FOREIGN KEY (AnaesthetistId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSurgeries_CompanyId_CaseNo ON dbo.HospitalSurgeries (CompanyId, CaseNo);
    CREATE INDEX IX_HospitalSurgeries_CompanyId_CaseDate_RoomId ON dbo.HospitalSurgeries (CompanyId, CaseDate, RoomId);
    CREATE INDEX IX_HospitalSurgeries_CompanyId_PatientId ON dbo.HospitalSurgeries (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalSurgeryChecks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurgeryChecks (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalSurgeryChecks PRIMARY KEY,
        CompanyId             int            NOT NULL,
        SurgeryId             int            NOT NULL,
        Item                  nvarchar(20)   NOT NULL,
        CheckedAt             datetime2      NOT NULL,
        CheckedByEmployeeId   int            NULL,
        CONSTRAINT CK_HospitalSurgeryChecks_Item CHECK (Item IN (N'Identity', N'Consent', N'Anaesthesia', N'Allergies', N'Count', N'Specimens')),
        CONSTRAINT FK_HospitalSurgeryChecks_Surgery FOREIGN KEY (SurgeryId) REFERENCES dbo.HospitalSurgeries(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalSurgeryChecks_SurgeryId_Item ON dbo.HospitalSurgeryChecks (SurgeryId, Item);
END

COMMIT TRANSACTION;
