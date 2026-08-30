/* ============================================================================================================
   HR-8 — employee service requests (letters and hourly permissions) and their approval steps.

   CANONICAL AUTHORED SLICE (D-38), migrated from deploy/sql/hr8_employee_requests.sql.

   WHY IT MOVED. CrossBuy/deploy/sql is the authored root apply-sql-slices.ps1 treats as canonical; the
   repository-level deploy/sql is the deployment PACKAGE root. The HR scripts were authored only in the package
   root, so they were outside the canonical mechanism — deployable by hand, invisible to the authored-slice
   governance. This file is the canonical version. The package copy stays where it is: removing it is a
   packaging decision for the Integration Owner, not something an HR batch should do silently.

   IDEMPOTENT AND ADDITIVE, unchanged from the original: every object is created only when absent, and the
   ALTER guards below add a missing column only if it is not already there.

   DRIFT CORRECTED. The package script was missing FromTime and ToTime, which the live schema and the committed application
   both have — PeopleController.CreateRequest parses both from the form and stores them on the request. So a fresh deployment created a table the code
   could not use. The column is added by a guarded ALTER rather than by editing the CREATE, so a database that
   already has it is untouched and one built from the old script is repaired in place.
   ============================================================================================================ */

IF OBJECT_ID('dbo.EmployeeRequests','U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeRequests (
        ID                        int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        CompanyID                 int NOT NULL,
        EmployeeID                int NOT NULL,
        RequestType               nvarchar(20) NOT NULL CONSTRAINT DF_EmpReq_Type DEFAULT('Letter'),  -- Letter | Permission
        LetterType                nvarchar(20) NULL,     -- Salary | Employment | Experience | ToWhom
        Addressee                 nvarchar(200) NULL,
        PermissionDate            datetime2(7) NULL,
        FromTime                  time(0) NULL,
        ToTime                    time(0) NULL,
        Reason                    nvarchar(1000) NULL,
        Status                    int NOT NULL CONSTRAINT DF_EmpReq_Status DEFAULT(0),   -- 0 pending / 1 approved / 2 rejected
        CurrentLevel              int NOT NULL CONSTRAINT DF_EmpReq_Level DEFAULT(0),
        CurrentApproverEmployeeID int NULL,
        ApproverEmployeeID        int NULL,
        DecisionAt                datetime2(7) NULL,
        DecisionNote              nvarchar(1000) NULL,
        CreatedAt                 datetime2(7) NULL,
        CreatedBy                 int NULL,
        UpdatedAt                 datetime2(7) NULL,
        updatedBy                 int NULL
    );
    CREATE INDEX IX_EmpReq_Co_Emp ON dbo.EmployeeRequests(CompanyID, EmployeeID);
    CREATE INDEX IX_EmpReq_CurAppr ON dbo.EmployeeRequests(CurrentApproverEmployeeID, Status);
    CREATE INDEX IX_EmpReq_Perm ON dbo.EmployeeRequests(EmployeeID, RequestType, Status, PermissionDate);
END
GO

IF OBJECT_ID('dbo.EmployeeRequestSteps','U') IS NULL
BEGIN
    CREATE TABLE dbo.EmployeeRequestSteps (
        ID                 int IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeRequestID  int NOT NULL,
        Level              int NOT NULL,
        ApproverEmployeeID int NOT NULL,
        Status             int NOT NULL CONSTRAINT DF_EmpReqStep_Status DEFAULT(0),
        DecisionAt         datetime2(7) NULL,
        DecisionNote       nvarchar(1000) NULL,
        CreatedAt          datetime2(7) NULL,
        CreatedBy          int NULL,
        UpdatedAt          datetime2(7) NULL,
        updatedBy          int NULL
    );
    CREATE INDEX IX_EmpReqStep_Req ON dbo.EmployeeRequestSteps(EmployeeRequestID);
END
GO

-- ------------------------------------------------------------------------------------------------------------
-- D-38 DRIFT REPAIR. Guarded so this is safe on a database that already has the column — which CrossBuyDev
-- does, so this batch's own verification environment no-ops here.
-- ------------------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.EmployeeRequests','FromTime') IS NULL
    ALTER TABLE dbo.EmployeeRequests ADD FromTime time(0) NULL;
GO

-- ------------------------------------------------------------------------------------------------------------
-- D-38 DRIFT REPAIR. Guarded so this is safe on a database that already has the column — which CrossBuyDev
-- does, so this batch's own verification environment no-ops here.
-- ------------------------------------------------------------------------------------------------------------
IF COL_LENGTH('dbo.EmployeeRequests','ToTime') IS NULL
    ALTER TABLE dbo.EmployeeRequests ADD ToTime time(0) NULL;
GO
