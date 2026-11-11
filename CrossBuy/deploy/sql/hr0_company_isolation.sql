-- ==========================================================================================
-- HR0 (2026-11-11): the HR set-up tables get their company — the last open item of HR0.
--
-- Before this, seven tables had no company column, so every company read and wrote the same rows: the
-- organisation tree (Hierarchicals), job titles, leave types, and the policy group with its leave / attendance /
-- salary policies. A company editing "annual leave" changed it for every other company.
--
-- This slice only ADDS a nullable CompanyID and fills it from evidence already in the data; the global company
-- filter and the write guard (CompanyQueryFilters) then isolate reads and writes in the application.
--
-- How each row's company is decided (measured 2026-11-11 on CrossBuyDB2):
--   * Hierarchicals — the nearest ancestor-or-self of type 1 (a company node) names its company in H_ObjectID.
--     51 of 62 nodes resolve to an existing company. 11 do not and are left with NO company, so no company sees
--     them: node 27 points at company 66, which no longer exists, and nodes 87–96 are two test trees under a
--     company 990771 that never existed.
--   * Policies (the policy group) — the company of the employees assigned to it. Policy 8 has 10 employees, all in
--     company 1. Policies 9, 10 and 13 have no one assigned; like every other HR record in this database they were
--     made in company 1, so they go to company 1.
--   * Leave / attendance / salary policies — the company of their policy group.
--   * JobTitles and LeaveTypes — every employee using a job title (21) and every leave request using a leave type
--     (20) is in company 1, so the existing rows are company 1's. Other companies create their own.
--
-- Idempotent: each column is added only if missing, and each fill touches only rows still without a company.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;

IF COL_LENGTH('dbo.Hierarchicals', 'CompanyID') IS NULL       ALTER TABLE dbo.Hierarchicals ADD CompanyID int NULL;
IF COL_LENGTH('dbo.JobTitles', 'CompanyID') IS NULL           ALTER TABLE dbo.JobTitles ADD CompanyID int NULL;
IF COL_LENGTH('dbo.LeaveTypes', 'CompanyID') IS NULL          ALTER TABLE dbo.LeaveTypes ADD CompanyID int NULL;
IF COL_LENGTH('dbo.Policies', 'CompanyID') IS NULL            ALTER TABLE dbo.Policies ADD CompanyID int NULL;
IF COL_LENGTH('dbo.LeavePolicies', 'CompanyID') IS NULL       ALTER TABLE dbo.LeavePolicies ADD CompanyID int NULL;
IF COL_LENGTH('dbo.AttendancePolicies', 'CompanyID') IS NULL  ALTER TABLE dbo.AttendancePolicies ADD CompanyID int NULL;
IF COL_LENGTH('dbo.SalaryPolicies', 'CompanyID') IS NULL      ALTER TABLE dbo.SalaryPolicies ADD CompanyID int NULL;
GO

BEGIN TRANSACTION;

-- the tree: nearest company node above (or at) each node
;WITH t AS (
    SELECT H_ID, CAST(CASE WHEN H_Type = 1 THEN H_ObjectID END AS int) AS co
      FROM dbo.Hierarchicals WHERE H_Parent IS NULL OR H_Parent = 0
    UNION ALL
    SELECT h.H_ID, CASE WHEN h.H_Type = 1 THEN h.H_ObjectID ELSE t.co END
      FROM dbo.Hierarchicals h JOIN t ON h.H_Parent = t.H_ID
)
UPDATE h SET CompanyID = t.co
  FROM dbo.Hierarchicals h JOIN t ON t.H_ID = h.H_ID
 WHERE h.CompanyID IS NULL
   AND t.co IN (SELECT CompanyID FROM dbo.Companies);

-- the policy group: the one company its assigned employees belong to
UPDATE p SET CompanyID = x.co
  FROM dbo.Policies p
  JOIN (SELECT a.LeavePolicyTypeID AS pid, MIN(e.EmpCompanyID) AS co
          FROM dbo.PolicyAssignments a JOIN dbo.Employee e ON e.ID = a.EmployeeID
         GROUP BY a.LeavePolicyTypeID
        HAVING COUNT(DISTINCT e.EmpCompanyID) = 1) x ON x.pid = p.ID
 WHERE p.CompanyID IS NULL;

-- groups nobody is assigned to: company 1, where all HR records of this database were made
UPDATE dbo.Policies SET CompanyID = 1
 WHERE CompanyID IS NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.PolicyAssignments a WHERE a.LeavePolicyTypeID = Policies.ID)
   AND EXISTS (SELECT 1 FROM dbo.Companies WHERE CompanyID = 1);

-- the policies inside a group follow the group
UPDATE c SET CompanyID = p.CompanyID FROM dbo.LeavePolicies c JOIN dbo.Policies p ON p.ID = c.LeavePolicyTypeID WHERE c.CompanyID IS NULL AND p.CompanyID IS NOT NULL;
UPDATE c SET CompanyID = p.CompanyID FROM dbo.AttendancePolicies c JOIN dbo.Policies p ON p.ID = c.LeavePolicyTypeID WHERE c.CompanyID IS NULL AND p.CompanyID IS NOT NULL;
UPDATE c SET CompanyID = p.CompanyID FROM dbo.SalaryPolicies c JOIN dbo.Policies p ON p.ID = c.LeavePolicyTypeID WHERE c.CompanyID IS NULL AND p.CompanyID IS NOT NULL;

-- the shared catalogues: their only users are company 1's
UPDATE dbo.JobTitles  SET CompanyID = 1 WHERE CompanyID IS NULL AND EXISTS (SELECT 1 FROM dbo.Companies WHERE CompanyID = 1);
UPDATE dbo.LeaveTypes SET CompanyID = 1 WHERE CompanyID IS NULL AND EXISTS (SELECT 1 FROM dbo.Companies WHERE CompanyID = 1);

COMMIT TRANSACTION;
GO

-- every read filters on it
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Hierarchicals_CompanyID' AND object_id = OBJECT_ID('dbo.Hierarchicals'))
    CREATE INDEX IX_Hierarchicals_CompanyID ON dbo.Hierarchicals (CompanyID);
GO
