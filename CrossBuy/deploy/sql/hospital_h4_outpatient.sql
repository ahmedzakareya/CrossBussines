-- ==========================================================================================
-- Hospital H4 — outpatient: departments, practitioners, appointments.
--
-- A PRACTITIONER is one clinical record per doctor (optionally the employee they are, for the
-- photo and the payroll), with the days and hours they see patients, their clinic room and
-- their professional licence: a licence past its expiry stops new bookings.
--
-- An APPOINTMENT is a patient with a practitioner on a DATE at a MINUTE OF THE DAY — wall-clock
-- clinic time, deliberately not an instant, so a booking never shifts with a time zone. Its
-- arrival, start and finish ARE instants (UTC), because they are measured, not planned.
--
-- The database keeps the race rule: one live appointment per practitioner per slot
-- (filtered unique index; a cancelled booking frees the slot).
--
-- THREE NEW TABLES. Idempotent: safe to run repeatedly.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDepartments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDepartments (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalDepartments PRIMARY KEY,
        CompanyId           int            NOT NULL,
        Code                nvarchar(40)   NOT NULL,
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NULL,
        SortOrder           int            NOT NULL CONSTRAINT DF_HospitalDepartments_Sort DEFAULT(0),
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalDepartments_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalDepartments_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL
    );
    CREATE UNIQUE INDEX UX_HospitalDepartments_Code ON dbo.HospitalDepartments (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPractitioners (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalPractitioners PRIMARY KEY,
        CompanyId           int            NOT NULL,
        EmployeeId          int            NULL,   -- Employee.ID when the doctor is one (photo, payroll)
        Name                nvarchar(200)  NOT NULL,
        NameEn              nvarchar(200)  NOT NULL,
        Title               nvarchar(200)  NULL,
        TitleEn             nvarchar(200)  NULL,
        DepartmentId        int            NULL,
        Engagement          nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalPractitioners_Eng DEFAULT('Permanent'),
        PayChannel          nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalPractitioners_Pay DEFAULT('Payroll'),
        LicenseNumber       nvarchar(60)   NULL,
        LicenseExpiry       datetime2      NULL,
        Privileges          nvarchar(200)  NULL,   -- comma-separated: opd,ip,proc,surg,er,anes
        WorkDays            nvarchar(20)   NULL,   -- comma-separated DayOfWeek numbers, 0 = Sunday
        SessionStartMinute  int            NOT NULL CONSTRAINT DF_HospitalPractitioners_Start DEFAULT(540),
        SessionEndMinute    int            NOT NULL CONSTRAINT DF_HospitalPractitioners_End DEFAULT(840),
        SlotMinutes         int            NOT NULL CONSTRAINT DF_HospitalPractitioners_Slot DEFAULT(15),
        ClinicRoomId        int            NULL,   -- HospitalRooms.ID, a Clinic space
        IsActive            bit            NOT NULL CONSTRAINT DF_HospitalPractitioners_Active DEFAULT(1),
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalPractitioners_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalPractitioners_Eng CHECK (Engagement IN ('Permanent','PartTime','Visiting')),
        CONSTRAINT CK_HospitalPractitioners_Pay CHECK (PayChannel IN ('Payroll','Vendor')),
        CONSTRAINT CK_HospitalPractitioners_Session CHECK (SessionStartMinute >= 0 AND SessionEndMinute <= 1440 AND SessionStartMinute < SessionEndMinute),
        CONSTRAINT CK_HospitalPractitioners_Slot CHECK (SlotMinutes BETWEEN 5 AND 120)
    );
    CREATE INDEX IX_HospitalPractitioners_Department ON dbo.HospitalPractitioners (CompanyId, DepartmentId);
END

IF OBJECT_ID('dbo.HospitalAppointments','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalAppointments (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalAppointments PRIMARY KEY,
        CompanyId           int            NOT NULL,
        PatientId           int            NOT NULL,
        PractitionerId      int            NOT NULL,
        AppointmentDate     datetime2      NOT NULL,   -- the clinic day (date part only)
        StartMinute         int            NOT NULL,   -- wall-clock minute of that day
        DurationMinutes     int            NOT NULL,
        VisitType           nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalAppointments_Type DEFAULT('New'),
        Status              nvarchar(20)   NOT NULL CONSTRAINT DF_HospitalAppointments_Status DEFAULT('Booked'),
        QueueNumber         int            NULL,       -- given on arrival, per company per day
        Note                nvarchar(400)  NULL,
        ArrivedAt           datetime2      NULL,
        StartedAt           datetime2      NULL,
        FinishedAt          datetime2      NULL,
        CreatedAt           datetime2      NOT NULL CONSTRAINT DF_HospitalAppointments_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int            NULL,
        UpdatedAt           datetime2      NULL,
        UpdatedByEmployeeId int            NULL,
        CONSTRAINT CK_HospitalAppointments_Type CHECK (VisitType IN ('New','FollowUp','Procedure','Video')),
        CONSTRAINT CK_HospitalAppointments_Status CHECK (Status IN ('Booked','Arrived','InRoom','Done','NoShow','Cancelled')),
        CONSTRAINT CK_HospitalAppointments_Minute CHECK (StartMinute >= 0 AND StartMinute < 1440 AND DurationMinutes BETWEEN 5 AND 240)
    );
    CREATE UNIQUE INDEX UX_HospitalAppointments_OneLivePerSlot ON dbo.HospitalAppointments (CompanyId, PractitionerId, AppointmentDate, StartMinute) WHERE Status <> 'Cancelled';
    CREATE INDEX IX_HospitalAppointments_Day ON dbo.HospitalAppointments (CompanyId, AppointmentDate);
    CREATE INDEX IX_HospitalAppointments_Patient ON dbo.HospitalAppointments (CompanyId, PatientId, AppointmentDate);
END

IF COL_LENGTH('dbo.HospitalPractitioners','WorkDays') IS NULL OR COL_LENGTH('dbo.HospitalAppointments','StartMinute') IS NULL
    THROW 51931, 'Hospital outpatient tables exist with the wrong shape: schema mismatch.', 1;

COMMIT;
