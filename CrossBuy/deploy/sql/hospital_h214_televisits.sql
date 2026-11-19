-- ==========================================================================================
-- Hospital H214 - video visits: a booked appointment held over video between the doctor and the patient (portal),
-- and the set-up messages of the call (the call itself is peer to peer and never stored).
--
--   HospitalTeleVisits    (new) one per appointment
--   HospitalTeleSignals   (new) offer / answer / candidates passed between the two sides
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 52014, 'Run the outpatient slice first.', 1;

IF OBJECT_ID('dbo.HospitalTeleVisits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalTeleVisits (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AppointmentId int NOT NULL,
        PatientId int NOT NULL,
        PractitionerId int NOT NULL,
        Status nvarchar(10) NOT NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        DoctorJoinedAt datetime2 NULL,
        PatientJoinedAt datetime2 NULL,
        EndedAt datetime2 NULL,
        EndedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalTeleVisits PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalTeleVisits_Appointment FOREIGN KEY (AppointmentId) REFERENCES dbo.HospitalAppointments (ID),
        CONSTRAINT FK_HospitalTeleVisits_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalTeleVisits_Status CHECK (Status IN ('Scheduled', 'Live', 'Ended'))
    );
    CREATE UNIQUE INDEX IX_HospitalTeleVisits_CompanyId_AppointmentId ON dbo.HospitalTeleVisits (CompanyId, AppointmentId);
    CREATE INDEX IX_HospitalTeleVisits_CompanyId_PatientId ON dbo.HospitalTeleVisits (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalTeleSignals','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalTeleSignals (
        ID bigint IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        VisitId int NOT NULL,
        FromSide nvarchar(1) NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Payload nvarchar(max) NULL,
        At datetime2 NOT NULL,
        CONSTRAINT PK_HospitalTeleSignals PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalTeleSignals_Visit FOREIGN KEY (VisitId) REFERENCES dbo.HospitalTeleVisits (ID),
        CONSTRAINT CK_HospitalTeleSignals_Side CHECK (FromSide IN ('D', 'P')),
        CONSTRAINT CK_HospitalTeleSignals_Kind CHECK (Kind IN ('hello', 'offer', 'answer', 'ice', 'bye'))
    );
    CREATE INDEX IX_HospitalTeleSignals_CompanyId_VisitId_ID ON dbo.HospitalTeleSignals (CompanyId, VisitId, ID);
END

COMMIT TRANSACTION;
