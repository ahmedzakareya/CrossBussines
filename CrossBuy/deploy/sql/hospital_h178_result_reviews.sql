-- ==========================================================================================
-- Hospital H178 - the result follow-up inbox. A row per lab order / imaging report that was handed over or reviewed:
-- its owner now, the hand-over (who, why), and the review (who, when, what was done). A result with no row is open in
-- its ordering doctor's inbox.
--
--   HospitalResultReviews   (new)
--
-- New table only; nothing existing changes. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalLabOrders','U') IS NULL OR OBJECT_ID('dbo.HospitalRadOrders','U') IS NULL
    THROW 51982, 'Run the laboratory and radiology slices first.', 1;

IF OBJECT_ID('dbo.HospitalResultReviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalResultReviews (
        ID int IDENTITY(1,1) NOT NULL,
        CompanyId int NOT NULL,
        PatientId int NOT NULL,
        Kind nvarchar(4) NOT NULL,
        OrderId int NOT NULL,
        OwnerEmployeeId int NOT NULL,
        HandedAt datetime2 NULL,
        HandedByEmployeeId int NULL,
        HandReason nvarchar(300) NULL,
        ReviewedAt datetime2 NULL,
        ReviewedByEmployeeId int NULL,
        Action nvarchar(20) NULL,
        Note nvarchar(500) NULL,
        CONSTRAINT PK_HospitalResultReviews PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalResultReviews_Kind CHECK (Kind IN ('Lab','Rad')),
        CONSTRAINT CK_HospitalResultReviews_Action CHECK (Action IS NULL OR Action IN ('NoAction','InformedPatient','ChangedTreatment','FollowUpBooked','Repeated','Referred')),
        CONSTRAINT CK_HospitalResultReviews_Reviewed CHECK (ReviewedAt IS NULL OR Action IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalResultReviews_CompanyId_Kind_OrderId ON dbo.HospitalResultReviews (CompanyId, Kind, OrderId);
    CREATE INDEX IX_HospitalResultReviews_CompanyId_OwnerEmployeeId_ReviewedAt ON dbo.HospitalResultReviews (CompanyId, OwnerEmployeeId, ReviewedAt);
END

COMMIT TRANSACTION;
