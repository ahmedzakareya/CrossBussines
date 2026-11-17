-- ==========================================================================================
-- Hospital H160 - checkout at the desk after the consultation: one row per visit the desk closed, with what was still
-- open at that moment (owed on the visit's bill, the follow-up advised and whether it was booked, orders waiting) and
-- the desk's word when something was left unresolved.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 51993, 'Run the hospital outpatient slices first.', 1;

IF OBJECT_ID('dbo.HospitalDeskCheckouts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeskCheckouts (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AppointmentId int NOT NULL,
        PatientId int NOT NULL,
        EncounterId int NULL,
        CompletedAt datetime2 NOT NULL,
        CompletedByEmployeeId int NULL,
        OwedAtClose decimal(18,3) NOT NULL CONSTRAINT DF_HospitalDeskCheckouts_Owed DEFAULT (0),
        FollowUpDays int NULL,
        FollowUpBooked bit NOT NULL CONSTRAINT DF_HospitalDeskCheckouts_Booked DEFAULT (0),
        OpenOrders int NOT NULL CONSTRAINT DF_HospitalDeskCheckouts_Orders DEFAULT (0),
        Note nvarchar(300) NULL,
        CONSTRAINT PK_HospitalDeskCheckouts PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalDeskCheckouts_CompanyId_AppointmentId ON dbo.HospitalDeskCheckouts (CompanyId, AppointmentId);
    CREATE INDEX IX_HospitalDeskCheckouts_CompanyId_PatientId ON dbo.HospitalDeskCheckouts (CompanyId, PatientId);
END

COMMIT TRANSACTION;
