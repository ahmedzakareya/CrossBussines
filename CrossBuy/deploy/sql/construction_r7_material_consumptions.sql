-- Immutable analytical receipt -> issue provenance. No stock/GL posting or budget release.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionPurchaseAdmissions','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionActualCostAllocations','U') IS NULL
 OR OBJECT_ID('dbo.PurchaseOrderInvoiceLineLinks','U') IS NULL
 THROW 51900,'Apply approved purchase, actual cost and invoice provenance first.',1;
IF OBJECT_ID('dbo.ConstructionMaterialConsumptions','U') IS NULL
 CREATE TABLE dbo.ConstructionMaterialConsumptions(
  ID int IDENTITY NOT NULL CONSTRAINT PK_ConstructionMaterialConsumptions PRIMARY KEY,
  CompanyID int NOT NULL, ProjectId int NOT NULL, PurchaseOrderId int NOT NULL, PurchaseOrderLineId int NOT NULL,
  GoodsReceiptId int NOT NULL, GoodsReceiptLineId int NOT NULL, ReceiptMovementId int NOT NULL, IssueMovementId int NOT NULL,
  IssueJournalEntryId int NOT NULL, ExpenseJournalLineId int NOT NULL, ActualAllocationId int NOT NULL, ReservationId int NOT NULL,
  FunctionalCurrencyId int NOT NULL, FunctionalCurrencyDecimals int NOT NULL,
  QuantityBase decimal(28,8) NOT NULL, NetBaseCost decimal(28,8) NOT NULL,
  SourceVersion nvarchar(64) NOT NULL, RequestId uniqueidentifier NOT NULL, RequestHash nvarchar(64) NOT NULL,
  Reason nvarchar(1000) NOT NULL, CreatedBy int NOT NULL, CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionMaterialConsumptions_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionMaterialConsumptions_Issue UNIQUE(CompanyID,IssueMovementId),
  CONSTRAINT UQ_ConstructionMaterialConsumptions_Expense UNIQUE(CompanyID,ExpenseJournalLineId),
  CONSTRAINT CK_ConstructionMaterialConsumptions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
   AND QuantityBase>0 AND NetBaseCost>0 AND FunctionalCurrencyDecimals BETWEEN 0 AND 8
   AND NetBaseCost=ROUND(NetBaseCost,FunctionalCurrencyDecimals)
   AND LEN(SourceVersion)=64 AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0
   AND RequestId<>'00000000-0000-0000-0000-000000000000'),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Order FOREIGN KEY(PurchaseOrderId,CompanyID) REFERENCES dbo.PurchaseOrders(ID,CompanyID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_OrderLine FOREIGN KEY(PurchaseOrderLineId,PurchaseOrderId) REFERENCES dbo.PurchaseOrderLines(ID,PurchaseOrderId),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Receipt FOREIGN KEY(GoodsReceiptId) REFERENCES dbo.GoodsReceipts(ID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_ReceiptLine FOREIGN KEY(GoodsReceiptLineId) REFERENCES dbo.GoodsReceiptLines(ID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Incoming FOREIGN KEY(ReceiptMovementId) REFERENCES dbo.StockMovements(ID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Issue FOREIGN KEY(IssueMovementId) REFERENCES dbo.StockMovements(ID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Journal FOREIGN KEY(IssueJournalEntryId,CompanyID) REFERENCES dbo.JournalEntries(ID,CompanyID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Expense FOREIGN KEY(ExpenseJournalLineId,IssueJournalEntryId) REFERENCES dbo.JournalEntryLines(ID,JournalEntryId),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Actual FOREIGN KEY(ActualAllocationId,ProjectId,CompanyID) REFERENCES dbo.ConstructionActualCostAllocations(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Reservation FOREIGN KEY(ReservationId,ProjectId,CompanyID) REFERENCES dbo.ConstructionCommitmentReservations(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionMaterialConsumptions_Currency FOREIGN KEY(FunctionalCurrencyId) REFERENCES dbo.Currencies(ID)
 );
IF NOT EXISTS(SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionMaterialConsumptions') AND name='IX_ConstructionMaterialConsumptions_Receipt')
 CREATE INDEX IX_ConstructionMaterialConsumptions_Receipt ON dbo.ConstructionMaterialConsumptions(CompanyID,GoodsReceiptLineId);
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionMaterialConsumptions_Insert ON dbo.ConstructionMaterialConsumptions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 -- Serialize consumption of a shared receipt even for writers outside the application project lock.
 DECLARE @locked bigint;
 SELECT @locked=COUNT_BIG(*) FROM dbo.GoodsReceiptLines r WITH(UPDLOCK,HOLDLOCK)
 WHERE EXISTS(SELECT 1 FROM inserted i WHERE i.GoodsReceiptLineId=r.ID);
 IF EXISTS(SELECT 1 FROM inserted i WHERE NOT EXISTS(
  SELECT 1 FROM dbo.PurchaseOrders p
  JOIN dbo.PurchaseOrderLines pl ON pl.ID=i.PurchaseOrderLineId AND pl.PurchaseOrderId=p.ID
  JOIN dbo.GoodsReceipts g ON g.ID=i.GoodsReceiptId AND g.PurchaseOrderId=p.ID AND g.CompanyID=i.CompanyID
  JOIN dbo.GoodsReceiptLines l ON l.ID=i.GoodsReceiptLineId AND l.GoodsReceiptId=g.ID AND l.PurchaseOrderLineId=pl.ID
  JOIN dbo.StockMovements r ON r.ID=i.ReceiptMovementId AND l.StockMovementId=r.ID AND r.CompanyID=i.CompanyID
  JOIN dbo.JournalEntries re ON re.ID=r.JournalEntryId AND re.CompanyID=i.CompanyID
  JOIN dbo.StockMovements m ON m.ID=i.IssueMovementId AND m.CompanyID=i.CompanyID
  JOIN dbo.JournalEntries e ON e.ID=i.IssueJournalEntryId AND m.JournalEntryId=e.ID AND e.CompanyID=i.CompanyID
  JOIN dbo.JournalEntryLines j ON j.ID=i.ExpenseJournalLineId AND j.JournalEntryId=e.ID
  JOIN dbo.Accounts a ON a.ID=j.AccountId AND a.CompanyID=i.CompanyID
  JOIN dbo.AccountTypes t ON t.ID=a.AccountTypeId
  JOIN dbo.ConstructionActualCostAllocations c ON c.ID=i.ActualAllocationId AND c.CompanyID=i.CompanyID AND c.ProjectId=i.ProjectId
  JOIN dbo.ConstructionCommitmentReservations v ON v.ID=i.ReservationId AND v.CompanyID=i.CompanyID AND v.ProjectId=i.ProjectId AND v.PurchaseOrderId=p.ID
  JOIN dbo.ConstructionPurchaseAdmissions d ON d.ReservationId=v.ID AND d.PurchaseOrderId=p.ID AND d.ProjectId=i.ProjectId AND d.CompanyID=i.CompanyID
  LEFT JOIN dbo.PurchaseOrderInvoiceLinks k ON k.CompanyID=i.CompanyID AND k.PurchaseOrderId=p.ID AND k.PurchaseInvoiceId=g.InvoiceId
  LEFT JOIN dbo.PurchaseInvoices invoice ON invoice.ID=k.PurchaseInvoiceId AND invoice.CompanyID=i.CompanyID
  WHERE p.ID=i.PurchaseOrderId AND p.CompanyID=i.CompanyID AND p.ProjectId=i.ProjectId
   AND p.CurrencyId=i.FunctionalCurrencyId AND p.ExchangeRate=1 AND g.Status=N'Posted'
   AND ((p.Status=N'Closed' AND invoice.Status=N'Posted' AND invoice.ProjectId=i.ProjectId AND invoice.VendorId=p.VendorId
         AND invoice.CurrencyId=i.FunctionalCurrencyId AND invoice.ExchangeRate=1)
        OR (p.Status IN(N'Approved',N'Received') AND g.InvoiceId IS NULL
            AND NOT EXISTS(SELECT 1 FROM dbo.PurchaseOrderInvoiceLinks existing WHERE existing.CompanyID=i.CompanyID AND existing.PurchaseOrderId=p.ID)))
   AND g.VendorId=p.VendorId AND g.CurrencyId=i.FunctionalCurrencyId AND g.ExchangeRate=1
   AND r.Direction=1 AND r.SourceType=N'Receipt' AND r.SourceId=g.ID AND r.ItemId=pl.ItemId AND r.WarehouseId=g.WarehouseId
   AND re.Status=N'Posted' AND re.SourceType=N'Inventory' AND re.SourceId=r.ID
   AND re.FunctionalCurrencyId=i.FunctionalCurrencyId AND re.FunctionalCurrencyDecimals=i.FunctionalCurrencyDecimals
   AND m.Direction=-1 AND m.SourceType=N'Issue' AND m.ItemId=r.ItemId AND m.WarehouseId=r.WarehouseId
   AND e.Status=N'Posted' AND e.SourceType=N'Inventory' AND e.SourceId=m.ID
   AND e.FunctionalCurrencyId=i.FunctionalCurrencyId AND e.FunctionalCurrencyDecimals=i.FunctionalCurrencyDecimals
   AND j.ProjectId=i.ProjectId AND j.Debit=i.NetBaseCost AND j.Credit=0 AND t.Code=N'EXP' AND t.StatementType=N'IncomeStatement'
   AND c.IsSealed=1 AND c.JournalEntryId=e.ID AND c.JournalEntryLineId=j.ID AND c.NetBaseAmount=i.NetBaseCost
   AND c.FunctionalCurrencyId=i.FunctionalCurrencyId AND c.FunctionalCurrencyDecimals=i.FunctionalCurrencyDecimals
   AND i.QuantityBase=m.QtyBase AND i.NetBaseCost=m.TotalCost AND r.QtyBase>0 AND r.TotalCost>0
   AND m.TotalCost*r.QtyBase=r.TotalCost*m.QtyBase
   AND NOT EXISTS(SELECT 1 FROM dbo.ConstructionCommitmentReleases x WHERE x.ReservationId=v.ID AND x.CompanyID=i.CompanyID)
 )) THROW 51901,'Material consumption requires owned posted receipt/issue/expense, admitted purchase and sealed actual attribution.',1;
 IF EXISTS(SELECT 1 FROM dbo.ConstructionMaterialConsumptions c WITH(HOLDLOCK)
  JOIN dbo.StockMovements r ON r.ID=c.ReceiptMovementId
  WHERE EXISTS(SELECT 1 FROM inserted i WHERE i.GoodsReceiptLineId=c.GoodsReceiptLineId AND i.CompanyID=c.CompanyID)
  GROUP BY c.CompanyID,c.GoodsReceiptLineId,r.QtyBase,r.TotalCost
  HAVING SUM(c.QuantityBase)>r.QtyBase OR SUM(c.NetBaseCost)>r.TotalCost)
  THROW 51902,'Material consumption exceeds the receipt base quantity or value.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionMaterialConsumptions_Immutable ON dbo.ConstructionMaterialConsumptions AFTER UPDATE,DELETE AS
BEGIN
 SET NOCOUNT ON;
 THROW 51903,'Material consumption provenance is immutable; corrections require an explicit future reversal workflow.',1;
END;
GO
COMMIT TRANSACTION;
