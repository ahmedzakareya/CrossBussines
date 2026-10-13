-- R1-HR-DOCUMENT-ATTACHMENTS-003: reviewed successor candidate.
-- Single transaction; company-aware, serialized backfill; no row deletion or repair.
-- Existing incompatible schemas/indexes are refused rather than silently blessed.
IF @@TRANCOUNT<>0
BEGIN
 RAISERROR('Run the HR successor outside any ambient transaction.',16,1);
 RETURN;
END;
SET NOCOUNT ON;
SET XACT_ABORT ON;
DECLARE @Lock int,@Rows bigint,@Attachment int;
DECLARE @Expected TABLE(Name sysname,TypeName sysname,MaxBytes int,ScaleValue int,Nullable bit,IdentityValue bit);
INSERT @Expected VALUES
(N'ID',N'int',4,0,0,1),(N'CompanyID',N'int',4,0,0,0),
(N'OwnerKind',N'nvarchar',40,0,0,0),(N'OwnerID',N'int',4,0,0,0),
(N'FilePath',N'nvarchar',800,0,0,0),(N'FileName',N'nvarchar',520,0,1,0),
(N'UploadedAt',N'datetime2',8,7,1,0);
BEGIN TRY
 BEGIN TRANSACTION;
 EXEC @Lock=sys.sp_getapplock @Resource=N'CrossBusiness:R1:HrAttachments:003',
  @LockMode=N'Exclusive',@LockOwner=N'Transaction',@LockTimeout=15000;
 IF @Lock<0 THROW 51200,'HR successor lock unavailable.',1;
 IF COALESCE(HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','VIEW DEFINITION'),0)<>1
  THROW 51201,'Complete metadata visibility required.',1;
 IF OBJECT_ID(N'dbo.EmploymentContracts',N'U') IS NULL OR OBJECT_ID(N'dbo.EmployeeDocuments',N'U') IS NULL
  THROW 51202,'Missing HR source table.',1;
 SET @Attachment=OBJECT_ID(N'dbo.HrDocumentAttachments',N'U');
 IF @Attachment IS NULL AND OBJECT_ID(N'dbo.HrDocumentAttachments') IS NOT NULL
  THROW 51203,'Attachment object is not an ordinary table.',1;
 IF EXISTS(SELECT 1 FROM sys.tables WHERE object_id IN(OBJECT_ID(N'dbo.EmploymentContracts'),OBJECT_ID(N'dbo.EmployeeDocuments'),@Attachment)
  AND (is_memory_optimized=1 OR temporal_type<>0 OR is_filetable=1 OR is_replicated=1 OR is_tracked_by_cdc=1))
  THROW 51204,'Special table lifecycle requires separate review.',1;
 IF EXISTS(SELECT 1 FROM sys.triggers WHERE is_disabled=0 AND
  (parent_class=0 OR parent_id IN(OBJECT_ID(N'dbo.EmploymentContracts'),OBJECT_ID(N'dbo.EmployeeDocuments'),@Attachment)))
  THROW 51205,'Enabled triggers require separate review.',1;
 EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.EmploymentContracts WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@Rows OUTPUT;
 EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.EmployeeDocuments WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@Rows OUTPUT;
 IF @Attachment IS NOT NULL
  EXEC sys.sp_executesql N'SELECT @n=COUNT_BIG(*) FROM dbo.HrDocumentAttachments WITH(TABLOCKX,HOLDLOCK)',N'@n bigint OUTPUT',@n=@Rows OUTPUT;
 IF EXISTS(SELECT 1 FROM (VALUES(N'EmploymentContracts'),(N'EmployeeDocuments')) p(TableName)
  CROSS JOIN (VALUES(N'ID',N'int',4),(N'CompanyID',N'int',4),(N'FilePath',N'nvarchar',800),(N'CreatedAt',N'datetime2',8)) e(Name,TypeName,MaxBytes)
  LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(N'dbo.'+p.TableName) AND c.name=e.Name
  LEFT JOIN sys.types t ON t.user_type_id=c.user_type_id
  WHERE c.column_id IS NULL OR t.name<>e.TypeName OR c.max_length<>e.MaxBytes OR c.is_computed=1 OR c.is_sparse=1
   OR c.encryption_type IS NOT NULL OR c.user_type_id<>c.system_type_id
   OR (e.Name IN(N'ID',N'CompanyID') AND c.is_nullable=1) OR (e.Name=N'CreatedAt' AND c.scale<>7))
  THROW 51206,'Incompatible HR source column shape.',1;
 IF @Attachment IS NOT NULL
 BEGIN
  IF EXISTS(SELECT 1 FROM @Expected e LEFT JOIN sys.columns c ON c.object_id=@Attachment AND c.name=e.Name
   LEFT JOIN sys.types t ON t.user_type_id=c.user_type_id WHERE c.column_id IS NULL
   OR t.name<>e.TypeName OR c.user_type_id<>c.system_type_id OR c.max_length<>e.MaxBytes OR c.scale<>e.ScaleValue
   OR c.is_nullable<>e.Nullable OR c.is_identity<>e.IdentityValue OR c.is_computed=1 OR c.default_object_id<>0
   OR c.is_sparse=1 OR c.encryption_type IS NOT NULL)
   THROW 51207,'Incompatible attachment column contract.',1;
  IF EXISTS(SELECT 1 FROM sys.columns c WHERE c.object_id=@Attachment AND NOT EXISTS(SELECT 1 FROM @Expected e WHERE e.Name=c.name))
   THROW 51208,'Additional attachment columns require review.',1;
  IF NOT EXISTS(SELECT 1 FROM sys.indexes i WHERE i.object_id=@Attachment AND i.is_primary_key=1 AND i.is_unique=1 AND i.is_disabled=0
   AND (SELECT COUNT(*) FROM sys.index_columns k WHERE k.object_id=@Attachment AND k.index_id=i.index_id AND k.key_ordinal>0)=1
   AND EXISTS(SELECT 1 FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
    WHERE k.object_id=@Attachment AND k.index_id=i.index_id AND k.key_ordinal=1 AND c.name=N'ID'))
   THROW 51209,'Attachment primary key requires review.',1;
  DECLARE @Index int=(SELECT index_id FROM sys.indexes WHERE object_id=@Attachment AND name=N'IX_HrDocAtt_Owner');
  IF @Index IS NOT NULL
  BEGIN
   IF EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=@Attachment AND index_id=@Index
    AND (type<>2 OR is_unique=1 OR is_disabled=1 OR is_hypothetical=1 OR has_filter=1 OR allow_row_locks=0 OR allow_page_locks=0))
    THROW 51210,'Unsupported owner index options.',1;
   DECLARE @Keys nvarchar(max);
   SELECT @Keys=STRING_AGG(CONVERT(nvarchar(max),c.name+CASE WHEN k.is_descending_key=1 THEN N':D' ELSE N':A' END),N',') WITHIN GROUP(ORDER BY k.key_ordinal)
    FROM sys.index_columns k JOIN sys.columns c ON c.object_id=k.object_id AND c.column_id=k.column_id
    WHERE k.object_id=@Attachment AND k.index_id=@Index AND k.key_ordinal>0;
   IF ISNULL(@Keys,N'')<>N'CompanyID:A,OwnerKind:A,OwnerID:A'
    OR EXISTS(SELECT 1 FROM sys.index_columns WHERE object_id=@Attachment AND index_id=@Index AND is_included_column=1)
    THROW 51211,'Incompatible same-name owner index; no automatic replacement.',1;
  END;
 END;
 IF @Attachment IS NULL AND EXISTS(SELECT 1 FROM sys.objects WHERE schema_id=SCHEMA_ID(N'dbo') AND name=N'PK_HrDocumentAttachments_R1')
  THROW 51212,'Reserved attachment primary-key name is occupied.',1;
 EXEC sys.sp_executesql N'IF EXISTS(SELECT 1 FROM dbo.EmploymentContracts WHERE FilePath IS NOT NULL AND LTRIM(RTRIM(FilePath))<>N'''' AND (CompanyID<=0 OR ID<=0))
  OR EXISTS(SELECT 1 FROM dbo.EmployeeDocuments WHERE FilePath IS NOT NULL AND LTRIM(RTRIM(FilePath))<>N'''' AND (CompanyID<=0 OR ID<=0))
  THROW 51213,''Invalid source owner/company; no row repair authorized.'',1;';
 DECLARE @Before TABLE(ID int,CompanyID int,OwnerKind varbinary(40),OwnerID int,FilePath varbinary(800),FileName varbinary(520),UploadedAt datetime2(7));
 DECLARE @After TABLE(ID int,CompanyID int,OwnerKind varbinary(40),OwnerID int,FilePath varbinary(800),FileName varbinary(520),UploadedAt datetime2(7));
 IF @Attachment IS NOT NULL INSERT @Before EXEC sys.sp_executesql N'SELECT ID,CompanyID,CONVERT(varbinary(40),OwnerKind),OwnerID,CONVERT(varbinary(800),FilePath),CONVERT(varbinary(520),FileName),UploadedAt FROM dbo.HrDocumentAttachments';
 -- PREFLIGHT COMPLETE: no persistent DDL or backfill occurs before this fence.
 IF @Attachment IS NULL
  EXEC sys.sp_executesql N'CREATE TABLE dbo.HrDocumentAttachments(ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HrDocumentAttachments_R1 PRIMARY KEY,
   CompanyID int NOT NULL,OwnerKind nvarchar(20) NOT NULL,OwnerID int NOT NULL,FilePath nvarchar(400) NOT NULL,FileName nvarchar(260) NULL,UploadedAt datetime2(7) NULL);';
 IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.HrDocumentAttachments') AND name=N'IX_HrDocAtt_Owner')
  EXEC sys.sp_executesql N'CREATE INDEX IX_HrDocAtt_Owner ON dbo.HrDocumentAttachments(CompanyID,OwnerKind,OwnerID);';
 EXEC sys.sp_executesql N'INSERT dbo.HrDocumentAttachments(CompanyID,OwnerKind,OwnerID,FilePath,FileName,UploadedAt)
  SELECT c.CompanyID,N''Contract'',c.ID,c.FilePath,NULL,c.CreatedAt FROM dbo.EmploymentContracts c
  WHERE c.FilePath IS NOT NULL AND LTRIM(RTRIM(c.FilePath))<>N''''
   AND NOT EXISTS(SELECT 1 FROM dbo.HrDocumentAttachments a WHERE a.CompanyID=c.CompanyID AND a.OwnerKind=N''Contract'' AND a.OwnerID=c.ID AND a.FilePath=c.FilePath);
  INSERT dbo.HrDocumentAttachments(CompanyID,OwnerKind,OwnerID,FilePath,FileName,UploadedAt)
  SELECT d.CompanyID,N''Document'',d.ID,d.FilePath,NULL,d.CreatedAt FROM dbo.EmployeeDocuments d
  WHERE d.FilePath IS NOT NULL AND LTRIM(RTRIM(d.FilePath))<>N''''
   AND NOT EXISTS(SELECT 1 FROM dbo.HrDocumentAttachments a WHERE a.CompanyID=d.CompanyID AND a.OwnerKind=N''Document'' AND a.OwnerID=d.ID AND a.FilePath=d.FilePath);';
 INSERT @After EXEC sys.sp_executesql N'SELECT ID,CompanyID,CONVERT(varbinary(40),OwnerKind),OwnerID,CONVERT(varbinary(800),FilePath),CONVERT(varbinary(520),FileName),UploadedAt FROM dbo.HrDocumentAttachments';
 IF EXISTS(SELECT * FROM @Before EXCEPT SELECT * FROM @After)
  THROW 51214,'Existing attachment values changed; rolling back.',1;
 COMMIT TRANSACTION;
 SELECT N'COMMITTED' AS SuccessorStatus,N'R1-HR-DOCUMENT-ATTACHMENTS-003' AS SliceId;
END TRY
BEGIN CATCH
 IF XACT_STATE()<>0 ROLLBACK TRANSACTION;
 THROW;
END CATCH;
