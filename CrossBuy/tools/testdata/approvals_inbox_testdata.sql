-- =================================================================================================
-- Pending approvals for /Approvals/Index, so the inbox can be seen with rows in it.
--
-- WRITTEN AGAINST THE READERS' OWN PREDICATES, not against a guess at what the screen shows. The
-- inbox merges four silo readers; two of them are seedable with plain rows:
--
--   LeaveWorkflowService.PendingForApproverAsync
--       Status = 0  AND  CurrentApproverEmployeeID = @me  AND  Employee.EmpCompanyID = @company
--
--   EmployeeRequestService.PendingForApproverAsync
--       Status = 0  AND  CurrentApproverEmployeeID = @me  AND  CompanyID = @company
--
-- Note the two differ: leave reaches the company through Employee.EmpCompanyID (the table has no
-- CompanyID at all), employee requests carry their own. Seeding either one wrong produces a row
-- that exists and never appears, which looks like a broken screen.
--
-- The other two silos (Inventory, ProjectBilling) are NOT seeded: their readers apply
-- preparer <> approver plus a per-module permission, so a row inserted by hand would be filtered
-- out and prove nothing. They stay empty and the screen should say so.
--
-- ADDITIVE AND REVERSIBLE. INSERT only. Nothing existing is updated or deleted. Every row carries
-- the marker below in a column the screen does not render, and the teardown at the bottom removes
-- exactly these rows.
--
--   sqlcmd -S localhost -d CrossBuyDev -E -C -f 65001 -i tools/testdata/approvals_inbox_testdata.sql
-- =================================================================================================
-- sqlcmd -i runs with QUOTED_IDENTIFIER OFF; these tables carry filtered indexes, so without this
-- line every INSERT is rejected. It must be first in the batch.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @marker  nvarchar(40) = N'ZZ-APPROVALS-SEED';
DECLARE @company int = 1;
DECLARE @me      int = 5;              -- admin: the APPROVER, never the requester
DECLARE @today   datetime2 = CAST(SYSUTCDATETIME() AS date);

IF NOT EXISTS (SELECT 1 FROM Employee WHERE ID = @me AND EmpCompanyID = @company)
BEGIN
    RAISERROR('Approver %d is not in company %d; nothing seeded.', 16, 1, @me, @company);
    RETURN;
END

-- Requesters: real colleagues in the same company, never the approver themselves. An approval
-- whose requester IS the approver is exactly what the other two silos filter out, and seeding one
-- would model a case the product refuses.
DECLARE @people TABLE (rn int IDENTITY(1,1), EmployeeId int);
INSERT INTO @people (EmployeeId)
SELECT TOP 5 ID FROM Employee
WHERE EmpCompanyID = @company AND IsActive = 1 AND ID <> @me
ORDER BY ID;

DECLARE @types TABLE (rn int IDENTITY(1,1), LeaveTypeId int);
INSERT INTO @types (LeaveTypeId) SELECT TOP 4 ID FROM LeaveTypes ORDER BY ID;

IF (SELECT COUNT(*) FROM @people) = 0 OR (SELECT COUNT(*) FROM @types) = 0
BEGIN
    RAISERROR('No colleague or no leave type available; nothing seeded.', 16, 1);
    RETURN;
END

DECLARE @nPeople int = (SELECT COUNT(*) FROM @people);
DECLARE @nTypes  int = (SELECT COUNT(*) FROM @types);

BEGIN TRAN;

-- =================================================================================================
-- LEAVE. Oldest inserted first, so the row ids ascend with time - the inbox orders by ID DESC, and
-- a seed that backdates rows in the other direction puts the oldest request at the top.
-- =================================================================================================
DECLARE @leaves TABLE (rn int IDENTITY(1,1), DaysAgo int, StartsInDays int, Len int, Reason nvarchar(200));
INSERT INTO @leaves (DaysAgo, StartsInDays, Len, Reason) VALUES
 (9, 21, 5, N'إجازة سنوية مخططة - تم تسليم المهام للزميل'),
 (6, 14, 2, N'ظرف عائلي'),
 (3,  9, 7, N'إجازة سنوية - سفر'),
 (1,  4, 1, N'مراجعة طبية'),
 (0,  2, 3, N'إجازة مرضية بتقرير');

DECLARE @i int = (SELECT COUNT(*) FROM @leaves);
DECLARE @madeLeave int = 0;

