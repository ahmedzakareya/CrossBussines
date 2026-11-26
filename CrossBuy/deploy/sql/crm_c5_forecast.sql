-- CRM C5 — quotas and forecasting. Additive only: three new tables, nothing existing is altered.
--   CrmQuotas              one target per person per month; a quarter is the sum of its three months
--   CrmDealForecasts       the forecast category a person put a deal in (absent = derived from its probability)
--   CrmForecastSubmissions every submission is a new row — the latest one per person+period is current, the rest is
--                          the week-by-week history; a manager approves or returns it, and may adjust the commit
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmQuotas') IS NULL
CREATE TABLE dbo.CrmQuotas (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmQuotas PRIMARY KEY,
    CompanyId int NOT NULL,
    EmployeeId int NOT NULL,
    Period char(7) NOT NULL,                      -- yyyy-MM
    Amount decimal(18,2) NOT NULL CONSTRAINT CK_CrmQuotas_Amount CHECK (Amount >= 0),
    UpdatedByEmployeeId int NULL,
    UpdatedAt datetime2 NOT NULL CONSTRAINT DF_CrmQuotas_UpdatedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_CrmQuotas_Period CHECK (Period LIKE '[12][0-9][0-9][0-9]-[01][0-9]'),
    CONSTRAINT UQ_CrmQuotas UNIQUE (CompanyId, EmployeeId, Period)
);

IF OBJECT_ID('dbo.CrmDealForecasts') IS NULL
CREATE TABLE dbo.CrmDealForecasts (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmDealForecasts PRIMARY KEY,
    CompanyId int NOT NULL,
    OpportunityId int NOT NULL,
    Category varchar(12) NOT NULL CONSTRAINT CK_CrmDealForecasts_Category CHECK (Category IN ('Pipeline', 'BestCase', 'Commit', 'Omitted')),
    SetByEmployeeId int NULL,
    SetAt datetime2 NOT NULL CONSTRAINT DF_CrmDealForecasts_SetAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_CrmDealForecasts UNIQUE (CompanyId, OpportunityId)
);

IF OBJECT_ID('dbo.CrmForecastSubmissions') IS NULL
CREATE TABLE dbo.CrmForecastSubmissions (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmForecastSubmissions PRIMARY KEY,
    CompanyId int NOT NULL,
    EmployeeId int NOT NULL,
    Period varchar(7) NOT NULL,                   -- yyyy-MM or yyyy-Qn
    Quota decimal(18,2) NOT NULL,
    Won decimal(18,2) NOT NULL,
    CommitAmount decimal(18,2) NOT NULL,
    BestCase decimal(18,2) NOT NULL,
    Pipeline decimal(18,2) NOT NULL,
    Note nvarchar(500) NULL,
    SubmittedAt datetime2 NOT NULL CONSTRAINT DF_CrmForecastSubmissions_At DEFAULT SYSUTCDATETIME(),
    Status varchar(10) NOT NULL CONSTRAINT DF_CrmForecastSubmissions_Status DEFAULT 'Submitted'
        CONSTRAINT CK_CrmForecastSubmissions_Status CHECK (Status IN ('Submitted', 'Approved', 'Returned', 'Superseded')),
    ReviewedByEmployeeId int NULL,
    ReviewedAt datetime2 NULL,
    ReviewNote nvarchar(500) NULL,
    AdjustedCommit decimal(18,2) NULL,
    CONSTRAINT CK_CrmForecastSubmissions_Period CHECK (Period LIKE '[12][0-9][0-9][0-9]-[01][0-9]' OR Period LIKE '[12][0-9][0-9][0-9]-Q[1-4]'),
    -- nobody signs off their own forecast
    CONSTRAINT CK_CrmForecastSubmissions_Reviewer CHECK (ReviewedByEmployeeId IS NULL OR ReviewedByEmployeeId <> EmployeeId),
    -- a decision carries who and when; a pending one carries neither
    CONSTRAINT CK_CrmForecastSubmissions_Decision CHECK (
        (Status IN ('Approved', 'Returned') AND ReviewedByEmployeeId IS NOT NULL AND ReviewedAt IS NOT NULL)
        OR (Status IN ('Submitted', 'Superseded') AND ReviewedByEmployeeId IS NULL))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CrmForecastSubmissions_Person')
    CREATE INDEX IX_CrmForecastSubmissions_Person ON dbo.CrmForecastSubmissions (CompanyId, EmployeeId, Period, ID DESC);
-- at most one live (undecided) submission per person and period
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_CrmForecastSubmissions_Open')
    CREATE UNIQUE INDEX UX_CrmForecastSubmissions_Open ON dbo.CrmForecastSubmissions (CompanyId, EmployeeId, Period) WHERE Status = 'Submitted';

COMMIT TRANSACTION;
