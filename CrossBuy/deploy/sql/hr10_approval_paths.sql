-- HR (2026-11-09): approval paths you design, not code. One row per company and request kind (optionally per leave type)
-- that SHAPES the manager chain the org tree produces: how many manager levels, direct manager only for short leave, and
-- an HR final approver. The safeguards stay in code (delegation, leavers, no self-approval, no double approval). Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'dbo.HrApprovalPaths', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HrApprovalPaths (
        ID                       int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HrApprovalPaths PRIMARY KEY,
        CompanyID                int          NOT NULL,
        Kind                     nvarchar(30) NOT NULL,     -- Leave | Letter | Permission | Advance | ProfileChange
        LeaveTypeID              int          NULL,         -- Leave only: one leave type; NULL = every leave type
        ManagerLevels            int          NOT NULL CONSTRAINT DF_HrApprovalPaths_Levels DEFAULT 0,   -- 0 = up to the unit head
        DirectOnlyUpToDays       int          NOT NULL CONSTRAINT DF_HrApprovalPaths_Direct DEFAULT 0,   -- Leave: <= N days -> direct manager only; 0 = off
        FinalApproverEmployeeID  int          NULL CONSTRAINT FK_HrApprovalPaths_Final REFERENCES dbo.Employee(ID),
        IsActive                 bit          NOT NULL CONSTRAINT DF_HrApprovalPaths_Active DEFAULT 1,
        UpdatedAt                datetime2    NOT NULL CONSTRAINT DF_HrApprovalPaths_At DEFAULT SYSUTCDATETIME(),
        UpdatedByEmployeeID      int          NULL,
        CONSTRAINT CK_HrApprovalPaths_Levels CHECK (ManagerLevels BETWEEN 0 AND 20),
        CONSTRAINT CK_HrApprovalPaths_Direct CHECK (DirectOnlyUpToDays BETWEEN 0 AND 365)
    );
    CREATE UNIQUE INDEX UX_HrApprovalPaths_Scope ON dbo.HrApprovalPaths (CompanyID, Kind, LeaveTypeID);
END
-- SLA (2026-11-09): a step waiting longer than SlaDays is escalated once to its approver's manager (HrApprovalEscalation).
IF COL_LENGTH(N'dbo.HrApprovalPaths', N'SlaDays') IS NULL
    ALTER TABLE dbo.HrApprovalPaths ADD SlaDays int NOT NULL CONSTRAINT DF_HrApprovalPaths_Sla DEFAULT 0
        CONSTRAINT CK_HrApprovalPaths_Sla CHECK (SlaDays BETWEEN 0 AND 90);
