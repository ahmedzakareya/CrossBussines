-- ==========================================================================================
-- Hospital H26 — a doctor's weekly schedule (sessions per weekday, each with its minutes per patient; a morning and
-- an evening clinic on one day) and leave (whole days, or part of each day). A doctor without session rows keeps
-- booking by H4's single window (WorkDays + SessionStart/End + SlotMinutes) until the weekly schedule is started.
--
-- TWO NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalPractitionerSessions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPractitionerSessions (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPractitionerSessions PRIMARY KEY,
        CompanyId       int      NOT NULL,
        PractitionerId  int      NOT NULL,
        DayOfWeek       tinyint  NOT NULL,
        StartMinute     int      NOT NULL,
        EndMinute       int      NOT NULL,
        SlotMinutes     int      NOT NULL,
        CONSTRAINT CK_HospitalPractitionerSessions_Day CHECK (DayOfWeek <= 6),
        CONSTRAINT CK_HospitalPractitionerSessions_Times CHECK (StartMinute >= 0 AND EndMinute <= 1440 AND SlotMinutes BETWEEN 5 AND 120 AND StartMinute + SlotMinutes <= EndMinute),
        CONSTRAINT FK_HospitalPractitionerSessions_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE INDEX IX_HospitalPractitionerSessions_CompanyId_PractitionerId_DayOfWeek ON dbo.HospitalPractitionerSessions (CompanyId, PractitionerId, DayOfWeek);
END

IF OBJECT_ID('dbo.HospitalPractitionerLeaves','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPractitionerLeaves (
        ID                   int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPractitionerLeaves PRIMARY KEY,
        CompanyId            int            NOT NULL,
        PractitionerId       int            NOT NULL,
        FromDate             datetime2      NOT NULL,
        ToDate               datetime2      NOT NULL,
        StartMinute          int            NULL,
        EndMinute            int            NULL,
        Reason               nvarchar(200)  NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalPractitionerLeaves_Dates CHECK (ToDate >= FromDate),
        -- whole days (no times) or part of each day (both times, start before end)
        CONSTRAINT CK_HospitalPractitionerLeaves_Times CHECK ((StartMinute IS NULL AND EndMinute IS NULL) OR (StartMinute >= 0 AND EndMinute <= 1440 AND StartMinute < EndMinute)),
        CONSTRAINT FK_HospitalPractitionerLeaves_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID)
    );
    CREATE INDEX IX_HospitalPractitionerLeaves_CompanyId_PractitionerId_FromDate ON dbo.HospitalPractitionerLeaves (CompanyId, PractitionerId, FromDate);
END

COMMIT TRANSACTION;
