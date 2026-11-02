-- EXTERNAL REVIEW CANDIDATE ONLY. NOT EXECUTED / NOT RELEASE QUALIFIED.
-- R1-ACCOUNTING-AUDIT-SUCCESSOR-001 v1; coordinator-owned activation only.
-- Single batch, standalone transaction. No GO; no SQLCMD includes; no business-row DML.
-- Prerequisites: dbo.FiscalYears, dbo.FiscalPeriods, dbo.ProgressBillings already exist.
-- Both historical input orders are intended targets, NOT execution-proven.
-- Refuse unknown shape, disabled/untrusted constraints or noncanonical index options.
-- Locks are intrusive: coordinated maintenance window required. Never run at app startup.
IF @@TRANCOUNT <> 0
BEGIN
    RAISERROR ('Run this candidate standalone, outside any ambient transaction.', 16, 1);
    RETURN;
END;
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
DECLARE @Expected TABLE(TableName sysname, ColumnName sysname, TypeName sysname,
 MaxBytes smallint, ScaleValue tinyint, Nullable bit, IdentityValue bit, OptionalColumn bit);
INSERT @Expected VALUES
(N'FiscalYears',N'ID',N'int',4,0,0,1,0),
(N'FiscalPeriods',N'ID',N'int',4,0,0,1,0),
(N'ProgressBillings',N'ID',N'int',4,0,0,1,0),
(N'FiscalYears',N'CompanyID',N'int',4,0,0,0,0),
(N'FiscalPeriods',N'FiscalYearId',N'int',4,0,0,0,0),
(N'FiscalPeriods',N'ClosedBy',N'int',4,0,1,0,1),
(N'FiscalPeriods',N'ClosedAt',N'datetime2',8,7,1,0,1),
(N'FiscalPeriods',N'ReopenedBy',N'int',4,0,1,0,1),
(N'FiscalPeriods',N'ReopenedAt',N'datetime2',8,7,1,0,1),
(N'FiscalPeriods',N'ReopenReason',N'nvarchar',1000,0,1,0,1),
(N'ProgressBillings',N'UpdatedAt',N'datetime2',8,7,1,0,1),
(N'ProgressBillings',N'UpdatedBy',N'int',4,0,1,0,1),
(N'ProgressBillings',N'SubmittedAt',N'datetime2',8,7,1,0,1),
(N'ProgressBillings',N'SubmittedBy',N'int',4,0,1,0,1),
(N'ProgressBillings',N'ApprovedAt',N'datetime2',8,7,1,0,1),
(N'ProgressBillings',N'ApprovedBy',N'int',4,0,1,0,1),
(N'ProgressBillings',N'PostedBy',N'int',4,0,1,0,1),
(N'AccountingPeriodAudits',N'ID',N'int',4,0,0,1,0),
(N'AccountingPeriodAudits',N'CompanyID',N'int',4,0,0,0,0),
(N'AccountingPeriodAudits',N'FiscalPeriodId',N'int',4,0,0,0,0),
(N'AccountingPeriodAudits',N'FromStatus',N'nvarchar',40,0,0,0,0),
(N'AccountingPeriodAudits',N'ToStatus',N'nvarchar',40,0,0,0,0),
(N'AccountingPeriodAudits',N'ActorEmployeeId',N'int',4,0,0,0,0),
(N'AccountingPeriodAudits',N'OccurredAt',N'datetime2',8,7,0,0,0),
(N'AccountingPeriodAudits',N'Reason',N'nvarchar',1000,0,1,0,0);
DECLARE @Audit int, @IndexState varchar(12) = 'Absent', @IndexFilegroup sysname;
DECLARE @LockedRows bigint, @LockResult int;
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC @LockResult = sys.sp_getapplock @Resource=N'CrossBusiness:R1:AccountingAuditSuccessor:001',
  @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=15000;
 IF @LockResult < 0 THROW 51100, 'Accounting successor lock unavailable.', 1;
 IF COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'),0) <> 1
  THROW 51101, 'Complete database metadata visibility required.', 1;
 IF OBJECT_ID(N'dbo.FiscalYears',N'U') IS NULL OR OBJECT_ID(N'dbo.FiscalPeriods',N'U') IS NULL
 OR OBJECT_ID(N'dbo.ProgressBillings',N'U') IS NULL
  THROW 51102, 'Missing prerequisite table; this is not an empty database bootstrap.', 1;
 SET @Audit=OBJECT_ID(N'dbo.AccountingPeriodAudits',N'U');
 IF @Audit IS NULL AND OBJECT_ID(N'dbo.AccountingPeriodAudits') IS NOT NULL
  THROW 51103, 'Audit object exists but is not an ordinary table.', 1;
 IF EXISTS(SELECT 1 FROM sys.tables WHERE object_id IN
  (OBJECT_ID(N'dbo.FiscalYears'),OBJECT_ID(N'dbo.FiscalPeriods'),OBJECT_ID(N'dbo.ProgressBillings'),@Audit)
  AND (is_memory_optimized=1 OR temporal_type<>0 OR is_filetable=1 OR is_replicated=1 OR is_tracked_by_cdc=1))
  THROW 51104, 'Special table storage/lifecycle requires separate review.', 1;
 IF EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=0 AND is_disabled=0)
  THROW 51105, 'Enabled database DDL trigger requires separate review.', 1;
 -- Hold row writers through preflight, snapshot, DDL and preservation checks.
 EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.FiscalYears WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@LockedRows OUTPUT;
 EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.FiscalPeriods WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@LockedRows OUTPUT;
 EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.ProgressBillings WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@LockedRows OUTPUT;
 IF @Audit IS NOT NULL
  EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.AccountingPeriodAudits WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@LockedRows OUTPUT;
 -- No persistent DDL before the PREFLIGHT COMPLETE marker below.
 IF EXISTS(
  SELECT 1 FROM @Expected e
  LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'dbo.'+e.TableName,N'U') AND c.name=e.ColumnName
  LEFT JOIN sys.types t ON t.user_type_id=c.user_type_id
  WHERE NOT(e.TableName=N'AccountingPeriodAudits' AND @Audit IS NULL)
  AND ((c.column_id IS NULL AND e.OptionalColumn=0)
    OR (c.column_id IS NOT NULL AND (t.name<>e.TypeName OR t.is_user_defined=1
      OR c.user_type_id<>c.system_type_id OR c.max_length<>e.MaxBytes OR c.scale<>e.ScaleValue
      OR c.is_nullable<>e.Nullable OR c.is_identity<>e.IdentityValue OR c.is_computed=1
      OR c.is_sparse=1 OR c.generated_always_type<>0 OR c.rule_object_id<>0 OR c.is_rowguidcol=1 OR c.encryption_type IS NOT NULL
      OR (e.OptionalColumn=1 AND c.default_object_id<>0)))))
  THROW 51106, 'Missing or incompatible column contract; no column coercion authorized.', 1;
 IF EXISTS(SELECT 1 FROM sys.identity_columns i JOIN @Expected e
  ON i.object_id=OBJECT_ID(N'dbo.'+e.TableName) AND i.name=e.ColumnName
  WHERE e.IdentityValue=1 AND (CONVERT(decimal(38,0),i.seed_value)<>1
   OR CONVERT(decimal(38,0),i.increment_value)<>1 OR i.is_not_for_replication=1))
  THROW 51107, 'Unexpected identity definition.', 1;
 IF @Audit IS NOT NULL AND EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id=@Audit
  AND NOT EXISTS(SELECT 1 FROM @Expected e WHERE e.TableName=N'AccountingPeriodAudits' AND e.ColumnName=c.name))
  THROW 51108, 'Unexpected audit columns require review.', 1;
 IF @Audit IS NOT NULL AND EXISTS(SELECT 1 FROM sys.columns WHERE object_id=@Audit
   AND name IN(N'FromStatus',N'ToStatus',N'Reason')
   AND collation_name<>CONVERT(sysname,DATABASEPROPERTYEX(DB_NAME(),'Collation')))
  THROW 51109, 'Audit collation differs from historical contract.', 1;
 -- Prerequisite IDs must be unique; FiscalPeriods.ID also backs the FK.
 IF EXISTS(SELECT 1 FROM (VALUES(N'FiscalYears'),(N'FiscalPeriods'),(N'ProgressBillings')) p(TableName)
 WHERE NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=OBJECT_ID(N'dbo.'+p.TableName)
  AND i.is_unique=1 AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
  AND i.type IN(1,2)
  AND (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id=i.object_id AND k.index_id=i.index_id AND k.key_ordinal>0)=1
  AND EXISTS(SELECT 1 FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
   WHERE k.object_id=i.object_id AND k.index_id=i.index_id AND k.key_ordinal=1 AND c.name=N'ID')))
  THROW 51110, 'Prerequisite ID has no usable unique candidate key.', 1;
 IF EXISTS(SELECT 1 FROM sys.objects WHERE schema_id=SCHEMA_ID(N'dbo')
  AND name IN(N'PK_AccountingPeriodAudits',N'DF_AccountingPeriodAudits_At',N'CK_AccountingPeriodAudits_Keys',N'CK_AccountingPeriodAudits_Moved',N'FK_AccountingPeriodAudits_Period')
  AND parent_object_id<>ISNULL(@Audit,0))
  THROW 51111, 'Reserved constraint name belongs to another object.', 1;
 IF @Audit IS NOT NULL
 BEGIN
  IF NOT EXISTS(SELECT 1 FROM sys.key_constraints k JOIN sys.indexes i ON i.object_id=k.parent_object_id AND i.index_id=k.unique_index_id
   WHERE k.parent_object_id=@Audit AND k.type='PK' AND k.name=N'PK_AccountingPeriodAudits'
   AND i.type=1 AND i.is_unique=1 AND i.is_disabled=0 AND i.is_hypothetical=0 AND i.has_filter=0
   AND (SELECT COUNT(*) FROM sys.index_columns x WHERE x.object_id=@Audit AND x.index_id=i.index_id AND x.key_ordinal>0)=1
   AND EXISTS(SELECT 1 FROM sys.index_columns x JOIN sys.columns c ON c.object_id=x.object_id AND c.column_id=x.column_id
    WHERE x.object_id=@Audit AND x.index_id=i.index_id AND x.key_ordinal=1 AND x.is_descending_key=0 AND c.name=N'ID'))
   THROW 51112, 'Existing audit primary key is incompatible; no key repair authorized.', 1;
  IF EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=@Audit
   AND (name NOT IN(N'CK_AccountingPeriodAudits_Keys',N'CK_AccountingPeriodAudits_Moved')
     OR is_disabled=1 OR is_not_trusted=1 OR is_not_for_replication=1))
   THROW 51113, 'Unknown, disabled or untrusted audit check constraint.', 1;
  IF EXISTS(SELECT 1 FROM sys.check_constraints c
   CROSS APPLY(SELECT UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(c.definition,N'[',N''),N']',N''),N'(',N''),N')',N''),N' ',N''),CHAR(9),N''),CHAR(10),N''),CHAR(13),N'')) AS Canon) n
   WHERE c.parent_object_id=@Audit AND
    ((c.name=N'CK_AccountingPeriodAudits_Keys' AND n.Canon<>N'COMPANYID>0ANDFISCALPERIODID>0ANDACTOREMPLOYEEID>0')
    OR (c.name=N'CK_AccountingPeriodAudits_Moved' AND n.Canon<>N'FROMSTATUS<>TOSTATUS')))
   THROW 51114, 'Check definition differs; equivalent alternatives require review.', 1;
  IF EXISTS(SELECT 1 FROM sys.foreign_keys f WHERE f.parent_object_id=@Audit
   AND (f.name<>N'FK_AccountingPeriodAudits_Period' OR f.referenced_object_id<>OBJECT_ID(N'dbo.FiscalPeriods')
    OR f.is_disabled=1 OR f.is_not_trusted=1 OR f.is_not_for_replication=1
    OR f.delete_referential_action<>0 OR f.update_referential_action<>0
    OR (SELECT COUNT(*) FROM sys.foreign_key_columns k WHERE k.constraint_object_id=f.object_id)<>1
    OR NOT EXISTS(SELECT 1 FROM sys.foreign_key_columns k
      JOIN sys.columns pc ON pc.object_id=k.parent_object_id AND pc.column_id=k.parent_column_id
      JOIN sys.columns rc ON rc.object_id=k.referenced_object_id AND rc.column_id=k.referenced_column_id
      WHERE k.constraint_object_id=f.object_id AND pc.name=N'FiscalPeriodId' AND rc.name=N'ID')))
   THROW 51115, 'Foreign-key shape/trust/actions differ from the canonical contract.', 1;
  IF EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id=@Audit AND c.default_object_id<>0
   AND NOT EXISTS(SELECT 1 FROM sys.default_constraints d WHERE d.object_id=c.default_object_id AND d.parent_object_id=@Audit
    AND c.name=N'OccurredAt' AND d.name=N'DF_AccountingPeriodAudits_At'
    AND UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(d.definition,N'[',N''),N']',N''),N'(',N''),N')',N''),N' ',N''),CHAR(9),N''),CHAR(10),N''),CHAR(13),N''))=N'SYSUTCDATETIME'))
   THROW 51116, 'Unexpected default definition; no default replacement authorized.', 1;
  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=@Audit AND index_id>0
   AND name NOT IN(N'PK_AccountingPeriodAudits',N'IX_AccountingPeriodAudits_ByPeriod'))
   THROW 51117, 'Additional audit index/constraint needs explicit review.', 1;
  IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=@Audit AND name=N'IX_AccountingPeriodAudits_ByPeriod')
  BEGIN
   DECLARE @Ix int=(SELECT index_id FROM sys.indexes WHERE object_id=@Audit AND name=N'IX_AccountingPeriodAudits_ByPeriod');
   IF EXISTS(SELECT 1 FROM sys.indexes i LEFT JOIN sys.data_spaces d ON d.data_space_id=i.data_space_id
    WHERE i.object_id=@Audit AND i.index_id=@Ix AND
    (i.type<>2 OR i.is_unique=1 OR i.is_disabled=1 OR i.is_hypothetical=1 OR i.has_filter=1
     OR i.is_padded=1 OR i.fill_factor NOT IN(0,100) OR i.ignore_dup_key=1
     OR i.allow_row_locks=0 OR i.allow_page_locks=0 OR ISNULL(d.type,'')<>'FG'))
    THROW 51118, 'Index state/options unsupported.', 1;
   IF EXISTS(SELECT 1 FROM sys.partitions WHERE object_id=@Audit AND index_id=@Ix AND data_compression<>0)
    THROW 51119, 'Compressed index requires separate deployment review.', 1;
   DECLARE @Keys nvarchar(400), @Includes nvarchar(400);
   SELECT @Keys=STRING_AGG(CONVERT(nvarchar(max),c.name+CASE WHEN k.is_descending_key=1 THEN N':D' ELSE N':A' END),N',') WITHIN GROUP(ORDER BY k.key_ordinal)
    FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
    WHERE k.object_id=@Audit AND k.index_id=@Ix AND k.key_ordinal>0;
   SELECT @Includes=STRING_AGG(CONVERT(nvarchar(max),c.name),N',') WITHIN GROUP(ORDER BY c.name COLLATE Latin1_General_100_BIN2)
    FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
    WHERE k.object_id=@Audit AND k.index_id=@Ix AND k.is_included_column=1;
   IF @Keys=N'CompanyID:A,FiscalPeriodId:A,OccurredAt:A' AND @Includes IS NULL SET @IndexState='Legacy';
   ELSE IF @Keys=N'CompanyID:A,FiscalPeriodId:A,OccurredAt:D' AND @Includes=N'ActorEmployeeId,FromStatus,Reason,ToStatus' SET @IndexState='Canonical';
   ELSE THROW 51120, 'Index keys/direction/includes are neither known input nor canonical.', 1;
   SELECT @IndexFilegroup=d.name FROM sys.indexes i JOIN sys.data_spaces d ON d.data_space_id=i.data_space_id WHERE i.object_id=@Audit AND i.index_id=@Ix;
  END;
  -- Dynamic SQL compiles after column verification, and only if the table exists.
  EXEC sys.sp_executesql N'IF EXISTS(SELECT 1 FROM dbo.AccountingPeriodAudits WHERE CompanyID<=0 OR FiscalPeriodId<=0 OR ActorEmployeeId<=0 OR FromStatus=ToStatus)
    THROW 51121, ''Invalid historic audit keys or no-op transition; no row repair authorized.'', 1;
   IF EXISTS(SELECT 1 FROM dbo.AccountingPeriodAudits a LEFT JOIN dbo.FiscalPeriods p ON p.ID=a.FiscalPeriodId
    LEFT JOIN dbo.FiscalYears y ON y.ID=p.FiscalYearId WHERE p.ID IS NULL OR y.ID IS NULL OR y.CompanyID<>a.CompanyID)
    THROW 51122, ''Orphan or foreign-company historical audit; no row repair authorized.'', 1;';
 END;
 DECLARE @AuditBefore TABLE([ID] int NULL,[CompanyID] int NULL,[FiscalPeriodId] int NULL,[FromStatus] varbinary(40) NULL,[ToStatus] varbinary(40) NULL,[ActorEmployeeId] int NULL,[OccurredAt] datetime2(7) NULL,[Reason] varbinary(1000) NULL);
 DECLARE @AuditAfter TABLE([ID] int NULL,[CompanyID] int NULL,[FiscalPeriodId] int NULL,[FromStatus] varbinary(40) NULL,[ToStatus] varbinary(40) NULL,[ActorEmployeeId] int NULL,[OccurredAt] datetime2(7) NULL,[Reason] varbinary(1000) NULL);
 DECLARE @AuditRead nvarchar(max)=N'SELECT '+N'[ID]'+N','+N'[CompanyID]'+N','+N'[FiscalPeriodId]'+N','+N'CONVERT(varbinary(40),[FromStatus])'+N','+N'CONVERT(varbinary(40),[ToStatus])'+N','+N'[ActorEmployeeId]'+N','+N'[OccurredAt]'+N','+N'CONVERT(varbinary(1000),[Reason])'+N' FROM dbo.AccountingPeriodAudits';
 IF @Audit IS NOT NULL
 INSERT @AuditBefore EXEC sys.sp_executesql @AuditRead;
 DECLARE @PeriodBefore TABLE([ID] int NULL,[ClosedBy] int NULL,[ClosedAt] datetime2(7) NULL,[ReopenedBy] int NULL,[ReopenedAt] datetime2(7) NULL,[ReopenReason] varbinary(1000) NULL);
 DECLARE @PeriodAfter TABLE([ID] int NULL,[ClosedBy] int NULL,[ClosedAt] datetime2(7) NULL,[ReopenedBy] int NULL,[ReopenedAt] datetime2(7) NULL,[ReopenReason] varbinary(1000) NULL);
 DECLARE @PeriodRead nvarchar(max)=N'SELECT '+N'[ID]'+N','+CASE WHEN COL_LENGTH(N'dbo.FiscalPeriods',N'ClosedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[ClosedBy]' END+N','+CASE WHEN COL_LENGTH(N'dbo.FiscalPeriods',N'ClosedAt') IS NULL THEN N'CAST(NULL AS datetime2(7))' ELSE N'[ClosedAt]' END+N','+CASE WHEN COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[ReopenedBy]' END+N','+CASE WHEN COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenedAt') IS NULL THEN N'CAST(NULL AS datetime2(7))' ELSE N'[ReopenedAt]' END+N','+CASE WHEN COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenReason') IS NULL THEN N'CAST(NULL AS varbinary(1000))' ELSE N'CONVERT(varbinary(1000),[ReopenReason])' END+N' FROM dbo.FiscalPeriods';
 INSERT @PeriodBefore EXEC sys.sp_executesql @PeriodRead;
 DECLARE @BillingBefore TABLE([ID] int NULL,[UpdatedAt] datetime2(7) NULL,[UpdatedBy] int NULL,[SubmittedAt] datetime2(7) NULL,[SubmittedBy] int NULL,[ApprovedAt] datetime2(7) NULL,[ApprovedBy] int NULL,[PostedBy] int NULL);
 DECLARE @BillingAfter TABLE([ID] int NULL,[UpdatedAt] datetime2(7) NULL,[UpdatedBy] int NULL,[SubmittedAt] datetime2(7) NULL,[SubmittedBy] int NULL,[ApprovedAt] datetime2(7) NULL,[ApprovedBy] int NULL,[PostedBy] int NULL);
 DECLARE @BillingRead nvarchar(max)=N'SELECT '+N'[ID]'+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'UpdatedAt') IS NULL THEN N'CAST(NULL AS datetime2(7))' ELSE N'[UpdatedAt]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'UpdatedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[UpdatedBy]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'SubmittedAt') IS NULL THEN N'CAST(NULL AS datetime2(7))' ELSE N'[SubmittedAt]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'SubmittedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[SubmittedBy]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'ApprovedAt') IS NULL THEN N'CAST(NULL AS datetime2(7))' ELSE N'[ApprovedAt]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'ApprovedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[ApprovedBy]' END+N','+CASE WHEN COL_LENGTH(N'dbo.ProgressBillings',N'PostedBy') IS NULL THEN N'CAST(NULL AS int)' ELSE N'[PostedBy]' END+N' FROM dbo.ProgressBillings';
 INSERT @BillingBefore EXEC sys.sp_executesql @BillingRead;
 -- PREFLIGHT COMPLETE. First persistent DDL starts here.
 IF @Audit IS NULL
  EXEC sys.sp_executesql N'CREATE TABLE dbo.AccountingPeriodAudits(
   ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AccountingPeriodAudits PRIMARY KEY CLUSTERED,
   CompanyID int NOT NULL, FiscalPeriodId int NOT NULL,
   FromStatus nvarchar(20) NOT NULL, ToStatus nvarchar(20) NOT NULL,
   ActorEmployeeId int NOT NULL, OccurredAt datetime2(7) NOT NULL
    CONSTRAINT DF_AccountingPeriodAudits_At DEFAULT(SYSUTCDATETIME()),
   Reason nvarchar(500) NULL);';
 SET @Audit=OBJECT_ID(N'dbo.AccountingPeriodAudits',N'U');
 IF COL_LENGTH(N'dbo.FiscalPeriods',N'ClosedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.FiscalPeriods ADD [ClosedBy] int NULL;';
 IF COL_LENGTH(N'dbo.FiscalPeriods',N'ClosedAt') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.FiscalPeriods ADD [ClosedAt] datetime2(7) NULL;';
 IF COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.FiscalPeriods ADD [ReopenedBy] int NULL;';
 IF COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenedAt') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.FiscalPeriods ADD [ReopenedAt] datetime2(7) NULL;';
 IF COL_LENGTH(N'dbo.FiscalPeriods',N'ReopenReason') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.FiscalPeriods ADD [ReopenReason] nvarchar(500) NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'UpdatedAt') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [UpdatedAt] datetime2(7) NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'UpdatedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [UpdatedBy] int NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'SubmittedAt') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [SubmittedAt] datetime2(7) NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'SubmittedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [SubmittedBy] int NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'ApprovedAt') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [ApprovedAt] datetime2(7) NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'ApprovedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [ApprovedBy] int NULL;';
 IF COL_LENGTH(N'dbo.ProgressBillings',N'PostedBy') IS NULL
  EXEC sys.sp_executesql N'ALTER TABLE dbo.ProgressBillings ADD [PostedBy] int NULL;';
 IF NOT EXISTS(SELECT 1 FROM sys.default_constraints WHERE parent_object_id=@Audit AND parent_column_id=COLUMNPROPERTY(@Audit,N'OccurredAt','ColumnId'))
  EXEC sys.sp_executesql N'ALTER TABLE dbo.AccountingPeriodAudits ADD CONSTRAINT DF_AccountingPeriodAudits_At DEFAULT(SYSUTCDATETIME()) FOR OccurredAt;';
 IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=@Audit AND name=N'CK_AccountingPeriodAudits_Keys')
  EXEC sys.sp_executesql N'ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK ADD CONSTRAINT CK_AccountingPeriodAudits_Keys CHECK(CompanyID>0 AND FiscalPeriodId>0 AND ActorEmployeeId>0);';
 IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=@Audit AND name=N'CK_AccountingPeriodAudits_Moved')
  EXEC sys.sp_executesql N'ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK ADD CONSTRAINT CK_AccountingPeriodAudits_Moved CHECK(FromStatus<>ToStatus);';
 IF NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=@Audit AND name=N'FK_AccountingPeriodAudits_Period')
  EXEC sys.sp_executesql N'ALTER TABLE dbo.AccountingPeriodAudits WITH CHECK ADD CONSTRAINT FK_AccountingPeriodAudits_Period FOREIGN KEY(FiscalPeriodId) REFERENCES dbo.FiscalPeriods(ID) ON DELETE NO ACTION ON UPDATE NO ACTION;';
 IF @IndexState IN('Absent','Legacy')
 BEGIN
  DECLARE @IndexSql nvarchar(max)=N'CREATE NONCLUSTERED INDEX IX_AccountingPeriodAudits_ByPeriod ON dbo.AccountingPeriodAudits(CompanyID,FiscalPeriodId,OccurredAt DESC) INCLUDE(FromStatus,ToStatus,ActorEmployeeId,Reason)';
  IF @IndexState='Legacy' SET @IndexSql+=N' WITH(DROP_EXISTING=ON) ON '+QUOTENAME(@IndexFilegroup);
  SET @IndexSql+=N';';
  EXEC sys.sp_executesql @IndexSql;
 END;
 -- Postconditions fail within the same transaction; no success before preservation checks.
 IF EXISTS(SELECT 1 FROM @Expected e LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'dbo.'+e.TableName) AND c.name=e.ColumnName
  LEFT JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.column_id IS NULL OR t.name<>e.TypeName
  OR c.max_length<>e.MaxBytes OR c.scale<>e.ScaleValue OR c.is_nullable<>e.Nullable OR c.is_identity<>e.IdentityValue)
  THROW 51130, 'Post-DDL column contract failed.', 1;
 IF (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=@Audit AND is_disabled=0 AND is_not_trusted=0
  AND name IN(N'CK_AccountingPeriodAudits_Keys',N'CK_AccountingPeriodAudits_Moved'))<>2
  OR NOT EXISTS(SELECT 1 FROM sys.foreign_keys WHERE parent_object_id=@Audit AND name=N'FK_AccountingPeriodAudits_Period' AND is_disabled=0 AND is_not_trusted=0)
  OR NOT EXISTS(SELECT 1 FROM sys.default_constraints WHERE parent_object_id=@Audit AND name=N'DF_AccountingPeriodAudits_At')
  THROW 51131, 'Post-DDL constraint/default contract failed.', 1;
 DECLARE @FinalIndex int=(SELECT index_id FROM sys.indexes WHERE object_id=@Audit AND name=N'IX_AccountingPeriodAudits_ByPeriod');
 DECLARE @FinalKeys nvarchar(400), @FinalIncludes nvarchar(400);
 SELECT @FinalKeys=STRING_AGG(CONVERT(nvarchar(max),c.name+CASE WHEN k.is_descending_key=1 THEN N':D' ELSE N':A' END),N',') WITHIN GROUP(ORDER BY k.key_ordinal)
  FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id WHERE k.object_id=@Audit AND k.index_id=@FinalIndex AND k.key_ordinal>0;
 SELECT @FinalIncludes=STRING_AGG(CONVERT(nvarchar(max),c.name),N',') WITHIN GROUP(ORDER BY c.name COLLATE Latin1_General_100_BIN2)
  FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id WHERE k.object_id=@Audit AND k.index_id=@FinalIndex AND k.is_included_column=1;
 IF ISNULL(@FinalKeys,N'')<>N'CompanyID:A,FiscalPeriodId:A,OccurredAt:D' OR ISNULL(@FinalIncludes,N'')<>N'ActorEmployeeId,FromStatus,Reason,ToStatus'
  THROW 51132, 'Post-DDL index contract failed.', 1;
 INSERT @AuditAfter EXEC sys.sp_executesql N'SELECT [ID],[CompanyID],[FiscalPeriodId],CONVERT(varbinary(40),[FromStatus]),CONVERT(varbinary(40),[ToStatus]),[ActorEmployeeId],[OccurredAt],CONVERT(varbinary(1000),[Reason]) FROM dbo.AccountingPeriodAudits';
 IF EXISTS(SELECT * FROM @AuditBefore EXCEPT SELECT * FROM @AuditAfter) OR EXISTS(SELECT * FROM @AuditAfter EXCEPT SELECT * FROM @AuditBefore)
 THROW 51140, 'Audit values changed unexpectedly; rolling back.', 1;
 INSERT @PeriodAfter EXEC sys.sp_executesql N'SELECT [ID],[ClosedBy],[ClosedAt],[ReopenedBy],[ReopenedAt],CONVERT(varbinary(1000),[ReopenReason]) FROM dbo.FiscalPeriods';
 IF EXISTS(SELECT * FROM @PeriodBefore EXCEPT SELECT * FROM @PeriodAfter) OR EXISTS(SELECT * FROM @PeriodAfter EXCEPT SELECT * FROM @PeriodBefore)
 THROW 51140, 'Period values changed unexpectedly; rolling back.', 1;
 INSERT @BillingAfter EXEC sys.sp_executesql N'SELECT [ID],[UpdatedAt],[UpdatedBy],[SubmittedAt],[SubmittedBy],[ApprovedAt],[ApprovedBy],[PostedBy] FROM dbo.ProgressBillings';
 IF EXISTS(SELECT * FROM @BillingBefore EXCEPT SELECT * FROM @BillingAfter) OR EXISTS(SELECT * FROM @BillingAfter EXCEPT SELECT * FROM @BillingBefore)
 THROW 51140, 'Billing values changed unexpectedly; rolling back.', 1;

 COMMIT TRANSACTION;
 SELECT N'COMMITTED' AS SuccessorStatus, N'R1-ACCOUNTING-AUDIT-SUCCESSOR-001' AS SliceId,
  @IndexState AS PreviousIndexShape;
END TRY
BEGIN CATCH
 IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
