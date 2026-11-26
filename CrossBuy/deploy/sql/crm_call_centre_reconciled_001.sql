-- R1 successor: finish the additive CRM call-centre schema on databases that
-- already have the base tables. The earlier package files are retained as
-- history; this canonical slice is the only deployment input for these deltas.
-- No rows are inserted, updated, or deleted.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID(N'dbo.CrmCalls', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmCalls', N'DepartmentId') IS NULL
    ALTER TABLE dbo.CrmCalls ADD DepartmentId INT NULL;
GO

IF OBJECT_ID(N'dbo.CrmCalls', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmCalls', N'SourceType') IS NULL
    ALTER TABLE dbo.CrmCalls ADD SourceType NVARCHAR(50) NULL;
GO

IF OBJECT_ID(N'dbo.CrmCalls', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmCalls', N'SourceId') IS NULL
    ALTER TABLE dbo.CrmCalls ADD SourceId INT NULL;
GO

IF OBJECT_ID(N'dbo.CrmTickets', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmTickets', N'Channel') IS NULL
    ALTER TABLE dbo.CrmTickets ADD Channel NVARCHAR(20) NULL;
GO

IF OBJECT_ID(N'dbo.CrmTickets', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmTickets', N'CallerPhone') IS NULL
    ALTER TABLE dbo.CrmTickets ADD CallerPhone NVARCHAR(40) NULL;
GO

IF OBJECT_ID(N'dbo.CrmTickets', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmTickets', N'DepartmentId') IS NULL
    ALTER TABLE dbo.CrmTickets ADD DepartmentId INT NULL;
GO

IF OBJECT_ID(N'dbo.CrmTickets', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmTickets', N'SourceType') IS NULL
    ALTER TABLE dbo.CrmTickets ADD SourceType NVARCHAR(50) NULL;
GO

IF OBJECT_ID(N'dbo.CrmTickets', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.CrmTickets', N'SourceId') IS NULL
    ALTER TABLE dbo.CrmTickets ADD SourceId INT NULL;
GO
