-- ==========================================================================================
-- Hospital H212 - clinical surveillance: possible sepsis (qSOFA / SIRS with an infection) and the VTE assessment
-- every admission is due within 24 hours, raised by the system itself and answered by a clinician.
--
--   HospitalSurveillanceAlerts   (new) what fired it, when it is due, the answer
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52012, 'Run the inpatient slices first.', 1;

IF OBJECT_ID('dbo.HospitalSurveillanceAlerts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurveillanceAlerts (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        AdmissionId int NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Criteria nvarchar(200) NOT NULL,
        Score int NULL,
        ObservationId int NULL,
        RaisedAt datetime2 NOT NULL,
        DueAt datetime2 NOT NULL,
        Status nvarchar(10) NOT NULL,
        Response nvarchar(20) NULL,
        Note nvarchar(500) NULL,
        AnsweredAt datetime2 NULL,
        AnsweredByEmployeeId int NULL,
        CONSTRAINT PK_HospitalSurveillanceAlerts PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSurveillanceAlerts_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalSurveillanceAlerts_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT CK_HospitalSurveillanceAlerts_Kind CHECK (Kind IN ('Sepsis', 'Vte')),
        CONSTRAINT CK_HospitalSurveillanceAlerts_Status CHECK (Status IN ('Open', 'Answered')),
        -- an answered alert carries the decision; a "no" carries why
        CONSTRAINT CK_HospitalSurveillanceAlerts_Answer CHECK (Status <> 'Answered' OR (Response IS NOT NULL AND AnsweredAt IS NOT NULL)),
        CONSTRAINT CK_HospitalSurveillanceAlerts_Response CHECK (Response IS NULL OR Response IN ('BundleStarted', 'NotSepsis', 'ProphylaxisOrdered', 'NotIndicated', 'Contraindicated'))
    );
    CREATE INDEX IX_HospitalSurveillanceAlerts_CompanyId_Status_Kind ON dbo.HospitalSurveillanceAlerts (CompanyId, Status, Kind);
    CREATE INDEX IX_HospitalSurveillanceAlerts_CompanyId_AdmissionId_Kind ON dbo.HospitalSurveillanceAlerts (CompanyId, AdmissionId, Kind);
    CREATE INDEX IX_HospitalSurveillanceAlerts_CompanyId_PatientId ON dbo.HospitalSurveillanceAlerts (CompanyId, PatientId);
END

COMMIT TRANSACTION;
