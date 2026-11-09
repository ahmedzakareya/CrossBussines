-- HR9 (2026-11-09): approval delegation — an approver away for a period hands their approvals to a colleague.
-- Applied when a leave or employee-request approval chain is built; requests already waiting are not moved. Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
IF OBJECT_ID(N'dbo.ApprovalDelegations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ApprovalDelegations (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ApprovalDelegations PRIMARY KEY,
        CompanyID       int           NOT NULL,
        FromEmployeeID  int           NOT NULL CONSTRAINT FK_ApprovalDelegations_From REFERENCES dbo.Employee(ID),
        ToEmployeeID    int           NOT NULL CONSTRAINT FK_ApprovalDelegations_To REFERENCES dbo.Employee(ID),
        StartDate       date          NOT NULL,
        EndDate         date          NOT NULL,
        Reason          nvarchar(400) NULL,
        IsActive        bit           NOT NULL CONSTRAINT DF_ApprovalDelegations_Active DEFAULT 1,
        CreatedAt       datetime2     NOT NULL CONSTRAINT DF_ApprovalDelegations_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT CK_ApprovalDelegations_Range CHECK (EndDate >= StartDate),
        CONSTRAINT CK_ApprovalDelegations_NotSelf CHECK (FromEmployeeID <> ToEmployeeID)
    );
    CREATE INDEX IX_ApprovalDelegations_From ON dbo.ApprovalDelegations (CompanyID, FromEmployeeID, IsActive, StartDate, EndDate);
END
