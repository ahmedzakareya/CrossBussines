-- ==========================================================================================
-- Hospital H31 — the pictures of an imaging order. The bytes live in the platform's private document storage
-- (App_Data/documents, not wwwroot); this table holds which order they belong to and the opaque key that names them.
--
-- ONE NEW TABLE. Nothing existing is altered. Idempotent by construction (an IF OBJECT_ID guard).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 51990, 'Run hospital_h9_radiology.sql first.', 1;

IF OBJECT_ID('dbo.HospitalRadImages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadImages (
        ID                    int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalRadImages PRIMARY KEY,
        CompanyId             int            NOT NULL,
        RadOrderId            int            NOT NULL,
        StorageKey            nvarchar(32)   NOT NULL,
        FileName              nvarchar(200)  NOT NULL,
        ContentType           nvarchar(60)   NOT NULL,
        SizeBytes             int            NOT NULL,
        Caption               nvarchar(200)  NULL,
        UploadedAt            datetime2      NOT NULL,
        UploadedByEmployeeId  int            NULL,
        CONSTRAINT CK_HospitalRadImages_Type CHECK (ContentType IN (N'image/jpeg', N'image/png', N'image/webp')),
        CONSTRAINT FK_HospitalRadImages_Order FOREIGN KEY (RadOrderId) REFERENCES dbo.HospitalRadOrders(ID)
    );
    CREATE INDEX IX_HospitalRadImages_CompanyId_RadOrderId ON dbo.HospitalRadImages (CompanyId, RadOrderId);
END

COMMIT TRANSACTION;
