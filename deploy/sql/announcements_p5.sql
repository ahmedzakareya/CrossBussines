-- Communication Hub P5: company/branch announcements + document comments. Idempotent, additive. Safe to re-run.
IF OBJECT_ID('Announcements','U') IS NULL
BEGIN
    CREATE TABLE Announcements (
        Id         INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID  INT           NOT NULL,
        BranchID   INT           NULL,
        Title      NVARCHAR(200) NOT NULL,
        Body       NVARCHAR(MAX) NOT NULL,
        Priority   NVARCHAR(20)  NOT NULL DEFAULT 'Normal',
        Scope      NVARCHAR(20)  NOT NULL DEFAULT 'Company',
        StartsAt   DATETIME2     NULL,
        ExpiresAt  DATETIME2     NULL,
        IsActive   BIT           NOT NULL DEFAULT 1,
        CreatedBy  INT NULL, CreatedAt DATETIME2 NULL, updatedBy INT NULL, UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_Ann_Company ON Announcements (CompanyID, IsActive);
END
GO
IF OBJECT_ID('AnnouncementReads','U') IS NULL
BEGIN
    CREATE TABLE AnnouncementReads (
        Id             INT IDENTITY(1,1) PRIMARY KEY,
        AnnouncementId INT       NOT NULL,
        EmployeeId     INT       NOT NULL,
        ReadAt         DATETIME2 NOT NULL
    );
    CREATE UNIQUE INDEX UX_AnnRead ON AnnouncementReads (AnnouncementId, EmployeeId);
END
GO
IF OBJECT_ID('DocComments','U') IS NULL
BEGIN
    CREATE TABLE DocComments (
        Id         INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID  INT           NOT NULL,
        EntityType NVARCHAR(60)  NOT NULL,
        EntityId   INT           NOT NULL,
        Body       NVARCHAR(MAX) NOT NULL,
        DeletedAt  DATETIME2     NULL,
        CreatedBy  INT NULL, CreatedAt DATETIME2 NULL, updatedBy INT NULL, UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_DocComment_Entity ON DocComments (CompanyID, EntityType, EntityId);
END
GO
