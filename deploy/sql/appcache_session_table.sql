-- =============================================================================
-- CrossBuy — SQL session cache table (dbo.AppCache)
-- The app stores sessions in SQL (Program.cs: AddDistributedSqlServerCache → dbo.AppCache).
-- On a successful LOGIN it writes the session (Session.SetString) into this table.
-- If the table is MISSING, GET /Account/Login works but POST returns 500 on session write.
-- Run this on any database that lacks it. Idempotent — safe to run repeatedly.
-- =============================================================================
IF OBJECT_ID('dbo.AppCache','U') IS NULL
BEGIN
    CREATE TABLE dbo.AppCache
    (
        Id                          NVARCHAR(449)   NOT NULL,
        Value                       VARBINARY(MAX)  NOT NULL,
        ExpiresAtTime               DATETIMEOFFSET  NOT NULL,
        SlidingExpirationInSeconds  BIGINT          NULL,
        AbsoluteExpiration          DATETIMEOFFSET  NULL,
        CONSTRAINT pk_AppCache_Id PRIMARY KEY CLUSTERED (Id)
    );
    CREATE NONCLUSTERED INDEX Index_AppCache_ExpiresAtTime ON dbo.AppCache (ExpiresAtTime);
    PRINT 'CREATED dbo.AppCache (SQL session cache)';
END
ELSE PRINT 'dbo.AppCache already EXISTS';
GO

SELECT CASE WHEN OBJECT_ID('dbo.AppCache','U') IS NULL THEN 'MISSING' ELSE 'OK' END AS AppCacheStatus;
GO
