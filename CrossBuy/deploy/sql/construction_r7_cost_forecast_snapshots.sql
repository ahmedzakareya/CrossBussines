-- D-12 authored analytical snapshots only. No default policy, business backfill or financial writes.
SET XACT_ABORT ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionBudgetVersions','U') IS NULL OR OBJECT_ID('dbo.ConstructionBudgetLines','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 THROW 51980,'Cost forecast prerequisites missing.',1;
IF OBJECT_ID('dbo.ConstructionCostForecastSnapshots','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionCostForecastSnapshots(
 ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionCostForecastSnapshots PRIMARY KEY,
 CompanyID int NOT NULL,ProjectId int NOT NULL,BudgetVersionId int NOT NULL,CurrencyId int NOT NULL,CurrencyDecimals int NOT NULL,
 PreviousSnapshotId int NULL,RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,SourceVersion nvarchar(64) NOT NULL,
 PolicyToken varbinary(16) NOT NULL,Method nvarchar(64) NOT NULL,SnapshotJson nvarchar(max) NOT NULL,
 EstimateAtCompletion decimal(28,8) NOT NULL,BudgetVariance decimal(28,8) NOT NULL,HasCommitmentWarning bit NOT NULL,
 Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
 CONSTRAINT AK_ConstructionCostForecastSnapshots_Scope UNIQUE(ID,ProjectId,CompanyID),
 CONSTRAINT UQ_ConstructionCostForecastSnapshots_Request UNIQUE(CompanyID,RequestId),
 CONSTRAINT FK_ConstructionCostForecastSnapshots_Budget FOREIGN KEY(BudgetVersionId,ProjectId,CompanyID) REFERENCES dbo.ConstructionBudgetVersions(ID,ProjectId,CompanyID),
 CONSTRAINT FK_ConstructionCostForecastSnapshots_Previous FOREIGN KEY(PreviousSnapshotId,ProjectId,CompanyID) REFERENCES dbo.ConstructionCostForecastSnapshots(ID,ProjectId,CompanyID),
 CONSTRAINT CK_ConstructionCostForecastSnapshots_Values CHECK(CompanyID>0 AND ProjectId>0 AND CurrencyId>0 AND CreatedBy>0 AND CurrencyDecimals BETWEEN 0 AND 8
  AND (PreviousSnapshotId IS NULL OR PreviousSnapshotId<ID) AND Method=N'ManualRemainingIncludingCommitmentsV1'
  AND LEN(LTRIM(RTRIM(Reason)))>0 AND LEN(RequestHash)=64 AND LEN(SourceVersion)=64 AND DATALENGTH(PolicyToken)=16
  AND RequestId<>'00000000-0000-0000-0000-000000000000'),
 CONSTRAINT CK_ConstructionCostForecastSnapshots_Json CHECK(ISJSON(SnapshotJson)=1 AND LEFT(LTRIM(SnapshotJson),1)=N'[')
 );
 CREATE UNIQUE INDEX UX_ConstructionCostForecastSnapshots_Root ON dbo.ConstructionCostForecastSnapshots(CompanyID,ProjectId) WHERE PreviousSnapshotId IS NULL;
 CREATE UNIQUE INDEX UX_ConstructionCostForecastSnapshots_Successor ON dbo.ConstructionCostForecastSnapshots(CompanyID,ProjectId,PreviousSnapshotId) WHERE PreviousSnapshotId IS NOT NULL;
END;
IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionCostForecastSnapshots'))<>19
 OR COL_LENGTH('dbo.ConstructionCostForecastSnapshots','PolicyToken')<>16
 OR COL_LENGTH('dbo.ConstructionCostForecastSnapshots','RequestHash')<>128
 OR COL_LENGTH('dbo.ConstructionCostForecastSnapshots','SnapshotJson')<>-1
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionCostForecastSnapshots') AND is_disabled=0 AND is_not_trusted=0)<>2
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionCostForecastSnapshots') AND is_disabled=0 AND is_not_trusted=0)<>2
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionCostForecastSnapshots') AND name=N'UX_ConstructionCostForecastSnapshots_Root' AND is_unique=1 AND is_disabled=0)
 OR NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionCostForecastSnapshots') AND name=N'UX_ConstructionCostForecastSnapshots_Successor' AND is_unique=1 AND is_disabled=0)
 THROW 51980,'Cost forecast schema mismatch; no automatic repair.',1;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCostForecastSnapshots_Immutable ON dbo.ConstructionCostForecastSnapshots
