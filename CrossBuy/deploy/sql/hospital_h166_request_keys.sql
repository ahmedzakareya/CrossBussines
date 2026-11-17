-- ==========================================================================================
-- Hospital H166 - a desk action sent twice is done once: the answer to each keyed request is kept, and a repeat of the
-- same key gets that answer back.
--
-- One new table; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRequestKeys','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRequestKeys (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        RequestKey nvarchar(64) NOT NULL,
        Path nvarchar(200) NOT NULL,
        EmployeeId int NULL,
        StatusCode int NULL,
        Body nvarchar(max) NULL,
        CreatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalRequestKeys PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRequestKeys_CompanyId_RequestKey ON dbo.HospitalRequestKeys (CompanyId, RequestKey);
    CREATE INDEX IX_HospitalRequestKeys_CreatedAt ON dbo.HospitalRequestKeys (CreatedAt);
END

COMMIT TRANSACTION;
