-- R7 / D-24. Preserve stable base identities and retain C1 satellites as migration history.
-- Requires the C1 foundation and native-rowversion scripts first. Deploy before the R7 EF model.
-- Null SnapshotCapturedAt remains NOT PROVEN; this script never fabricates historical snapshots.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.BoqLineStates','U') IS NULL OR OBJECT_ID('dbo.CertificateLineSnapshots','U') IS NULL
 THROW 51030, 'C1 schema is required before consolidation', 1;
IF OBJECT_ID('dbo.ConstructionSchemaTransitions','U') IS NULL
 CREATE TABLE dbo.ConstructionSchemaTransitions (
  TransitionCode nvarchar(80) NOT NULL CONSTRAINT PK_ConstructionSchemaTransitions PRIMARY KEY,
  CompletedAt datetime2 NOT NULL,
  BoqStatesCopied int NOT NULL,
  CertificateSnapshotsCopied int NOT NULL
 );
DECLARE @columns TABLE (TableName sysname, ColumnName sysname, Definition nvarchar(300));
INSERT @columns VALUES
 ('BoqItems','Status','nvarchar(20) NOT NULL CONSTRAINT DF_BoqItems_CommercialStatus DEFAULT(''Active'')'),
 ('BoqItems','ClientContractId','int NULL'),
 ('BoqItems','CurrentRevisionId','int NULL'),
 ('BoqItems','RetiredAt','datetime2 NULL'),
 ('BoqItems','RetiredBy','int NULL'),
 ('BoqItems','RetiredReason','nvarchar(500) NULL'),
 ('BoqItems','CreatedBy','int NULL'),
 ('BoqItems','UpdatedAt','datetime2 NULL'),
 ('BoqItems','UpdatedBy','int NULL'),
 ('BoqItems','ConcurrencyToken','varbinary(16) NOT NULL CONSTRAINT DF_BoqItems_CommercialToken DEFAULT(CONVERT(varbinary(16),NEWID()))'),
 ('ProgressBillingLines','SnapshotClientContractId','int NULL'),
 ('ProgressBillingLines','SnapshotCommercialRevisionId','int NULL'),
 ('ProgressBillingLines','SnapshotVariationOrderId','int NULL'),
 ('ProgressBillingLines','SnapshotContractedQuantity','decimal(19,4) NULL'),
 ('ProgressBillingLines','SnapshotContractedRate','decimal(19,4) NULL'),
 ('ProgressBillingLines','SnapshotCertifiedQuantity','decimal(19,4) NULL'),
 ('ProgressBillingLines','SnapshotCertifiedValue','decimal(19,4) NULL'),
 ('ProgressBillingLines','SnapshotCapturedAt','datetime2 NULL'),
 ('ProgressBillingLines','SnapshotCapturedBy','int NULL');
DECLARE @table sysname,@column sysname,@definition nvarchar(300),@statement nvarchar(max);
DECLARE consolidation_columns CURSOR LOCAL FAST_FORWARD FOR SELECT TableName,ColumnName,Definition FROM @columns;
OPEN consolidation_columns;
FETCH NEXT FROM consolidation_columns INTO @table,@column,@definition;
WHILE @@FETCH_STATUS=0
BEGIN
 IF COL_LENGTH('dbo.'+@table,@column) IS NULL
 BEGIN
  SET @statement=N'ALTER TABLE dbo.'+QUOTENAME(@table)+N' ADD '+QUOTENAME(@column)+N' '+@definition;
  EXEC sys.sp_executesql @statement;
 END;
 FETCH NEXT FROM consolidation_columns INTO @table,@column,@definition;
