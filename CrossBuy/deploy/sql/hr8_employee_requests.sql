-- ============================================================================
-- HR-8 — ESS self-service requests (letters + hourly permissions)
-- Idempotent. Approval chain mirrors LeaveRequest/LeaveApprovalStep. No GL impact.
-- ============================================================================

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
