-- ==========================================================================================
-- Hospital H159 - check-in gaps: a check-in step not finished at the counter becomes a follow-up with an owner (or
-- unassigned), a reason and a due date; it closes itself when the file shows the step done, or is closed / waived.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51994, 'Run the hospital patient slices first.', 1;

IF OBJECT_ID('dbo.HospitalCheckinGaps','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCheckinGaps (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Item nvarchar(20) NOT NULL,
        Reason nvarchar(300) NOT NULL,
        OwnerEmployeeId int NULL,
        DueDate date NOT NULL,
        Status nvarchar(10) NOT NULL,
        OpenedAt datetime2 NOT NULL,
        OpenedByEmployeeId int NULL,
        ClosedAt datetime2 NULL,
        ClosedByEmployeeId int NULL,
        CloseNote nvarchar(300) NULL,
        CONSTRAINT PK_HospitalCheckinGaps PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalCheckinGaps_Status CHECK (Status IN ('Open','Done','Waived')),
        CONSTRAINT CK_HospitalCheckinGaps_Item CHECK (Item IN ('identity','payer','phone','photo','consent','balance','document'))
    );
    CREATE INDEX IX_HospitalCheckinGaps_CompanyId_Status_DueDate ON dbo.HospitalCheckinGaps (CompanyId, Status, DueDate);
    CREATE INDEX IX_HospitalCheckinGaps_CompanyId_PatientId ON dbo.HospitalCheckinGaps (CompanyId, PatientId);
END

COMMIT TRANSACTION;
