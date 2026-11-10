-- ==========================================================================================
-- Hospital H68 — on-call: who a department calls outside the sessions, first on call and the backup above them,
-- on spans of ward time (a night shift runs past midnight). The lab's critical-result escalation reads it.
--   HospitalOnCallShifts  one shift (cancelled with why, never deleted)
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL OR OBJECT_ID('dbo.HospitalDepartments','U') IS NULL
    THROW 51990, 'Run hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalOnCallShifts','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalOnCallShifts (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        DepartmentId           int            NOT NULL,
        PractitionerId         int            NOT NULL,
        Level                  nvarchar(10)   NOT NULL,
        StartsAt               datetime2      NOT NULL,
        EndsAt                 datetime2      NOT NULL,
        Note                   nvarchar(300)  NULL,
        CreatedAt              datetime2      NOT NULL,
        CreatedByEmployeeId    int            NULL,
        CancelledAt            datetime2      NULL,
        CancelledByEmployeeId  int            NULL,
        CancelReason           nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalOnCallShifts PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalOnCallShifts_Department FOREIGN KEY (DepartmentId) REFERENCES dbo.HospitalDepartments(ID),
        CONSTRAINT FK_HospitalOnCallShifts_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID),
        CONSTRAINT CK_HospitalOnCallShifts_Level CHECK (Level IN ('First','Backup')),
        -- a shift ends after it starts, at most a day later
        CONSTRAINT CK_HospitalOnCallShifts_Span CHECK (EndsAt > StartsAt AND DATEDIFF(minute, StartsAt, EndsAt) <= 1440),
        -- a cancelled shift says why
        CONSTRAINT CK_HospitalOnCallShifts_Cancel CHECK ((CancelledAt IS NULL AND CancelReason IS NULL) OR (CancelledAt IS NOT NULL AND CancelReason IS NOT NULL))
    );
    CREATE INDEX IX_HospitalOnCallShifts_CompanyId_DepartmentId_StartsAt ON dbo.HospitalOnCallShifts (CompanyId, DepartmentId, StartsAt);
    CREATE INDEX IX_HospitalOnCallShifts_CompanyId_PractitionerId_StartsAt ON dbo.HospitalOnCallShifts (CompanyId, PractitionerId, StartsAt);
END

COMMIT TRANSACTION;
