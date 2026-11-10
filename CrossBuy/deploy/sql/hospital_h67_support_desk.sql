-- ==========================================================================================
-- Hospital H67 — housekeeping, porters and laundry: one desk of jobs (a cleaning, a patient moved, a laundry pickup),
-- taken by a member of staff and closed with a note, or cancelled with why. A bed left in Cleaning opens its own
-- terminal-cleaning job, and closing it turns the bed back to Vacant.
--   HospitalSupportJobs  one job and its whole path
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRooms','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql and hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSupportJobs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSupportJobs (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        JobNo                  nvarchar(20)   NOT NULL,
        Kind                   nvarchar(10)   NOT NULL,
        Source                 nvarchar(12)   NOT NULL,
        RoomId                 int            NULL,
        BedId                  int            NULL,
        PatientId              int            NULL,
        FromPlace              nvarchar(150)  NULL,
        ToPlace                nvarchar(150)  NULL,
        Bags                   int            NULL,
        Details                nvarchar(500)  NULL,
        Urgent                 bit            NOT NULL,
        Status                 nvarchar(10)   NOT NULL,
        RequestedAt            datetime2      NOT NULL,
        RequestedByEmployeeId  int            NULL,
        TakenByEmployeeId      int            NULL,
        TakenAt                datetime2      NULL,
        DoneAt                 datetime2      NULL,
        DoneNote               nvarchar(300)  NULL,
        CancelReason           nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalSupportJobs PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSupportJobs_Room FOREIGN KEY (RoomId) REFERENCES dbo.HospitalRooms(ID),
        CONSTRAINT FK_HospitalSupportJobs_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalSupportJobs_Kind CHECK (Kind IN ('Cleaning','Porter','Laundry')),
        CONSTRAINT CK_HospitalSupportJobs_Source CHECK (Source IN ('Manual','BedTurnover')),
        CONSTRAINT CK_HospitalSupportJobs_Status CHECK (Status IN ('Open','Taken','Done','Cancelled')),
        -- each kind names what it needs: a place to clean, a patient and two places to move, bags to collect
        CONSTRAINT CK_HospitalSupportJobs_What CHECK ((Kind = 'Cleaning' AND (RoomId IS NOT NULL OR BedId IS NOT NULL OR FromPlace IS NOT NULL))
            OR (Kind = 'Porter' AND FromPlace IS NOT NULL AND ToPlace IS NOT NULL)
            OR (Kind = 'Laundry' AND Bags IS NOT NULL AND Bags > 0 AND (RoomId IS NOT NULL OR FromPlace IS NOT NULL))),
        CONSTRAINT CK_HospitalSupportJobs_Path CHECK ((Status = 'Open' AND TakenByEmployeeId IS NULL)
            OR (Status = 'Taken' AND TakenByEmployeeId IS NOT NULL AND TakenAt IS NOT NULL)
            OR (Status = 'Done' AND TakenByEmployeeId IS NOT NULL AND DoneAt IS NOT NULL)
            OR (Status = 'Cancelled' AND CancelReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalSupportJobs_CompanyId_JobNo ON dbo.HospitalSupportJobs (CompanyId, JobNo);
    CREATE INDEX IX_HospitalSupportJobs_CompanyId_Status_Kind ON dbo.HospitalSupportJobs (CompanyId, Status, Kind);
    CREATE INDEX IX_HospitalSupportJobs_CompanyId_BedId ON dbo.HospitalSupportJobs (CompanyId, BedId);
END

COMMIT TRANSACTION;
