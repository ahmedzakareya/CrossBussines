-- ==========================================================================================
-- Hospital H201 - bedside monitors (HL7 v2 ORU^R01 vital signs), waiting for a nurse to chart or discard.
--
--   HospitalDeviceVitals   (new) every set a monitor sent, as it came, with the patient, the values and its fate
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalObservations','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL
    THROW 52001, 'Run the observations slice (hospital_h71_observations.sql) first.', 1;

IF OBJECT_ID('dbo.HospitalDeviceVitals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeviceVitals (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Device nvarchar(60) NOT NULL,
        MessageId nvarchar(60) NOT NULL,
        BedCode nvarchar(30) NULL,
        Mrn nvarchar(30) NULL,
        AdmissionId int NULL,
        PatientId int NULL,
        TakenAt datetime2 NULL,
        HeartRate int NULL,
        SpO2 int NULL,
        RespRate int NULL,
        SystolicBp int NULL,
        DiastolicBp int NULL,
        TempC decimal(4,1) NULL,
        Status nvarchar(12) NOT NULL,
        Detail nvarchar(500) NULL,
        Raw nvarchar(max) NOT NULL,
        ReceivedAt datetime2 NOT NULL,
        ReceivedByEmployeeId int NULL,
        DecidedAt datetime2 NULL,
        DecidedByEmployeeId int NULL,
        ObservationId int NULL,
        CONSTRAINT PK_HospitalDeviceVitals PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDeviceVitals_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions (ID),
        CONSTRAINT FK_HospitalDeviceVitals_Observation FOREIGN KEY (ObservationId) REFERENCES dbo.HospitalObservations (ID),
        CONSTRAINT CK_HospitalDeviceVitals_Status CHECK (Status IN ('Pending', 'Accepted', 'Discarded', 'Refused', 'Duplicate'))
    );
    CREATE INDEX IX_HospitalDeviceVitals_CompanyId_Device_MessageId ON dbo.HospitalDeviceVitals (CompanyId, Device, MessageId);
    CREATE INDEX IX_HospitalDeviceVitals_CompanyId_Status_ReceivedAt ON dbo.HospitalDeviceVitals (CompanyId, Status, ReceivedAt);
END

COMMIT TRANSACTION;
