-- ==========================================================================================
-- Hospital H40 — intensive care (the prototype's #/icu): the hourly flowsheet of an ICU patient (heart rate, mean
-- arterial pressure, SpO2, respiratory rate, temperature, urine output — entered by the nurse until the bedside
-- monitors are connected) and the day's assessment (SOFA, ventilator, vasopressors). Who is in intensive care is read
-- from the beds (an open stay in an Icu room), so nothing here repeats it.
--   HospitalIcuObservations  one row per admission and hour
--   HospitalIcuAssessments   one row per admission and day
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql and hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalIcuObservations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuObservations (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int           NOT NULL,
        AdmissionId           int           NOT NULL,
        PatientId             int           NOT NULL,
        HourAt                datetime2     NOT NULL,
        HeartRate             int           NULL,
        Map                   int           NULL,
        SpO2                  int           NULL,
        RespRate              int           NULL,
        TempC                 decimal(4,1)  NULL,
        UrineMl               int           NULL,
        RecordedByEmployeeId  int           NULL,
        RecordedAt            datetime2     NOT NULL,
        CONSTRAINT PK_HospitalIcuObservations PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuObservations_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalIcuObservations_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalIcuObservations_Ranges CHECK (
            (HeartRate IS NULL OR HeartRate BETWEEN 20 AND 250) AND (Map IS NULL OR Map BETWEEN 20 AND 200) AND
            (SpO2 IS NULL OR SpO2 BETWEEN 50 AND 100) AND (RespRate IS NULL OR RespRate BETWEEN 4 AND 80) AND
            (TempC IS NULL OR TempC BETWEEN 30 AND 45) AND (UrineMl IS NULL OR UrineMl BETWEEN 0 AND 2000))
    );
    CREATE UNIQUE INDEX IX_HospitalIcuObservations_AdmissionId_HourAt ON dbo.HospitalIcuObservations (AdmissionId, HourAt);
END

IF OBJECT_ID('dbo.HospitalIcuAssessments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIcuAssessments (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int        NOT NULL,
        AdmissionId           int        NOT NULL,
        PatientId             int        NOT NULL,
        Day                   date       NOT NULL,
        Sofa                  int        NULL,
        Ventilated            bit        NOT NULL,
        Vasopressors          bit        NOT NULL,
        RecordedByEmployeeId  int        NULL,
        RecordedAt            datetime2  NOT NULL,
        CONSTRAINT PK_HospitalIcuAssessments PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIcuAssessments_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalIcuAssessments_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalIcuAssessments_Sofa CHECK (Sofa IS NULL OR Sofa BETWEEN 0 AND 24)
    );
    CREATE UNIQUE INDEX IX_HospitalIcuAssessments_AdmissionId_Day ON dbo.HospitalIcuAssessments (AdmissionId, Day);
END

COMMIT TRANSACTION;
