-- ==========================================================================================
-- Hospital H184 - dental work marked done by mistake, taken back.
--
--   HospitalDentalCorrections   (new) what the line was, why it was taken back, and the bill's reversal
--                               (the open bill's line removed, or the closed bill's credit note)
--
-- A new table; nothing existing changes meaning. BillLineId keeps no foreign key: an open bill's line is removed by
-- the correction itself, and the row still names it. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDentalPlanLines','U') IS NULL OR OBJECT_ID('dbo.HospitalDentalSessions','U') IS NULL OR OBJECT_ID('dbo.HospitalCreditNotes','U') IS NULL
    THROW 51984, 'Run the dental session and credit note slices (hospital_h179_dental_sessions.sql, hospital_h148_credit_notes.sql) first.', 1;

IF OBJECT_ID('dbo.HospitalDentalCorrections','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDentalCorrections (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        PlanId int NOT NULL,
        LineId int NOT NULL,
        ServiceId int NOT NULL,
        Tooth int NULL,
        Fee decimal(18,4) NOT NULL,
        CompletedAt datetime2 NULL,
        CompletedByEmployeeId int NULL,
        SessionId int NULL,
        BillLineId int NULL,
        Reversed decimal(18,4) NULL,
        CreditNoteId int NULL,
        Reason nvarchar(300) NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NOT NULL,
        CONSTRAINT PK_HospitalDentalCorrections PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDentalCorrections_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT FK_HospitalDentalCorrections_Line FOREIGN KEY (LineId) REFERENCES dbo.HospitalDentalPlanLines (ID),
        CONSTRAINT FK_HospitalDentalCorrections_CreditNote FOREIGN KEY (CreditNoteId) REFERENCES dbo.HospitalCreditNotes (ID)
    );
    CREATE INDEX IX_HospitalDentalCorrections_CompanyId_PlanId ON dbo.HospitalDentalCorrections (CompanyId, PlanId);
END

COMMIT TRANSACTION;
