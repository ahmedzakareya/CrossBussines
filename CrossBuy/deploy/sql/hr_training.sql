/* ============================================================================================================
   HR-10 — training courses and employee enrollments.

   CANONICAL AUTHORED SLICE (D-38), migrated from deploy/sql/hr10_training.sql.

   WHY IT MOVED. CrossBuy/deploy/sql is the authored root apply-sql-slices.ps1 treats as canonical; the
   repository-level deploy/sql is the deployment PACKAGE root. The HR scripts were authored only in the package
   root, so they were outside the canonical mechanism — deployable by hand, invisible to the authored-slice
   governance. This file is the canonical version. The package copy stays where it is: removing it is a
   packaging decision for the Integration Owner, not something an HR batch should do silently.

   IDEMPOTENT AND ADDITIVE, unchanged from the original: every object is created only when absent, and the
   ALTER guards below add a missing column only if it is not already there.

   DRIFT CORRECTED. The package script was missing ProviderEn, which the live schema and the committed application
   both have — it is the English twin of Provider, and the course editor binds it. So a fresh deployment created a table the code
   could not use. The column is added by a guarded ALTER rather than by editing the CREATE, so a database that
   already has it is untouched and one built from the old script is repaired in place.
   ============================================================================================================ */

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

-- ------------------------------------------------------------------------------------------------------------
-- D-38 DRIFT REPAIR. Guarded so this is safe on a database that already has the column — which CrossBuyDev
-- does, so this batch's own verification environment no-ops here.
-- ------------------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.TrainingCourses','ProviderEn') IS NULL
    ALTER TABLE dbo.TrainingCourses ADD ProviderEn nvarchar(max) NULL;
GO
