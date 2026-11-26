-- R7 / D-26: SQL Server-managed concurrency on the five legacy construction tables.
-- Additive and rerunnable. No business values, identities, or posting records are changed.
-- Deploy before enabling the corresponding EF concurrency mappings.
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
BEGIN TRANSACTION;
DECLARE @tables TABLE (TableName sysname PRIMARY KEY);
INSERT @tables VALUES ('BoqItems'),('ProgressBillings'),('ProgressBillingLines'),('VariationOrders'),('SubcontractBillings');
DECLARE @table sysname, @object int, @statement nvarchar(max);
DECLARE rv_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT TableName FROM @tables ORDER BY TableName;
OPEN rv_cursor;
FETCH NEXT FROM rv_cursor INTO @table;
WHILE @@FETCH_STATUS = 0
BEGIN
 SET @object=OBJECT_ID(QUOTENAME('dbo')+'.'+QUOTENAME(@table),'U');
 IF @object IS NULL THROW 51020, 'Required legacy construction table is missing', 1;
 IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@object AND name='RowVersion' AND (system_type_id<>189 OR is_nullable<>0))
  THROW 51021, 'Existing RowVersion is not a nonnullable native SQL Server rowversion', 1;
 IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@object AND name='RowVersion')
 BEGIN
  IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id=@object AND system_type_id=189)
   THROW 51022, 'A differently named rowversion already exists; reconcile its mapping first', 1;
  SET @statement=N'ALTER TABLE dbo.'+QUOTENAME(@table)+N' ADD [RowVersion] rowversion NOT NULL;';
  EXEC sys.sp_executesql @statement;
 END;
 FETCH NEXT FROM rv_cursor INTO @table;
END;
CLOSE rv_cursor;
DEALLOCATE rv_cursor;
COMMIT TRANSACTION;
