-- Communication Hub — email (Metronic inbox). Idempotent, additive. Safe to re-run.
IF OBJECT_ID('CommMessages','U') IS NULL
BEGIN
    CREATE TABLE CommMessages (
        Id          INT IDENTITY(1,1) PRIMARY KEY,
        CompanyID   INT           NOT NULL,
        ToAddress   NVARCHAR(260) NOT NULL,
        Cc          NVARCHAR(400) NULL,
        Subject     NVARCHAR(300) NOT NULL,
        Body        NVARCHAR(MAX) NULL,
        Status      NVARCHAR(20)  NOT NULL DEFAULT 'Queued',   -- Queued | Sent | Failed
        Error       NVARCHAR(1000) NULL,
        SentAt      DATETIME2     NULL,
        Attempts    INT           NOT NULL DEFAULT 0,
        ParentId    INT           NULL,
        Kind        NVARCHAR(20)  NOT NULL DEFAULT 'New',
        Starred     BIT           NOT NULL DEFAULT 0,
        DeletedAt   DATETIME2     NULL,
        CreatedBy   INT NULL, CreatedAt DATETIME2 NULL, updatedBy INT NULL, UpdatedAt DATETIME2 NULL
    );
    CREATE INDEX IX_CommMsg_Company_Status ON CommMessages (CompanyID, Status);
END
GO
IF OBJECT_ID('CommAttachments','U') IS NULL
BEGIN
    CREATE TABLE CommAttachments (
        Id            INT IDENTITY(1,1) PRIMARY KEY,
        CommMessageId INT           NOT NULL,
        FilePath      NVARCHAR(400) NOT NULL,
        FileName      NVARCHAR(260) NOT NULL,
        Size          BIGINT        NOT NULL DEFAULT 0,
        CreatedAt     DATETIME2     NULL
    );
    CREATE INDEX IX_CommAtt_Msg ON CommAttachments (CommMessageId);
END
GO
