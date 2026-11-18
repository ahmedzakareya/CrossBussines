-- ==========================================================================================
-- Hospital H200 - electronic claims (HL7 FHIR R4): every file of a claim batch, sent or received.
--
--   HospitalClaimExchanges   (new) the claims file sent to the payer and the payer's answer files, as they were
--
-- New table only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalClaimBatches','U') IS NULL
    THROW 52000, 'Run the insurance slice first.', 1;

IF OBJECT_ID('dbo.HospitalClaimExchanges','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalClaimExchanges (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        BatchId int NOT NULL,
        Direction nvarchar(10) NOT NULL,
        Format nvarchar(20) NOT NULL,
        Raw nvarchar(max) NOT NULL,
        Applied int NOT NULL,
        Skipped int NOT NULL,
        Detail nvarchar(1000) NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalClaimExchanges PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalClaimExchanges_Batch FOREIGN KEY (BatchId) REFERENCES dbo.HospitalClaimBatches (ID),
        CONSTRAINT CK_HospitalClaimExchanges_Direction CHECK (Direction IN ('Sent', 'Received'))
    );
    CREATE INDEX IX_HospitalClaimExchanges_CompanyId_BatchId ON dbo.HospitalClaimExchanges (CompanyId, BatchId);
END

COMMIT TRANSACTION;
