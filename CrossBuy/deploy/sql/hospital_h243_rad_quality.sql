-- ==========================================================================================
-- Hospital H243 - radiology quality: the structured category of a study with its tracked follow-up, and the
-- radiation dose of an exam with its effective-dose estimate.
--
--   HospitalRadFollowUps   (new)
--   HospitalRadDoses       (new)
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 52043, 'Run the hospital radiology slice first.', 1;

IF OBJECT_ID('dbo.HospitalRadFollowUps','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadFollowUps (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        OrderId int NOT NULL,
        PatientId int NOT NULL,
        System nvarchar(12) NOT NULL,
        Category nvarchar(10) NOT NULL,
        Recommendation nvarchar(300) NULL,
        DueDate date NULL,
        Status nvarchar(8) NOT NULL,
        AnsweredByOrderId int NULL,
        Note nvarchar(300) NULL,
        CreatedAt datetime2 NOT NULL,
        CreatedByEmployeeId int NULL,
        ClosedAt datetime2 NULL,
        CONSTRAINT PK_HospitalRadFollowUps PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRadFollowUps_Order FOREIGN KEY (OrderId) REFERENCES dbo.HospitalRadOrders (ID),
        CONSTRAINT FK_HospitalRadFollowUps_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID),
        CONSTRAINT CK_HospitalRadFollowUps_Status CHECK (Status IN ('Open','Done','Closed'))
    );
    CREATE UNIQUE INDEX IX_HospitalRadFollowUps_CompanyId_OrderId ON dbo.HospitalRadFollowUps (CompanyId, OrderId);
    CREATE INDEX IX_HospitalRadFollowUps_CompanyId_Status_DueDate ON dbo.HospitalRadFollowUps (CompanyId, Status, DueDate);
    CREATE INDEX IX_HospitalRadFollowUps_CompanyId_PatientId ON dbo.HospitalRadFollowUps (CompanyId, PatientId);
END

IF OBJECT_ID('dbo.HospitalRadDoses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadDoses (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        OrderId int NOT NULL,
        PatientId int NOT NULL,
        Region nvarchar(15) NOT NULL,
        CtdiVol decimal(9,2) NULL,
        Dlp decimal(9,1) NULL,
        Dap decimal(9,2) NULL,
        EffectiveMsv decimal(7,2) NULL,
        AboveReference bit NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalRadDoses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRadDoses_Order FOREIGN KEY (OrderId) REFERENCES dbo.HospitalRadOrders (ID),
        CONSTRAINT FK_HospitalRadDoses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRadDoses_CompanyId_OrderId ON dbo.HospitalRadDoses (CompanyId, OrderId);
    CREATE INDEX IX_HospitalRadDoses_CompanyId_PatientId ON dbo.HospitalRadDoses (CompanyId, PatientId);
END

COMMIT TRANSACTION;
