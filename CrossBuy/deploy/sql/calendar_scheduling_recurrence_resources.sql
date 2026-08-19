-- ==========================================================================================
-- Calendar scheduling — recurrence, time zone, resources.
--
-- THREE NEW TABLES. No ALTER of CalendarEvents. An event with no CalendarEventSchedules row
-- behaves exactly as it does today: one occurrence, stored in UTC. So an environment that has
-- not run this script keeps a working calendar; only recurrence is absent.
--
-- Idempotent: safe to run repeatedly. NOT EXECUTED against any database by the authoring session.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- ---- recurrence + time zone ------------------------------------------------------------
IF OBJECT_ID('dbo.CalendarEventSchedules','U') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEventSchedules (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CalendarEventSchedules PRIMARY KEY,
        CompanyId           int           NOT NULL,
        EventId             int           NOT NULL,

        -- An IANA/Windows zone id, NOT an offset. An offset is only true until the next DST change:
        -- a 09:00 weekly meeting stored as "UTC+3" silently becomes 08:00 or 10:00 local after the
        -- clocks move. Storing the ZONE keeps "09:00 local, every week" true.
        TimeZoneId          nvarchar(100) NULL,

        -- None | Daily | Weekly | Monthly | Yearly
        RecurrenceKind      nvarchar(20)  NOT NULL CONSTRAINT DF_CalendarEventSchedules_Kind DEFAULT('None'),
        Interval            int           NOT NULL CONSTRAINT DF_CalendarEventSchedules_Interval DEFAULT(1),
        ByWeekdays          nvarchar(100) NULL,   -- "Monday,Wednesday"; empty = the starting weekday

        -- A repeating series ends by DATE or by COUNT. Without an end there is no last occurrence,
        -- so every expansion would be truncated by a limit the user never chose.
        UntilLocalDate      date          NULL,
        OccurrenceCount     int           NULL,

        -- Local dates (yyyy-MM-dd, comma separated) removed from the series: a cancelled single
        -- occurrence. Deleting the row would delete the whole series.
        ExceptionDates      nvarchar(max) NULL,

        CreatedAt           datetime2     NOT NULL CONSTRAINT DF_CalendarEventSchedules_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int           NULL,
        UpdatedAt           datetime2     NULL,
        UpdatedByEmployeeId int           NULL,

        CONSTRAINT CK_CalendarEventSchedules_Interval CHECK (Interval >= 1)
    );

    -- One schedule per event. Two would be two answers to "when does this repeat".
    CREATE UNIQUE INDEX UX_CalendarEventSchedules_Event
        ON dbo.CalendarEventSchedules (CompanyId, EventId);
END

-- ---- bookable resources ----------------------------------------------------------------
IF OBJECT_ID('dbo.CalendarResources','U') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarResources (
        ID                  int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CalendarResources PRIMARY KEY,
        CompanyId           int           NOT NULL,
        Name                nvarchar(200) NOT NULL,
        NameEn              nvarchar(200) NULL,
        Kind                nvarchar(30)  NOT NULL CONSTRAINT DF_CalendarResources_Kind DEFAULT('Room'),
        Capacity            int           NULL,
        IsActive            bit           NOT NULL CONSTRAINT DF_CalendarResources_Active DEFAULT(1),
        CreatedAt           datetime2     NOT NULL CONSTRAINT DF_CalendarResources_Created DEFAULT(SYSUTCDATETIME()),
        CreatedByEmployeeId int           NULL
    );
    CREATE UNIQUE INDEX UX_CalendarResources_Name ON dbo.CalendarResources (CompanyId, Name);
END

IF OBJECT_ID('dbo.CalendarEventResources','U') IS NULL
BEGIN
    CREATE TABLE dbo.CalendarEventResources (
        ID          int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CalendarEventResources PRIMARY KEY,
        CompanyId   int NOT NULL,
        EventId     int NOT NULL,
        ResourceId  int NOT NULL
    );
    -- A resource booked twice by the same event is not two bookings.
    CREATE UNIQUE INDEX UX_CalendarEventResources_Pair
        ON dbo.CalendarEventResources (CompanyId, EventId, ResourceId);
    -- Conflict detection asks "what else is on this resource" for every resource at once.
    CREATE INDEX IX_CalendarEventResources_Resource
        ON dbo.CalendarEventResources (CompanyId, ResourceId);
END
GO
