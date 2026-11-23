-- ==========================================================================================
-- Hospital H259 - structured radiology reporting and radiologist peer review.
--
--   HospitalRadTemplates      (new)  report templates: sections in Arabic and English, which are required
--   HospitalRadTemplateUses   (new)  the template a study is reported on (its required sections gate signing)
--   HospitalRadPeerReviews    (new)  RADPEER scores of signed reports by a second radiologist
--
-- New tables only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 52059, 'Run the hospital radiology slice first.', 1;

IF OBJECT_ID('dbo.HospitalRadTemplates','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadTemplates (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        Code nvarchar(20) NOT NULL,
        Name nvarchar(100) NOT NULL,
        NameEn nvarchar(100) NOT NULL,
        Modality nvarchar(4) NULL,
        Sections nvarchar(2000) NOT NULL,
        Active bit NOT NULL,
        UpdatedAt datetime2 NOT NULL,
        CONSTRAINT PK_HospitalRadTemplates PRIMARY KEY (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRadTemplates_CompanyId_Code ON dbo.HospitalRadTemplates (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalRadTemplateUses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadTemplateUses (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        OrderId int NOT NULL,
        TemplateId int NOT NULL,
        At datetime2 NOT NULL,
        ByEmployeeId int NULL,
        CONSTRAINT PK_HospitalRadTemplateUses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRadTemplateUses_Order FOREIGN KEY (OrderId) REFERENCES dbo.HospitalRadOrders (ID),
        CONSTRAINT FK_HospitalRadTemplateUses_Template FOREIGN KEY (TemplateId) REFERENCES dbo.HospitalRadTemplates (ID)
    );
    CREATE UNIQUE INDEX IX_HospitalRadTemplateUses_CompanyId_OrderId ON dbo.HospitalRadTemplateUses (CompanyId, OrderId);
END

IF OBJECT_ID('dbo.HospitalRadPeerReviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalRadPeerReviews (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        OrderId int NOT NULL,
        RadiologistEmployeeId int NOT NULL,
        ReviewerEmployeeId int NOT NULL,
        Score int NOT NULL,
        Significant bit NOT NULL,
        Note nvarchar(500) NULL,
        At datetime2 NOT NULL,
        CONSTRAINT PK_HospitalRadPeerReviews PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalRadPeerReviews_Order FOREIGN KEY (OrderId) REFERENCES dbo.HospitalRadOrders (ID),
        CONSTRAINT CK_HospitalRadPeerReviews_Score CHECK (Score BETWEEN 1 AND 3),
        CONSTRAINT CK_HospitalRadPeerReviews_Self CHECK (ReviewerEmployeeId <> RadiologistEmployeeId)
    );
    CREATE UNIQUE INDEX IX_HospitalRadPeerReviews_CompanyId_OrderId_ReviewerEmployeeId ON dbo.HospitalRadPeerReviews (CompanyId, OrderId, ReviewerEmployeeId);
    CREATE INDEX IX_HospitalRadPeerReviews_CompanyId_RadiologistEmployeeId_At ON dbo.HospitalRadPeerReviews (CompanyId, RadiologistEmployeeId, At);
END

COMMIT TRANSACTION;
