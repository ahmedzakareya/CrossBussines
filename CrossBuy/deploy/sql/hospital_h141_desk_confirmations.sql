-- ==========================================================================================
-- Hospital H141 - the front desk confirms tomorrow's appointments: what each patient said on the phone (confirmed, no
-- answer, wants to move), how many calls, when and by whom. One row per appointment.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
    THROW 51990, 'Run hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDeskConfirmations','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeskConfirmations (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        AppointmentId int NOT NULL,
        Status nvarchar(20) NOT NULL,
        Note nvarchar(300) NULL,
        Calls int NOT NULL,
        LastAt datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalDeskConfirmations PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDeskConfirmations_Status CHECK (Status IN (N'Confirmed', N'NoAnswer', N'WantsToMove'))
    );
    CREATE UNIQUE INDEX IX_HospitalDeskConfirmations_CompanyId_AppointmentId ON dbo.HospitalDeskConfirmations (CompanyId, AppointmentId);
END

COMMIT TRANSACTION;
