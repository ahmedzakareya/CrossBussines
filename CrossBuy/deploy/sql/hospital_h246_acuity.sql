-- ==========================================================================================
-- Hospital H246 - acuity-based staffing: each inpatient's classification per day and shift, and each ward's
-- planned nurses per day and shift.
--
--   HospitalAcuityScores   (new)
--   HospitalWardStaffing   (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52046, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalAcuityScores','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAcuityScores (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AdmissionId int NOT NULL,
        PatientId int NOT NULL,
        Day date NOT NULL,
        Shift nvarchar(10) NOT NULL,
        Factors nvarchar(200) NOT NULL,
        Level int NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalAcuityScores PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalAcuityScores_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalAcuityScores_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalAcuityScores_Level CHECK (Level BETWEEN 1 AND 4),
        CONSTRAINT CK_HospitalAcuityScores_Shift CHECK (Shift IN ('Day','Evening','Night'))
    );
    CREATE UNIQUE INDEX IX_HospitalAcuityScores_CompanyId_AdmissionId_Day_Shift ON dbo.HospitalAcuityScores (CompanyId, AdmissionId, Day, Shift);
    CREATE INDEX IX_HospitalAcuityScores_CompanyId_PatientId ON dbo.HospitalAcuityScores (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalWardStaffing','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalWardStaffing (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        WardId int NOT NULL,
        Day date NOT NULL,
        Shift nvarchar(10) NOT NULL,
        NursesPlanned int NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        UpdatedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalWardStaffing PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalWardStaffing_Shift CHECK (Shift IN ('Day','Evening','Night')),
        CONSTRAINT CK_HospitalWardStaffing_Nurses CHECK (NursesPlanned BETWEEN 0 AND 100)
    );
    CREATE UNIQUE INDEX IX_HospitalWardStaffing_CompanyId_WardId_Day_Shift ON dbo.HospitalWardStaffing (CompanyId, WardId, Day, Shift);
END

COMMIT TRANSACTION;
