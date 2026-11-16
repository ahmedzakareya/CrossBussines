-- ==========================================================================================
-- Hospital H143 - the patient's deposit at the counter: taken into the cashier's shift, used on a bill, refunded.
--   · HospitalPatientDeposits: one row per deposit (what was taken, how, in which shift; how much was used / refunded);
--   · HospitalDepositMoves: each use on a bill and each refund;
--   · HospitalBillingSettings.PatientDepositAccountId: the liability account the owner maps (nullable; unmapped = no
--     deposit is taken).
--
-- Two new tables and one new nullable column; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBillingSettings','U') IS NULL OR OBJECT_ID('dbo.HospitalCashierShifts','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql first.', 1;

IF COL_LENGTH('dbo.HospitalBillingSettings', 'PatientDepositAccountId') IS NULL
    ALTER TABLE dbo.HospitalBillingSettings ADD PatientDepositAccountId int NULL;

IF OBJECT_ID('dbo.HospitalPatientDeposits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPatientDeposits (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DepositNo nvarchar(20) NOT NULL,
        PatientId int NOT NULL,
        Purpose nvarchar(20) NOT NULL,
        Note nvarchar(300) NULL,
        Amount decimal(18,4) NOT NULL,
        Method nvarchar(20) NOT NULL,
        ShiftId int NOT NULL,
        CashierEmployeeId int NOT NULL,
        ReceivedAt datetime2 NOT NULL,
        JournalEntryId int NULL,
        Used decimal(18,4) NOT NULL,
        Refunded decimal(18,4) NOT NULL,
        CONSTRAINT PK_HospitalPatientDeposits PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalPatientDeposits_Purpose CHECK (Purpose IN (N'General', N'Procedure', N'Admission')),
        CONSTRAINT CK_HospitalPatientDeposits_Amounts CHECK (Amount > 0 AND Used >= 0 AND Refunded >= 0 AND Used + Refunded <= Amount)
    );
    CREATE UNIQUE INDEX IX_HospitalPatientDeposits_CompanyId_DepositNo ON dbo.HospitalPatientDeposits (CompanyId, DepositNo);
    CREATE INDEX IX_HospitalPatientDeposits_CompanyId_PatientId ON dbo.HospitalPatientDeposits (CompanyId, PatientId);
    CREATE INDEX IX_HospitalPatientDeposits_CompanyId_ShiftId ON dbo.HospitalPatientDeposits (CompanyId, ShiftId);
END

IF OBJECT_ID('dbo.HospitalDepositMoves','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDepositMoves (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        DepositId int NOT NULL,
        Kind nvarchar(10) NOT NULL,
        Amount decimal(18,4) NOT NULL,
        BillId int NULL,
        PaymentId int NULL,
        Method nvarchar(20) NULL,
        ShiftId int NULL,
        Reason nvarchar(300) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NOT NULL,
        JournalEntryId int NULL,
        CONSTRAINT PK_HospitalDepositMoves PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDepositMoves_Kind CHECK (Kind IN (N'Use', N'Refund')),
        CONSTRAINT CK_HospitalDepositMoves_Amount CHECK (Amount > 0)
    );
    CREATE INDEX IX_HospitalDepositMoves_CompanyId_DepositId ON dbo.HospitalDepositMoves (CompanyId, DepositId);
    CREATE INDEX IX_HospitalDepositMoves_CompanyId_ShiftId ON dbo.HospitalDepositMoves (CompanyId, ShiftId);
END

COMMIT TRANSACTION;
