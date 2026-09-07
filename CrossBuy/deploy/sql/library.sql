-- Document Library / File Manager (folders + files in one table, SharePoint-style). Idempotent.
IF OBJECT_ID(N'dbo.LibraryItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LibraryItems (
        Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID    INT           NOT NULL,
        ParentId     INT           NULL,                          -- NULL = library root
        IsFolder     BIT           NOT NULL DEFAULT(0),
        Name         NVARCHAR(400) NOT NULL DEFAULT(N''),
        NameEn       NVARCHAR(400) NULL,                          -- English twin (shown when UI language != Arabic)
        StoredPath   NVARCHAR(600) NULL,                          -- file only: /uploads/library/<company>/<guid>.ext
        ContentType  NVARCHAR(200) NULL,                          -- file only
        Size         BIGINT        NOT NULL DEFAULT(0),           -- file only, bytes
        OwnerEmpId   INT           NOT NULL,
        DeletedAt    DATETIME2     NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL,
        updatedBy    INT           NULL,
        UpdatedAt    DATETIME2     NULL
    );
    CREATE INDEX IX_LibraryItems_Company_Parent ON dbo.LibraryItems (CompanyID, ParentId);
    CREATE INDEX IX_LibraryItems_Owner          ON dbo.LibraryItems (OwnerEmpId);
END

-- English twin for the free-text name (added later; idempotent)
IF COL_LENGTH(N'dbo.LibraryItems', N'NameEn') IS NULL
    ALTER TABLE dbo.LibraryItems ADD NameEn NVARCHAR(400) NULL;
