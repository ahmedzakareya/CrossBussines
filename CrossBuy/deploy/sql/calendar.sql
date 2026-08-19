-- Calendar (company calendar events + attendees). Idempotent.
IF OBJECT_ID(N'dbo.CalendarEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEvents (
        Id           INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID    INT           NOT NULL,
        Title        NVARCHAR(300) NOT NULL DEFAULT(N''),
        Description  NVARCHAR(MAX) NULL,
        Location     NVARCHAR(300) NULL,
        AllDay       BIT           NOT NULL DEFAULT(0),
        StartAt      DATETIME2     NOT NULL,
        EndAt        DATETIME2     NULL,
        Scope        NVARCHAR(20)  NOT NULL DEFAULT(N'Personal'),  -- Personal | Company
        OwnerEmpId   INT           NOT NULL,
        DeletedAt    DATETIME2     NULL,
        CreatedBy    INT           NULL,
        CreatedAt    DATETIME2     NULL,
        updatedBy    INT           NULL,
        UpdatedAt    DATETIME2     NULL
    );
    CREATE INDEX IX_CalendarEvents_Company ON dbo.CalendarEvents (CompanyID, StartAt);
    CREATE INDEX IX_CalendarEvents_Owner   ON dbo.CalendarEvents (OwnerEmpId);
END

IF OBJECT_ID(N'dbo.CalendarEventAttendees', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEventAttendees (
        Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EventId     INT NOT NULL,
        EmployeeId  INT NOT NULL
    );
    CREATE INDEX IX_CalendarEventAttendees_Event ON dbo.CalendarEventAttendees (EventId);
    CREATE INDEX IX_CalendarEventAttendees_Emp   ON dbo.CalendarEventAttendees (EmployeeId);
END
