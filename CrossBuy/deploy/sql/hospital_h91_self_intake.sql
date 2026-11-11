-- ==========================================================================================
-- Hospital H91 — reception self-intake. The patient's own details from the reception tablet, provisional until the
-- desk links them to an existing file or opens a new one.
--   HospitalSelfIntakes   one intake: the danger signs, identity, visit, consent, and the desk's decision
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSelfIntakes','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSelfIntakes (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int            NOT NULL,
        IntakeNo                nvarchar(20)   NOT NULL,
        TicketNo                nvarchar(10)   NOT NULL,
        TicketDate              date           NOT NULL,
        Status                  nvarchar(10)   NOT NULL,
        Urgent                  bit            NOT NULL,
        RedFlags                nvarchar(150)  NULL,
        AlertAckAt              datetime2      NULL,
        AlertAckByEmployeeId    int            NULL,
        Language                nvarchar(5)    NOT NULL,
        VisitType               nvarchar(12)   NOT NULL,
        IdType                  nvarchar(12)   NULL,
        IdNumber                nvarchar(30)   NULL,
        Name                    nvarchar(200)  NULL,
        NameEn                  nvarchar(200)  NULL,
        BirthDate               date           NULL,
        Sex                     nvarchar(1)    NULL,
        Phone                   nvarchar(20)   NULL,
        Reason                  nvarchar(300)  NULL,
        Allergies               nvarchar(300)  NULL,
        Medications             nvarchar(300)  NULL,
        Pregnant                bit            NULL,
        Infection               nvarchar(60)   NULL,
        EmergencyName           nvarchar(100)  NULL,
        EmergencyPhone          nvarchar(20)   NULL,
        ConsentVersion          nvarchar(10)   NULL,
        ConsentAt               datetime2      NULL,
        SubmittedAt             datetime2      NOT NULL,
        ExpiresAt               datetime2      NOT NULL,
        ReconciledAt            datetime2      NULL,
        ReconciledByEmployeeId  int            NULL,
        PatientId               int            NULL,
        MatchScore              decimal(5,1)   NULL,
        MatchMethod             nvarchar(13)   NULL,
        DecisionNote            nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalSelfIntakes PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSelfIntakes_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalSelfIntakes_Status CHECK (Status IN ('Submitted','Linked','NewFile','Rejected','Expired')),
        CONSTRAINT CK_HospitalSelfIntakes_Visit CHECK (VisitType IN ('Appointment','WalkIn','Services')),
        CONSTRAINT CK_HospitalSelfIntakes_Done CHECK (Status NOT IN ('Linked','NewFile') OR (PatientId IS NOT NULL AND ReconciledAt IS NOT NULL)),
        -- an urgent intake may carry only the danger signs (the patient was sent to the desk before giving details)
        CONSTRAINT CK_HospitalSelfIntakes_Who CHECK (Urgent = 1 OR (Name IS NOT NULL AND BirthDate IS NOT NULL AND ConsentAt IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalSelfIntakes_CompanyId_IntakeNo ON dbo.HospitalSelfIntakes (CompanyId, IntakeNo);
    CREATE UNIQUE INDEX IX_HospitalSelfIntakes_CompanyId_TicketDate_TicketNo ON dbo.HospitalSelfIntakes (CompanyId, TicketDate, TicketNo);
    CREATE INDEX IX_HospitalSelfIntakes_CompanyId_Status ON dbo.HospitalSelfIntakes (CompanyId, Status);
END

COMMIT TRANSACTION;