WHILE @i >= 1
BEGIN
    DECLARE @ago int, @in int, @len int, @why nvarchar(200);
    SELECT @ago = DaysAgo, @in = StartsInDays, @len = Len, @why = Reason FROM @leaves WHERE rn = @i;

    DECLARE @emp int = (SELECT EmployeeId FROM @people WHERE rn = ((@i - 1) % @nPeople) + 1);
    DECLARE @lt  int = (SELECT LeaveTypeId FROM @types WHERE rn = ((@i - 1) % @nTypes) + 1);
    DECLARE @start datetime2 = DATEADD(DAY, @in, @today);

    INSERT INTO LeaveRequests
        (EmployeeID, LeaveTypeID, StartDate, EndDate, Days, Reason, Status,
         CurrentLevel, CurrentApproverEmployeeID, CreatedBy, CreatedAt)
    VALUES
        (@emp, @lt, @start, DATEADD(DAY, @len - 1, @start), @len, @why + N' [' + @marker + N']', 0,
         1, @me, @emp, DATEADD(DAY, -@ago, SYSUTCDATETIME()));

    SET @madeLeave = @madeLeave + 1;
    SET @i = @i - 1;
END

-- =================================================================================================
-- EMPLOYEE REQUESTS. Two kinds, because the inbox localizes the KIND and one kind alone would not
-- show that.
-- =================================================================================================
DECLARE @reqs TABLE (rn int IDENTITY(1,1), Kind nvarchar(30), LetterType nvarchar(60),
                     Addressee nvarchar(80), DaysAgo int, OnInDays int, Reason nvarchar(200));
INSERT INTO @reqs (Kind, LetterType, Addressee, DaysAgo, OnInDays, Reason) VALUES
 (N'Letter',     N'Salary certificate', N'البنك الأهلي',     7, NULL, N'مطلوبة لفتح حساب'),
 (N'Permission', NULL,                  NULL,                4, 3,    N'إنهاء إجراءات حكومية'),
 (N'Letter',     N'Employment letter',  N'السفارة',          2, NULL, N'مرفقة بطلب تأشيرة'),
 (N'Permission', NULL,                  NULL,                0, 1,    N'موعد طبي');

SET @i = (SELECT COUNT(*) FROM @reqs);
DECLARE @madeReq int = 0;

WHILE @i >= 1
BEGIN
    DECLARE @kind nvarchar(30), @lettert nvarchar(60), @addr nvarchar(80),
            @rago int, @onin int, @rwhy nvarchar(200);
    SELECT @kind = Kind, @lettert = LetterType, @addr = Addressee,
           @rago = DaysAgo, @onin = OnInDays, @rwhy = Reason
      FROM @reqs WHERE rn = @i;

    DECLARE @remp int = (SELECT EmployeeId FROM @people WHERE rn = ((@i - 1) % @nPeople) + 1);

    INSERT INTO EmployeeRequests
        (CompanyID, EmployeeID, RequestType, LetterType, Addressee, PermissionDate, FromTime, ToTime,
         Reason, Status, CurrentLevel, CurrentApproverEmployeeID, CreatedAt, CreatedBy)
    VALUES
        (@company, @remp, @kind, @lettert, @addr,
         CASE WHEN @onin IS NULL THEN NULL ELSE DATEADD(DAY, @onin, @today) END,
         CASE WHEN @onin IS NULL THEN NULL ELSE CAST('09:30' AS time) END,
         CASE WHEN @onin IS NULL THEN NULL ELSE CAST('12:00' AS time) END,
         @rwhy + N' [' + @marker + N']', 0, 1, @me,
         DATEADD(DAY, -@rago, SYSUTCDATETIME()), @remp);

    SET @madeReq = @madeReq + 1;
    SET @i = @i - 1;
END

COMMIT;

SELECT CAST(@madeLeave AS varchar) + N' leave + ' + CAST(@madeReq AS varchar)
     + N' employee request(s) now pending for approver ' + CAST(@me AS varchar) AS result;

-- =================================================================================================
-- TEARDOWN - removes exactly what this script created.
--
--   DELETE FROM LeaveRequests    WHERE Reason LIKE N'%[ZZ-APPROVALS-SEED]%';
--   DELETE FROM EmployeeRequests WHERE Reason LIKE N'%[ZZ-APPROVALS-SEED]%';
--
-- NOTE: the marker rides in Reason, which the inbox CAN show. It is bracketed and at the end so it
-- reads as an obvious test tag rather than as part of the sentence - these two tables have no
-- unused column to hide it in, and an untraceable seed is worse than a visible tag.
-- =================================================================================================
