-- ==========================================================================================
-- Hospital H158 - the reception desk's handoffs: why someone came, where the desk sent them, and whether the receiving
-- team accepted them (Sent -> Accepted -> Arrived, or Failed with the reason, or Closed at the desk).
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51995, 'Run the hospital patient slices first.', 1;

IF OBJECT_ID('dbo.HospitalDeskHandoffs','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeskHandoffs (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        HandoffNo nvarchar(20) NOT NULL,
        PatientId int NULL,
        Reason nvarchar(20) NOT NULL,
        Destination nvarchar(20) NOT NULL,
        LinkKind nvarchar(20) NULL,
        LinkId int NULL,
        Detail nvarchar(200) NULL,
        DetailEn nvarchar(200) NULL,
        ReferralFrom nvarchar(200) NULL,
        VisitorName nvarchar(120) NULL,
        VisitorRelation nvarchar(60) NULL,
        Note nvarchar(300) NULL,
        Status nvarchar(10) NOT NULL,
        SentAt datetime2 NOT NULL,
        SentByEmployeeId int NULL,
        AcceptedAt datetime2 NULL,
        AcceptedByEmployeeId int NULL,
        ArrivedAt datetime2 NULL,
        ArrivedByEmployeeId int NULL,
        FailedAt datetime2 NULL,
        FailedByEmployeeId int NULL,
        FailReason nvarchar(300) NULL,
        RemindedAt datetime2 NULL,
        Reminders int NOT NULL CONSTRAINT DF_HospitalDeskHandoffs_Reminders DEFAULT (0),
        CONSTRAINT PK_HospitalDeskHandoffs PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDeskHandoffs_Status CHECK (Status IN ('Sent','Accepted','Arrived','Failed','Closed'))
    );
    CREATE UNIQUE INDEX IX_HospitalDeskHandoffs_CompanyId_HandoffNo ON dbo.HospitalDeskHandoffs (CompanyId, HandoffNo);
    CREATE INDEX IX_HospitalDeskHandoffs_CompanyId_Status ON dbo.HospitalDeskHandoffs (CompanyId, Status);
    CREATE INDEX IX_HospitalDeskHandoffs_CompanyId_PatientId ON dbo.HospitalDeskHandoffs (CompanyId, PatientId);
END

COMMIT TRANSACTION;
