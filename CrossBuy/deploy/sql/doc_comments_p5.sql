-- Communication Hub P5 — document comments (DocComments). Idempotent, additive. Safe to re-run.
--
-- Ownership note: the working-tree script announcements_p5.sql creates Announcements,
-- AnnouncementReads AND DocComments in one file. Announcements landed on its own, so committing
-- that mixed script here would drag a second workstream's schema into a Comments commit. This
-- slice carries the DocComments half only. No committed script creates DocComments, so there is
-- no duplicate DDL anywhere in the repository.
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
    -- Matches the only hot read path: ListAsync filters CompanyID + EntityType + EntityId.
    CREATE INDEX IX_DocComment_Entity ON DocComments (CompanyID, EntityType, EntityId);
    PRINT 'ADDED  dbo.DocComments (+ IX_DocComment_Entity)';
END
ELSE
    PRINT 'EXISTS dbo.DocComments';
GO
