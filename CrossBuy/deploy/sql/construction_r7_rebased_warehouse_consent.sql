-- Source-bound consent after a reservation rebase. This is evidence only; it never grants
-- warehouse access, purchase admission, receipt, stock movement, invoice, or GL authority.
SET XACT_ABORT ON;
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
BEGIN TRANSACTION;
IF OBJECT_ID('dbo.ConstructionReservationExposureRebases','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionReservationRebaseRequests','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionReservationRebaseDecisions','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionCompanyPolicies','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionSites','U') IS NULL
 OR OBJECT_ID('dbo.Warehouses','U') IS NULL
 OR OBJECT_ID('dbo.PurchaseOrders','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseDrafts','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseApprovalRequests','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseRebaseRequests','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseRebaseDecisions','U') IS NULL
 OR OBJECT_ID('dbo.ConstructionPurchaseAdmissions','U') IS NULL
 THROW 52040,'Rebased warehouse consent prerequisites missing.',1;

IF NOT EXISTS(SELECT 1 FROM sys.key_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionPurchaseAdmissions')
 AND name='AK_ConstructionPurchaseAdmissions_ID_PurchaseOrderId_ProjectId_CompanyID')
 ALTER TABLE dbo.ConstructionPurchaseAdmissions ADD CONSTRAINT AK_ConstructionPurchaseAdmissions_ID_PurchaseOrderId_ProjectId_CompanyID
 UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID);

