-- ==========================================================================================
-- Hospital H21 — medication routing: an inpatient medication order (H14) carried to the bedside through six
-- stamped steps (prescribed, verified, prepared, dispatched, received on the ward, administered).
--
-- ONE NEW TABLE. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard).
--
-- STATUS: PREPARED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalMedOrders','U') IS NULL OR OBJECT_ID('dbo.HospitalMarEntries','U') IS NULL
   OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
   OR OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql, hospital_h3_admissions.sql, hospital_h6_billing.sql and hospital_h14_nursing.sql first.', 1;

IF OBJECT_ID('dbo.HospitalMedDeliveries','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMedDeliveries (
        ID                         int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalMedDeliveries PRIMARY KEY,
        CompanyId                  int            NOT NULL,
        DeliveryNo                 nvarchar(20)   NOT NULL,
        MedOrderId                 int            NOT NULL,
        AdmissionId                int            NOT NULL,
        PatientId                  int            NOT NULL,
        Priority                   nvarchar(10)   NOT NULL,
        Via                        nvarchar(10)   NOT NULL,
        Quantity                   decimal(18,3)  NOT NULL,
        Stage                      tinyint        NOT NULL,
        PrescribedAt               datetime2      NOT NULL,
        PrescribedByEmployeeId     int            NULL,
        VerifiedAt                 datetime2      NULL,
        VerifiedByEmployeeId       int            NULL,
        PreparedAt                 datetime2      NULL,
        PreparedByEmployeeId       int            NULL,
        DispatchedAt               datetime2      NULL,
        DispatchedByEmployeeId     int            NULL,
        ReceivedAt                 datetime2      NULL,
        ReceivedByEmployeeId       int            NULL,
        ReceivedWitnessEmployeeId  int            NULL,
        AdministeredAt             datetime2      NULL,
        AdministeredByEmployeeId   int            NULL,
        MarEntryId                 int            NULL,
        WarehouseId                int            NULL,
        Batches                    nvarchar(400)  NULL,
        UnitCost                   decimal(18,4)  NULL,
        BillLineId                 int            NULL,
        CancelledAt                datetime2      NULL,
        CancelledByEmployeeId      int            NULL,
        CancelReason               nvarchar(300)  NULL,
        CONSTRAINT CK_HospitalMedDeliveries_Stage CHECK (Stage <= 5),
        CONSTRAINT CK_HospitalMedDeliveries_Priority CHECK (Priority IN (N'Routine', N'Urgent', N'Stat')),
        CONSTRAINT CK_HospitalMedDeliveries_Via CHECK (Via IN (N'Tube', N'Porter', N'Cabinet')),
        CONSTRAINT CK_HospitalMedDeliveries_Quantity CHECK (Quantity >= 0),
        CONSTRAINT FK_HospitalMedDeliveries_Order FOREIGN KEY (MedOrderId) REFERENCES dbo.HospitalMedOrders(ID),
        CONSTRAINT FK_HospitalMedDeliveries_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalMedDeliveries_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalMedDeliveries_Mar FOREIGN KEY (MarEntryId) REFERENCES dbo.HospitalMarEntries(ID),
        CONSTRAINT FK_HospitalMedDeliveries_BillLine FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalMedDeliveries_CompanyId_DeliveryNo ON dbo.HospitalMedDeliveries (CompanyId, DeliveryNo);
    CREATE INDEX IX_HospitalMedDeliveries_CompanyId_Stage ON dbo.HospitalMedDeliveries (CompanyId, Stage);
    CREATE INDEX IX_HospitalMedDeliveries_MedOrderId ON dbo.HospitalMedDeliveries (MedOrderId);
END

COMMIT TRANSACTION;
