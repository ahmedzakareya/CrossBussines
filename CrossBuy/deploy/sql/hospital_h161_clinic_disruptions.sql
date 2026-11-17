-- ==========================================================================================
-- Hospital H161 - clinic disruptions: a doctor late or absent, a room change, a device down; the patients booked in the
-- window with their plan (keep / move / cancel), the message sent and the calls made until each one is reached.
--
-- Two new tables; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 51992, 'Run the hospital outpatient slices first.', 1;

IF OBJECT_ID('dbo.HospitalClinicDisruptions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClinicDisruptions (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DisruptionNo nvarchar(20) NOT NULL,
        PractitionerId int NOT NULL,
        Day date NOT NULL,
        Kind nvarchar(10) NOT NULL,
        FromMinute int NOT NULL,
        ToMinute int NOT NULL,
        DelayMinutes int NULL,
        Room nvarchar(60) NULL,
        Equipment nvarchar(120) NULL,
        Note nvarchar(300) NULL,
        Status nvarchar(10) NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        AppliedAt datetime2 NULL,
        AppliedByEmployeeId int NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalClinicDisruptions PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalClinicDisruptions_Kind CHECK (Kind IN ('Late','Absent','Room','Equipment')),
        CONSTRAINT CK_HospitalClinicDisruptions_Status CHECK (Status IN ('Open','Applied','Closed'))
    );
    CREATE UNIQUE INDEX IX_HospitalClinicDisruptions_CompanyId_DisruptionNo ON dbo.HospitalClinicDisruptions (CompanyId, DisruptionNo);
    CREATE INDEX IX_HospitalClinicDisruptions_CompanyId_Day_PractitionerId ON dbo.HospitalClinicDisruptions (CompanyId, Day, PractitionerId);
END

IF OBJECT_ID('dbo.HospitalDisruptionPatients','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDisruptionPatients (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DisruptionId int NOT NULL,
        AppointmentId int NOT NULL,
        PatientId int NOT NULL,
        PlanKind nvarchar(10) NOT NULL,
        NewPractitionerId int NULL,
        NewDay date NULL,
        NewMinute int NULL,
        NewAppointmentId int NULL,
        AppliedAt datetime2 NULL,
        ApplyError nvarchar(200) NULL,
        SmsMessageId int NULL,
        Reached bit NULL,
        Calls int NOT NULL CONSTRAINT DF_HospitalDisruptionPatients_Calls DEFAULT (0),
        LastCallAt datetime2 NULL,
        LastCallByEmployeeId int NULL,
        CallNote nvarchar(300) NULL,
        CONSTRAINT PK_HospitalDisruptionPatients PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDisruptionPatients_Disruption FOREIGN KEY (DisruptionId) REFERENCES dbo.HospitalClinicDisruptions (ID),
        CONSTRAINT CK_HospitalDisruptionPatients_Plan CHECK (PlanKind IN ('Keep','Move','Cancel'))
    );
    CREATE UNIQUE INDEX IX_HospitalDisruptionPatients_CompanyId_DisruptionId_AppointmentId ON dbo.HospitalDisruptionPatients (CompanyId, DisruptionId, AppointmentId);
    CREATE INDEX IX_HospitalDisruptionPatients_CompanyId_PatientId ON dbo.HospitalDisruptionPatients (CompanyId, PatientId);
END

COMMIT TRANSACTION;
