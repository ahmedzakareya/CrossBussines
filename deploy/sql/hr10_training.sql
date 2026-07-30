-- ============================================================================
-- HR-10 — Training (courses + enrollments). HR record only; no GL impact.
-- Idempotent.
-- ============================================================================

IF OBJECT_ID('dbo.TrainingCourses','U') IS NULL
CREATE TABLE dbo.TrainingCourses (
    ID        int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID int NOT NULL,
    Code      nvarchar(50) NOT NULL,
    Title     nvarchar(300) NOT NULL,
    TitleEn   nvarchar(300) NULL,
    Provider  nvarchar(200) NULL,
    Category  nvarchar(100) NULL,
    Cost      decimal(19,4) NOT NULL CONSTRAINT DF_TrnCourse_Cost DEFAULT(0),
    Hours     decimal(19,4) NOT NULL CONSTRAINT DF_TrnCourse_Hours DEFAULT(0),
    StartDate datetime2(7) NULL,
    EndDate   datetime2(7) NULL,
    IsActive  bit NOT NULL CONSTRAINT DF_TrnCourse_Active DEFAULT(1),
    Notes     nvarchar(1000) NULL,
    CreatedAt datetime2(7) NULL
);
GO
IF OBJECT_ID('dbo.TrainingEnrollments','U') IS NULL
CREATE TABLE dbo.TrainingEnrollments (
    ID          int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID   int NOT NULL,
    CourseId    int NOT NULL,
    EmployeeID  int NOT NULL,
    Status      nvarchar(15) NOT NULL CONSTRAINT DF_TrnEnr_Status DEFAULT('Planned'),  -- Planned | Attended | Completed | Cancelled
    Score       decimal(19,4) NULL,
    Certificate nvarchar(200) NULL,
    CompletedAt datetime2(7) NULL,
    Notes       nvarchar(1000) NULL,
    CreatedAt   datetime2(7) NULL
);
CREATE INDEX IX_TrnEnr_Course ON dbo.TrainingEnrollments(CourseId);
CREATE INDEX IX_TrnEnr_Emp ON dbo.TrainingEnrollments(EmployeeID);
GO
