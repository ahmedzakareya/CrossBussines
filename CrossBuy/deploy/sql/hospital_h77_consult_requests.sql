-- ==========================================================================================
-- Hospital H77 — inpatient consultation requests. Phase 2 of the 2026-11-09 gap research.
--   HospitalConsultRequests  the attending's question to another specialty, its urgency, and the specialty's answer
--
-- Additive only (one new table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalDepartments','U') IS NULL OR OBJECT_ID('dbo.HospitalPractitioners','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql and hospital_h4_outpatient.sql first.', 1;

IF OBJECT_ID('dbo.HospitalConsultRequests','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConsultRequests (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int             NOT NULL,
        RequestNo               nvarchar(20)    NOT NULL,
        AdmissionId             int             NOT NULL,
        PatientId               int             NOT NULL,
        DepartmentId            int             NOT NULL,
        PractitionerId          int             NULL,
        Urgency                 nvarchar(10)    NOT NULL,
        Question                nvarchar(1000)  NOT NULL,
        Status                  nvarchar(10)    NOT NULL,
        RequestedAt             datetime2       NOT NULL,
        RequestedByEmployeeId   int             NULL,
        DueAt                   datetime2       NOT NULL,
        AcceptedAt              datetime2       NULL,
        AcceptedByEmployeeId    int             NULL,
        AnsweredAt              datetime2       NULL,
        AnsweredByEmployeeId    int             NULL,
        Findings                nvarchar(2000)  NULL,
        Recommendations         nvarchar(2000)  NULL,
        FollowUp                bit             NOT NULL,
        CancelledAt             datetime2       NULL,
        CancelledByEmployeeId   int             NULL,
        CancelReason            nvarchar(300)   NULL,
        CONSTRAINT PK_HospitalConsultRequests PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalConsultRequests_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalConsultRequests_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalConsultRequests_Department FOREIGN KEY (DepartmentId) REFERENCES dbo.HospitalDepartments(ID),
        CONSTRAINT FK_HospitalConsultRequests_Practitioner FOREIGN KEY (PractitionerId) REFERENCES dbo.HospitalPractitioners(ID),
        CONSTRAINT CK_HospitalConsultRequests_Urgency CHECK (Urgency IN ('Emergency','Urgent','Routine')),
        CONSTRAINT CK_HospitalConsultRequests_Status CHECK (Status IN ('Requested','Accepted','Answered','Cancelled')),
        -- an answer carries its recommendations; a cancellation its reason
        CONSTRAINT CK_HospitalConsultRequests_Answered CHECK (Status <> 'Answered' OR (AnsweredAt IS NOT NULL AND Recommendations IS NOT NULL)),
        CONSTRAINT CK_HospitalConsultRequests_Cancelled CHECK (Status <> 'Cancelled' OR (CancelledAt IS NOT NULL AND CancelReason IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalConsultRequests_CompanyId_RequestNo ON dbo.HospitalConsultRequests (CompanyId, RequestNo);
    CREATE INDEX IX_HospitalConsultRequests_CompanyId_AdmissionId ON dbo.HospitalConsultRequests (CompanyId, AdmissionId);
    CREATE INDEX IX_HospitalConsultRequests_CompanyId_DepartmentId_Status ON dbo.HospitalConsultRequests (CompanyId, DepartmentId, Status);
END

COMMIT TRANSACTION;
