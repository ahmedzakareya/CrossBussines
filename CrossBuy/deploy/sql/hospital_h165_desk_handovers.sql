-- ==========================================================================================
-- Hospital H165 - the desk's shift handover: the work passed to a named colleague, with a note and the cash as the closed
-- cashier shift left it; acknowledged by the receiver.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalDeskHandovers','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDeskHandovers (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        HandoverNo nvarchar(20) NOT NULL,
        FromEmployeeId int NOT NULL,
        ToEmployeeId int NOT NULL,
        At datetime2 NOT NULL,
        Note nvarchar(500) NULL,
        QueueItems int NOT NULL CONSTRAINT DF_HospitalDeskHandovers_Queue DEFAULT (0),
        Gaps int NOT NULL CONSTRAINT DF_HospitalDeskHandovers_Gaps DEFAULT (0),
        Overdue int NOT NULL CONSTRAINT DF_HospitalDeskHandovers_Overdue DEFAULT (0),
        OpenHandoffs int NOT NULL CONSTRAINT DF_HospitalDeskHandovers_Handoffs DEFAULT (0),
        ShiftId int NULL,
        CashExpected decimal(18,3) NULL,
        CashCounted decimal(18,3) NULL,
        CashVariance decimal(18,3) NULL,
        AcknowledgedAt datetime2 NULL,
        CONSTRAINT PK_HospitalDeskHandovers PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalDeskHandovers_People CHECK (FromEmployeeId <> ToEmployeeId)
    );
    CREATE UNIQUE INDEX IX_HospitalDeskHandovers_CompanyId_HandoverNo ON dbo.HospitalDeskHandovers (CompanyId, HandoverNo);
    CREATE INDEX IX_HospitalDeskHandovers_CompanyId_ToEmployeeId_AcknowledgedAt ON dbo.HospitalDeskHandovers (CompanyId, ToEmployeeId, AcknowledgedAt);
END

COMMIT TRANSACTION;