END;
CLOSE consolidation_columns;
DEALLOCATE consolidation_columns;
GO
IF NOT EXISTS(SELECT 1 FROM dbo.ConstructionSchemaTransitions WHERE TransitionCode='R7-D24-COMMERCIAL-STATE-V1')
BEGIN
 IF EXISTS(SELECT 1 FROM dbo.BoqLineStates s LEFT JOIN dbo.BoqItems b ON b.ID=s.BoqItemId
           WHERE b.ID IS NULL OR b.CompanyID<>s.CompanyID OR b.ProjectId<>s.ProjectId)
  THROW 51031, 'BOQ history ownership mismatch', 1;
 IF EXISTS(SELECT 1 FROM dbo.CertificateLineSnapshots s
           LEFT JOIN dbo.ProgressBillingLines l ON l.ID=s.ProgressBillingLineId
           LEFT JOIN dbo.ProgressBillings h ON h.ID=l.BillingId
           WHERE l.ID IS NULL OR h.ID IS NULL OR h.CompanyID<>s.CompanyID OR h.ProjectId<>s.ProjectId
           OR h.ID<>s.ProgressBillingId OR ISNULL(l.BoqItemId,-1)<>ISNULL(s.BoqItemId,-1))
  THROW 51032, 'Certificate snapshot ownership mismatch', 1;
 UPDATE b SET Status=s.Status,ClientContractId=s.ClientContractId,CurrentRevisionId=s.CurrentRevisionId,
  RetiredAt=s.RetiredAt,RetiredBy=s.RetiredBy,RetiredReason=s.RetiredReason,
  CreatedBy=s.CreatedBy,UpdatedAt=s.UpdatedAt,UpdatedBy=s.UpdatedBy,ConcurrencyToken=s.ConcurrencyToken
 FROM dbo.BoqItems b JOIN dbo.BoqLineStates s ON s.BoqItemId=b.ID;
 DECLARE @boqCount int=@@ROWCOUNT;
 UPDATE l SET SnapshotClientContractId=s.ClientContractId,SnapshotCommercialRevisionId=s.CommercialRevisionId,
  SnapshotVariationOrderId=s.VariationOrderId,SnapshotContractedQuantity=s.ContractedQuantity,
  SnapshotContractedRate=s.ContractedRate,SnapshotCertifiedQuantity=s.CertifiedQuantity,
  SnapshotCertifiedValue=s.CertifiedValue,SnapshotCapturedAt=s.CapturedAt,SnapshotCapturedBy=s.CapturedBy
 FROM dbo.ProgressBillingLines l JOIN dbo.CertificateLineSnapshots s ON s.ProgressBillingLineId=l.ID;
 DECLARE @snapshotCount int=@@ROWCOUNT;
 INSERT dbo.ConstructionSchemaTransitions VALUES('R7-D24-COMMERCIAL-STATE-V1',SYSUTCDATETIME(),@boqCount,@snapshotCount);
END;
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.BoqItems') AND name='CK_BoqItems_CommercialStatus')
 ALTER TABLE dbo.BoqItems WITH CHECK ADD CONSTRAINT CK_BoqItems_CommercialStatus CHECK(Status IN('Active','Retired'));
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.BoqItems') AND name='CK_BoqItems_CommercialRetirement')
 ALTER TABLE dbo.BoqItems WITH CHECK ADD CONSTRAINT CK_BoqItems_CommercialRetirement CHECK(Status='Active' OR (RetiredAt IS NOT NULL AND RetiredReason IS NOT NULL));
IF NOT EXISTS(SELECT 1 FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ProgressBillingLines') AND name='CK_ProgressBillingLines_SnapshotCompleteness')
 ALTER TABLE dbo.ProgressBillingLines WITH CHECK ADD CONSTRAINT CK_ProgressBillingLines_SnapshotCompleteness CHECK(
  SnapshotCapturedAt IS NULL OR (SnapshotContractedQuantity IS NOT NULL AND SnapshotContractedRate IS NOT NULL
  AND SnapshotCertifiedQuantity IS NOT NULL AND SnapshotCertifiedValue IS NOT NULL));

GO
CREATE OR ALTER TRIGGER dbo.TR_ProgressBillingLines_ImmutableSnapshot ON dbo.ProgressBillingLines
AFTER UPDATE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS (
  SELECT d.ID,d.BillingId,d.BoqItemId,d.SnapshotClientContractId,d.SnapshotCommercialRevisionId,
   d.SnapshotVariationOrderId,d.SnapshotContractedQuantity,d.SnapshotContractedRate,
   d.SnapshotCertifiedQuantity,d.SnapshotCertifiedValue,d.SnapshotCapturedAt,d.SnapshotCapturedBy
  FROM deleted d WHERE d.SnapshotCapturedAt IS NOT NULL
  EXCEPT
  SELECT i.ID,i.BillingId,i.BoqItemId,i.SnapshotClientContractId,i.SnapshotCommercialRevisionId,
   i.SnapshotVariationOrderId,i.SnapshotContractedQuantity,i.SnapshotContractedRate,
   i.SnapshotCertifiedQuantity,i.SnapshotCertifiedValue,i.SnapshotCapturedAt,i.SnapshotCapturedBy
  FROM inserted i
 ) THROW 51033, 'A captured commercial snapshot is immutable', 1;
END;

GO
CREATE OR ALTER TRIGGER dbo.TR_BoqLineStates_MigrationHistory ON dbo.BoqLineStates
AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted) OR EXISTS(SELECT 1 FROM deleted)
  THROW 51034, 'BOQ satellite is migration history; write current state on BoqItems', 1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_CertificateLineSnapshots_MigrationHistory ON dbo.CertificateLineSnapshots
AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted) OR EXISTS(SELECT 1 FROM deleted)
  THROW 51035, 'Certificate satellite is migration history; capture on ProgressBillingLines', 1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ProgressBillingLines_PreserveCapturedSnapshot ON dbo.ProgressBillingLines
AFTER DELETE AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM deleted WHERE SnapshotCapturedAt IS NOT NULL)
  THROW 51036, 'A captured commercial snapshot cannot be deleted', 1;
END;

GO
COMMIT TRANSACTION;
