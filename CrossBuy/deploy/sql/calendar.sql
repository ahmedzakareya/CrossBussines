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

-- ---- ADDITIVE: CalendarEvents.TitleEn -----------------------------------------------------
--
-- OUTSIDE the CREATE TABLE guard on purpose: that block only runs for a database that does not
-- have the table yet, so a column added inside it never reaches an existing one.
--
-- An event title had no English twin at all, so the unified agenda showed Arabic event titles in
-- an English UI beside task titles that resolve correctly. Nullable, no default, no backfill.
IF COL_LENGTH('dbo.CalendarEvents', 'TitleEn') IS NULL
    ALTER TABLE dbo.CalendarEvents ADD TitleEn nvarchar(300) NULL;
GO
-- ---- ADDITIVE: the other two free-text fields on an event -----------------------------------
-- TitleEn landed on its own, which fixed the reminder popup's heading and left the two lines under
-- it Arabic: the location and the description are typed by hand and had one column each. Same shape,
-- same NULL-able fallback - no English text means the Arabic is shown rather than a blank line.
IF COL_LENGTH('dbo.CalendarEvents', 'DescriptionEn') IS NULL
    ALTER TABLE dbo.CalendarEvents ADD DescriptionEn nvarchar(max) NULL;
GO
IF COL_LENGTH('dbo.CalendarEvents', 'LocationEn') IS NULL
    ALTER TABLE dbo.CalendarEvents ADD LocationEn nvarchar(300) NULL;
GO