IF OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionRebasedWarehouseConsentRequests(
  ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionRebasedWarehouseConsentRequests PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,ExposureRebaseId int NOT NULL,
  DestinationSiteId int NOT NULL,SourceWarehouseId int NOT NULL,SourceVersion nvarchar(64) NOT NULL,
  SourceFactsVersion nvarchar(64) NOT NULL,MappingVersion nvarchar(64) NOT NULL,PolicyToken varbinary(16) NOT NULL,
  RequestId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,Reason nvarchar(1000) NOT NULL,
  CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
  CONSTRAINT AK_ConstructionRebasedWarehouseConsentRequests_Scope UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentRequests_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentRequests_Intent UNIQUE(CompanyID,ExposureRebaseId,DestinationSiteId,SourceFactsVersion,CreatedBy),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentRequests_Rebase FOREIGN KEY(ExposureRebaseId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionReservationExposureRebases(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentRequests_Order FOREIGN KEY(PurchaseOrderId,CompanyID)
   REFERENCES dbo.PurchaseOrders(ID,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentRequests_Site FOREIGN KEY(DestinationSiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionSites(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentRequests_Warehouse FOREIGN KEY(SourceWarehouseId,CompanyID)
   REFERENCES dbo.Warehouses(ID,CompanyID),
  CONSTRAINT CK_ConstructionRebasedWarehouseConsentRequests_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
   AND DATALENGTH(PolicyToken)=16 AND LEN(SourceVersion)=64 AND LEN(SourceFactsVersion)=64 AND LEN(MappingVersion)=64
   AND LEN(RequestHash)=64 AND LEN(LTRIM(RTRIM(Reason)))>0 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
END;
IF OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionRebasedWarehouseConsentDecisions(
  ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionRebasedWarehouseConsentDecisions PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,RequestRowId int NOT NULL,Outcome nvarchar(16) NOT NULL,
  SourceFactsVersion nvarchar(64) NOT NULL,DecisionId uniqueidentifier NOT NULL,RequestHash nvarchar(64) NOT NULL,
  Reason nvarchar(1000) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
  CONSTRAINT AK_ConstructionRebasedWarehouseConsentDecisions_Scope UNIQUE(ID,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentDecisions_Key UNIQUE(CompanyID,DecisionId),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentDecisions_Request UNIQUE(CompanyID,RequestRowId),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentDecisions_Request FOREIGN KEY(RequestRowId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionRebasedWarehouseConsentRequests(ID,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionRebasedWarehouseConsentDecisions_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
   AND Outcome IN(N'Approved',N'Rejected') AND LEN(SourceFactsVersion)=64 AND LEN(RequestHash)=64
   AND LEN(LTRIM(RTRIM(Reason)))>0 AND DecisionId<>'00000000-0000-0000-0000-000000000000')
 );
END;

IF OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionRebasedWarehouseConsentBindings(
  ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionRebasedWarehouseConsentBindings PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,ExposureRebaseId int NOT NULL,
  RebaseRequestId int NOT NULL,ApprovalRequestId int NOT NULL,ConsentRequestRowId int NOT NULL,ConsentDecisionRowId int NOT NULL,
  DestinationSiteId int NOT NULL,SourceWarehouseId int NOT NULL,SourceFactsVersion nvarchar(64) NOT NULL,
  MappingVersion nvarchar(64) NOT NULL,PolicyToken varbinary(16) NOT NULL,ReviewVersion nvarchar(64) NOT NULL,
  CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
  CONSTRAINT AK_ConstructionRebasedWarehouseConsentBindings_Scope UNIQUE(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentBindings_Approval UNIQUE(ApprovalRequestId),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentBindings_Decision UNIQUE(CompanyID,ConsentDecisionRowId),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_Rebase FOREIGN KEY(RebaseRequestId,PurchaseOrderId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionPurchaseRebaseRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_Approval FOREIGN KEY(ApprovalRequestId,PurchaseOrderId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionPurchaseApprovalRequests(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_Exposure FOREIGN KEY(ExposureRebaseId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionReservationExposureRebases(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_ConsentRequest FOREIGN KEY(ConsentRequestRowId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionRebasedWarehouseConsentRequests(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_ConsentDecision FOREIGN KEY(ConsentDecisionRowId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionRebasedWarehouseConsentDecisions(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_Site FOREIGN KEY(DestinationSiteId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionSites(ID,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentBindings_Warehouse FOREIGN KEY(SourceWarehouseId,CompanyID)
   REFERENCES dbo.Warehouses(ID,CompanyID),
  CONSTRAINT CK_ConstructionRebasedWarehouseConsentBindings_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
   AND DATALENGTH(PolicyToken)=16 AND LEN(SourceFactsVersion)=64 AND LEN(MappingVersion)=64 AND LEN(ReviewVersion)=64)
 );
END;
IF OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications','U') IS NULL
BEGIN
 CREATE TABLE dbo.ConstructionRebasedWarehouseConsentApplications(
  ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_ConstructionRebasedWarehouseConsentApplications PRIMARY KEY,
  CompanyID int NOT NULL,ProjectId int NOT NULL,PurchaseOrderId int NOT NULL,BindingId int NOT NULL,AdmissionId int NOT NULL,
  RequestId uniqueidentifier NOT NULL,ReviewVersion nvarchar(64) NOT NULL,CreatedBy int NOT NULL,CreatedAt datetime2 NOT NULL,
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentApplications_Request UNIQUE(CompanyID,RequestId),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentApplications_Binding UNIQUE(BindingId),
  CONSTRAINT UQ_ConstructionRebasedWarehouseConsentApplications_Admission UNIQUE(AdmissionId),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentApplications_Binding FOREIGN KEY(BindingId,PurchaseOrderId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionRebasedWarehouseConsentBindings(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT FK_ConstructionRebasedWarehouseConsentApplications_Admission FOREIGN KEY(AdmissionId,PurchaseOrderId,ProjectId,CompanyID)
   REFERENCES dbo.ConstructionPurchaseAdmissions(ID,PurchaseOrderId,ProjectId,CompanyID),
  CONSTRAINT CK_ConstructionRebasedWarehouseConsentApplications_Values CHECK(CompanyID>0 AND ProjectId>0 AND CreatedBy>0
   AND LEN(ReviewVersion)=64 AND RequestId<>'00000000-0000-0000-0000-000000000000')
 );
END;

IF (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests'))<>16
 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions'))<>11
 OR COL_LENGTH('dbo.ConstructionRebasedWarehouseConsentRequests','PolicyToken')<>16
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions')) AND is_disabled=0 AND is_not_trusted=0)<>5
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN(OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests'),OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions')) AND is_disabled=0 AND is_not_trusted=0)<>2
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentRequests') AND is_unique=1 AND is_disabled=0)<>4
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentDecisions') AND is_unique=1 AND is_disabled=0)<>4
 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings'))<>17
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings') AND is_disabled=0 AND is_not_trusted=0)<>7
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings') AND is_disabled=0 AND is_not_trusted=0)<>1
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentBindings') AND is_unique=1 AND is_disabled=0)<>4
 OR (SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications'))<>10
 OR (SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications') AND is_disabled=0 AND is_not_trusted=0)<>2
 OR (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications') AND is_disabled=0 AND is_not_trusted=0)<>1
 OR (SELECT COUNT(*) FROM sys.indexes WHERE object_id=OBJECT_ID('dbo.ConstructionRebasedWarehouseConsentApplications') AND is_unique=1 AND is_disabled=0)<>4
 THROW 52040,'Rebased warehouse consent schema mismatch; no automatic repair.',1;
COMMIT;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentRequests_Immutable
ON dbo.ConstructionRebasedWarehouseConsentRequests INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52041,'Rebased warehouse consent requests are immutable evidence.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentDecisions_Immutable
ON dbo.ConstructionRebasedWarehouseConsentDecisions INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52041,'Rebased warehouse consent decisions are immutable evidence.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentRequests_Proof
ON dbo.ConstructionRebasedWarehouseConsentRequests AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=i.ExposureRebaseId AND e.ProjectId=i.ProjectId AND e.CompanyID=i.CompanyID
  JOIN dbo.ConstructionCommitmentReservations r ON r.ID=e.ReservationId AND r.ProjectId=e.ProjectId AND r.CompanyID=e.CompanyID
  JOIN dbo.ConstructionReservationRebaseRequests rr ON rr.ID=e.RebaseRequestId AND rr.ProjectId=e.ProjectId AND rr.CompanyID=e.CompanyID
  JOIN dbo.ConstructionBudgetVersions b ON b.ID=e.TargetBudgetVersionId AND b.ProjectId=e.ProjectId AND b.CompanyID=e.CompanyID
  JOIN dbo.PurchaseOrders o ON o.ID=i.PurchaseOrderId AND o.CompanyID=i.CompanyID
  JOIN dbo.ConstructionPurchaseDrafts d ON d.PurchaseOrderId=i.PurchaseOrderId AND d.CompanyID=i.CompanyID AND d.ProjectId=i.ProjectId
  JOIN dbo.ConstructionSites s ON s.ID=i.DestinationSiteId AND s.ProjectId=i.ProjectId AND s.CompanyID=i.CompanyID
  JOIN dbo.Warehouses w ON w.ID=i.SourceWarehouseId AND w.CompanyID=i.CompanyID
  JOIN dbo.Warehouses dw ON dw.ID=s.WarehouseId AND dw.CompanyID=i.CompanyID
  JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CrossSiteWarehouse'
  WHERE e.ReservationId<>r.ID OR e.SourceVersion<>i.SourceVersion OR r.PurchaseOrderId<>i.PurchaseOrderId
   OR rr.ReservationId<>r.ID OR rr.TargetBudgetVersionId<>e.TargetBudgetVersionId
   OR b.IsBaseline<>1 OR b.Status<>N'Approved'
   OR NOT EXISTS(SELECT 1 FROM dbo.ConstructionReservationRebaseDecisions rd WHERE rd.CompanyID=i.CompanyID
    AND rd.ProjectId=i.ProjectId AND rd.RebaseRequestId=rr.ID AND rd.Outcome=N'Approved'
    AND rd.CreatedBy<>rr.CreatedBy AND rd.SourceVersion=rr.SourceVersion)
   OR d.BudgetVersionId<>r.BudgetVersionId OR o.Status<>N'Draft' OR o.WarehouseId<>i.SourceWarehouseId
   OR s.IsActive<>1 OR s.WarehouseId IS NULL OR s.WarehouseId=i.SourceWarehouseId OR w.IsActive<>1 OR dw.IsActive<>1
   OR (SELECT COUNT(*) FROM dbo.ConstructionSites ss JOIN dbo.Projects sp ON sp.ID=ss.ProjectId AND sp.CompanyID=ss.CompanyID
       WHERE ss.CompanyID=i.CompanyID AND ss.WarehouseId=i.SourceWarehouseId AND ss.IsActive=1 AND sp.IsActive=1)<>1
   OR p.Mode<>N'ApprovedException' OR p.ConcurrencyToken<>i.PolicyToken)
  THROW 52042,'Rebased warehouse consent does not match current source, mapping, destination or company policy.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentBindings_Immutable
ON dbo.ConstructionRebasedWarehouseConsentBindings INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52041,'Rebased warehouse consent bindings are immutable evidence.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentApplications_Immutable
ON dbo.ConstructionRebasedWarehouseConsentApplications INSTEAD OF UPDATE,DELETE AS
BEGIN SET NOCOUNT ON; THROW 52041,'Rebased warehouse consent applications are immutable evidence.',1; END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentDecisions_Proof
ON dbo.ConstructionRebasedWarehouseConsentDecisions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionRebasedWarehouseConsentRequests r
  ON r.ID=i.RequestRowId AND r.ProjectId=i.ProjectId AND r.CompanyID=i.CompanyID
  JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CrossSiteWarehouse'
  WHERE i.CreatedBy=r.CreatedBy OR i.SourceFactsVersion<>r.SourceFactsVersion
   OR (i.Outcome=N'Approved' AND (p.Mode<>N'ApprovedException' OR p.ConcurrencyToken<>r.PolicyToken)))
  THROW 52043,'Rebased warehouse consent decision actor, source facts or policy mismatch.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentBindings_Proof
ON dbo.ConstructionRebasedWarehouseConsentBindings AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionPurchaseApprovalRequests a ON a.ID=i.ApprovalRequestId AND a.PurchaseOrderId=i.PurchaseOrderId AND a.ProjectId=i.ProjectId AND a.CompanyID=i.CompanyID
  JOIN dbo.ConstructionPurchaseRebaseRequests br ON br.ID=i.RebaseRequestId AND br.ApprovalRequestId=a.ID AND br.ExposureRebaseId=i.ExposureRebaseId
  JOIN dbo.ConstructionRebasedWarehouseConsentRequests r ON r.ID=i.ConsentRequestRowId AND r.ProjectId=i.ProjectId AND r.CompanyID=i.CompanyID
  JOIN dbo.ConstructionRebasedWarehouseConsentDecisions d ON d.ID=i.ConsentDecisionRowId AND d.RequestRowId=r.ID AND d.ProjectId=i.ProjectId AND d.CompanyID=i.CompanyID
  JOIN dbo.ConstructionCompanyPolicies p ON p.CompanyID=i.CompanyID AND p.PolicyKey=N'CrossSiteWarehouse'
  WHERE a.CreatedBy<>i.CreatedBy OR br.CreatedBy<>a.CreatedBy OR i.CreatedBy=d.CreatedBy OR d.Outcome<>N'Approved'
   OR r.PurchaseOrderId<>i.PurchaseOrderId OR r.ExposureRebaseId<>i.ExposureRebaseId
   OR r.DestinationSiteId<>i.DestinationSiteId OR r.SourceWarehouseId<>i.SourceWarehouseId
   OR r.SourceFactsVersion<>i.SourceFactsVersion OR r.MappingVersion<>i.MappingVersion OR r.PolicyToken<>i.PolicyToken
   OR d.SourceFactsVersion<>r.SourceFactsVersion OR p.Mode<>N'ApprovedException' OR p.ConcurrencyToken<>i.PolicyToken
   OR a.ExceptionId IS NOT NULL)
  THROW 52044,'Consent binding must connect the exact independently approved destination to its canonical pending rebased request.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionRebasedWarehouseConsentApplications_Proof
ON dbo.ConstructionRebasedWarehouseConsentApplications AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionRebasedWarehouseConsentBindings b ON b.ID=i.BindingId AND b.PurchaseOrderId=i.PurchaseOrderId AND b.ProjectId=i.ProjectId AND b.CompanyID=i.CompanyID
  JOIN dbo.ConstructionPurchaseAdmissions a ON a.ID=i.AdmissionId AND a.PurchaseOrderId=i.PurchaseOrderId AND a.ProjectId=i.ProjectId AND a.CompanyID=i.CompanyID
  LEFT JOIN dbo.ConstructionPurchaseRebaseRequests br ON br.ID=b.RebaseRequestId AND br.PurchaseOrderId=i.PurchaseOrderId AND br.ProjectId=i.ProjectId AND br.CompanyID=i.CompanyID
  LEFT JOIN dbo.ConstructionPurchaseRebaseDecisions d ON d.RebaseRequestId=br.ID AND d.PurchaseOrderId=i.PurchaseOrderId AND d.ProjectId=i.ProjectId AND d.CompanyID=i.CompanyID
  WHERE a.ApprovalRequestId<>b.ApprovalRequestId OR a.RequestId<>i.RequestId OR a.CreatedBy<>i.CreatedBy
   OR br.ID IS NULL OR d.ID IS NULL OR a.RequestHash<>d.RequestHash OR a.RequestId<>d.RequestId OR d.RequestVersion<>br.ReviewVersion
   OR d.CreatedBy<>i.CreatedBy OR i.CreatedBy=b.CreatedBy OR i.ReviewVersion<>b.ReviewVersion)
  THROW 52045,'Consent application must consume its exact independent rebased admission.',1;
END;
GO
CREATE OR ALTER TRIGGER dbo.TR_ConstructionPurchaseRebaseRequests_AdmissionGate
ON dbo.ConstructionPurchaseAdmissions AFTER INSERT AS
BEGIN
 SET NOCOUNT ON;
 IF EXISTS(SELECT 1 FROM inserted i JOIN dbo.ConstructionPurchaseRebaseRequests br
  ON br.ApprovalRequestId=i.ApprovalRequestId AND br.CompanyID=i.CompanyID AND br.ProjectId=i.ProjectId
  WHERE NOT EXISTS(SELECT 1 FROM dbo.ConstructionPurchaseRebaseDecisions d
   JOIN dbo.ConstructionReservationExposureRebases e ON e.ID=br.ExposureRebaseId
   JOIN dbo.ConstructionBudgetVersions b ON b.ID=d.TargetBudgetVersionId
   WHERE d.RebaseRequestId=br.ID AND d.CompanyID=i.CompanyID AND d.ProjectId=i.ProjectId AND d.PurchaseOrderId=i.PurchaseOrderId
    AND d.ReservationId=i.ReservationId AND d.CreatedBy=i.CreatedBy AND d.RequestId=i.RequestId AND d.RequestHash=i.RequestHash
    AND d.RequestVersion=br.ReviewVersion AND d.SourceVersion=e.SourceVersion AND d.ReservationId=e.ReservationId
    AND d.TargetBudgetVersionId=e.TargetBudgetVersionId AND b.Status=N'Approved' AND b.IsBaseline=1
    AND DATALENGTH(d.Reason)=DATALENGTH(i.Reason) AND d.Reason COLLATE Latin1_General_100_BIN2=i.Reason COLLATE Latin1_General_100_BIN2))
  THROW 51932,'Rebased admission requires its matching fresh independently reviewed decision.',1;
 IF EXISTS(SELECT 1 FROM inserted i
  JOIN dbo.ConstructionPurchaseRebaseRequests br ON br.ApprovalRequestId=i.ApprovalRequestId AND br.CompanyID=i.CompanyID AND br.ProjectId=i.ProjectId
  JOIN dbo.ConstructionPurchaseApprovalRequests ar ON ar.ID=i.ApprovalRequestId
  JOIN dbo.PurchaseOrders po ON po.ID=i.PurchaseOrderId AND po.CompanyID=i.CompanyID
  WHERE (EXISTS(SELECT 1 FROM dbo.ConstructionWarehouseExceptionRequests wr WHERE wr.CompanyID=i.CompanyID AND wr.ProjectId=i.ProjectId AND wr.PurchaseOrderId=i.PurchaseOrderId)
    OR EXISTS(SELECT 1 FROM dbo.ConstructionSites ws JOIN dbo.Projects wp ON wp.ID=ws.ProjectId AND wp.CompanyID=ws.CompanyID
      WHERE ws.CompanyID=i.CompanyID AND ws.WarehouseId=po.WarehouseId AND ws.ProjectId<>i.ProjectId AND ws.IsActive=1 AND wp.IsActive=1))
   AND NOT EXISTS(SELECT 1 FROM dbo.ConstructionRebasedWarehouseConsentBindings cb
    JOIN dbo.ConstructionRebasedWarehouseConsentRequests cr ON cr.ID=cb.ConsentRequestRowId
    JOIN dbo.ConstructionRebasedWarehouseConsentDecisions cd ON cd.ID=cb.ConsentDecisionRowId AND cd.RequestRowId=cr.ID
    JOIN dbo.ConstructionPurchaseRebaseDecisions pd ON pd.PurchaseOrderId=i.PurchaseOrderId AND pd.CompanyID=i.CompanyID AND pd.ProjectId=i.ProjectId
    WHERE cb.ApprovalRequestId=i.ApprovalRequestId AND cb.RebaseRequestId=br.ID AND cb.CompanyID=i.CompanyID AND cb.ProjectId=i.ProjectId
     AND cb.PurchaseOrderId=i.PurchaseOrderId AND cb.ExposureRebaseId=br.ExposureRebaseId AND cb.CreatedBy=ar.CreatedBy
     AND cd.Outcome=N'Approved' AND cd.CreatedBy NOT IN(ar.CreatedBy,i.CreatedBy,pd.CreatedBy)
     AND cr.SourceFactsVersion=cb.SourceFactsVersion AND cr.MappingVersion=cb.MappingVersion
     AND cr.DestinationSiteId=cb.DestinationSiteId AND cr.SourceWarehouseId=cb.SourceWarehouseId
     AND cr.PolicyToken=cb.PolicyToken AND pd.CreatedBy=i.CreatedBy AND pd.ReservationId=i.ReservationId
     AND pd.RequestId=i.RequestId AND pd.RequestHash=i.RequestHash))
  THROW 52046,'Cross-site rebased purchase admission requires its exact approved, independently bound warehouse consent.',1;
END;
GO