INSTEAD OF UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51981,'Cost forecasts are immutable; append a new authored snapshot.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionCostForecastSnapshots_Proof ON dbo.ConstructionCostForecastSnapshots
AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 -- Structural/policy/precision proof. Full GL/stock/source exposure witness is re-read by
 -- the canonical locked service, not reconstructed or claimed here as a second writer.
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=i.BudgetVersionId AND b.ProjectId=i.ProjectId AND b.CompanyID=i.CompanyID
  LEFT JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CostForecast'
  LEFT JOIN dbo.Currencies c ON c.ID=i.CurrencyId
  LEFT JOIN dbo.Companies co ON co.CompanyID=i.CompanyID
  WHERE b.Status<>N'Approved' OR b.IsBaseline<>1 OR b.CurrencyId<>i.CurrencyId OR c.ID IS NULL
   OR c.DecimalPlaces<>i.CurrencyDecimals OR co.DefaultCurrencyId IS NULL OR co.DefaultCurrencyId<>i.CurrencyId
   OR p.Mode IS NULL OR p.Mode<>N'ManualAuthorOwned' OR p.ConcurrencyToken<>i.PolicyToken)
  THROW 51982,'Forecast baseline, currency or explicit policy changed.',1;
 DECLARE @rows TABLE(HeaderId int,WbsNodeId int,CostCodeId int,Budget decimal(28,8),Actual decimal(28,8),Commitment decimal(28,8),Remaining decimal(28,8),Eac decimal(28,8),Variance decimal(28,8),Warning bit,RawJson nvarchar(max));
 INSERT @rows SELECT i.ID,j.WbsNodeId,j.CostCodeId,j.Budget,j.Actual,j.Commitment,j.Remaining,j.Eac,j.Variance,j.Warning,v.value
 FROM inserted i CROSS APPLY OPENJSON(i.SnapshotJson) v
 CROSS APPLY OPENJSON(v.value) WITH(WbsNodeId int,CostCodeId int,Budget decimal(28,8),Actual decimal(28,8),Commitment decimal(28,8),
 Remaining decimal(28,8) '$.RemainingCostIncludingCommitments',Eac decimal(28,8) '$.EstimateAtCompletion',Variance decimal(28,8) '$.BudgetVariance',Warning bit '$.BelowOutstandingCommitments') j;
 IF EXISTS(SELECT 1 FROM inserted i WHERE (SELECT COUNT(*) FROM @rows r WHERE r.HeaderId=i.ID) NOT BETWEEN 1 AND 2000
  OR (SELECT COUNT(*) FROM @rows r WHERE r.HeaderId=i.ID)<>(SELECT COUNT(*) FROM dbo.ConstructionBudgetLines b WHERE b.CompanyID=i.CompanyID AND b.ProjectId=i.ProjectId AND b.BudgetVersionId=i.BudgetVersionId))
  OR EXISTS(SELECT HeaderId,WbsNodeId,CostCodeId FROM @rows GROUP BY HeaderId,WbsNodeId,CostCodeId HAVING COUNT(*)<>1)
  THROW 51983,'Forecast dimension coverage is incomplete or duplicated.',1;
 IF EXISTS(SELECT 1 FROM @rows r JOIN inserted i ON i.ID=r.HeaderId
  LEFT JOIN dbo.ConstructionBudgetLines b ON b.CompanyID=i.CompanyID AND b.ProjectId=i.ProjectId AND b.BudgetVersionId=i.BudgetVersionId AND b.WbsNodeId=r.WbsNodeId AND b.CostCodeId=r.CostCodeId
  LEFT JOIN dbo.ConstructionWbsNodes w ON w.ID=r.WbsNodeId AND w.CompanyID=i.CompanyID AND w.ProjectId=i.ProjectId
  LEFT JOIN dbo.ConstructionCostCodes c ON c.ID=r.CostCodeId AND c.CompanyID=i.CompanyID
  WHERE b.ID IS NULL OR w.ID IS NULL OR w.Status<>N'Active' OR w.OwnsBudget<>1 OR c.ID IS NULL OR c.IsActive<>1 OR c.AllowDirectPosting<>1
   OR r.Budget IS NULL OR r.Actual IS NULL OR r.Commitment IS NULL OR r.Remaining IS NULL OR r.Eac IS NULL OR r.Variance IS NULL OR r.Warning IS NULL
   OR r.Budget<>b.Amount OR r.Budget<0 OR r.Commitment<0 OR r.Remaining<0 OR r.Eac<>r.Actual+r.Remaining OR r.Variance<>r.Budget-r.Eac
   OR r.Warning<>CASE WHEN r.Remaining<r.Commitment THEN 1 ELSE 0 END)
  THROW 51983,'Forecast row scope or calculation is invalid.',1;
 IF EXISTS(SELECT 1 FROM @rows r JOIN inserted i ON i.ID=r.HeaderId
  CROSS APPLY (VALUES ('Budget',r.Budget),('Actual',r.Actual),('Commitment',r.Commitment),('RemainingCostIncludingCommitments',r.Remaining),('EstimateAtCompletion',r.Eac),('BudgetVariance',r.Variance)) a(Name,Amount)
  WHERE a.Amount<>ROUND(a.Amount,i.CurrencyDecimals)
   OR TRY_CONVERT(decimal(38,18),JSON_VALUE(r.RawJson,'$.'+a.Name)) IS NULL
   OR TRY_CONVERT(decimal(38,18),JSON_VALUE(r.RawJson,'$.'+a.Name))<>a.Amount)
  THROW 51984,'Forecast amounts have invalid currency precision.',1;
 IF EXISTS(SELECT 1 FROM inserted i CROSS APPLY(SELECT SUM(r.Eac) Eac,SUM(r.Variance) Variance,MAX(CONVERT(int,r.Warning)) Warning FROM @rows r WHERE r.HeaderId=i.ID) a
  WHERE a.Eac<>i.EstimateAtCompletion OR a.Variance<>i.BudgetVariance OR a.Warning<>CONVERT(int,i.HasCommitmentWarning))
  THROW 51983,'Forecast totals do not match its stored rows.',1;
END;
GO
COMMIT;
