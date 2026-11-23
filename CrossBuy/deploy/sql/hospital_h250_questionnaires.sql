-- ==========================================================================================
-- Hospital H250 - patient questionnaires (PHQ-9, GAD-7, AUDIT-C) sent by the clinic, answered on the portal, scored.
--
--   HospitalQuestionnaires   (new)
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 52050, 'Run the hospital slices first.', 1;

IF OBJECT_ID('dbo.HospitalQuestionnaires','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalQuestionnaires (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Instrument nvarchar(10) NOT NULL,
        Status nvarchar(10) NOT NULL,
        SentAt datetime2 NOT NULL,
        SentByEmployeeId int NULL,
        DueDate date NULL,
        Answers nvarchar(100) NULL,
        Score int NULL,
        Severity nvarchar(20) NULL,
        Urgent bit NOT NULL,
        CompletedAt datetime2 NULL,
        CompletedByAccountId int NULL,
        ReviewedAt datetime2 NULL,
        ReviewedByEmployeeId int NULL,
        CONSTRAINT PK_HospitalQuestionnaires PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalQuestionnaires_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalQuestionnaires_Status CHECK (Status IN ('Pending','Completed','Cancelled'))
    );
    CREATE INDEX IX_HospitalQuestionnaires_CompanyId_PatientId_Status ON dbo.HospitalQuestionnaires (CompanyId, PatientId, Status);
    CREATE INDEX IX_HospitalQuestionnaires_CompanyId_Status_Urgent ON dbo.HospitalQuestionnaires (CompanyId, Status, Urgent);
END

COMMIT TRANSACTION;
