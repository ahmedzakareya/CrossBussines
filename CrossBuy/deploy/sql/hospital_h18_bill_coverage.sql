-- ==========================================================================================
-- Hospital H18 — a bill's insurance split, frozen when the bill closes. The bill (what the cashier asks for,
-- what closing posts) and the claim (what the payer is asked for) read the same split; once a bill closes,
-- its split is stored here so the claim asks for exactly what was posted.
--
-- TWO NEW TABLES. Nothing existing is altered. Idempotent by construction (one IF OBJECT_ID guard per table).
--
-- STATUS: PREPARED, NOT APPLIED. Registration (manifest / known-debt) goes through TAB-1 and the generator.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalBills','U') IS NULL OR OBJECT_ID('dbo.HospitalBillLines','U') IS NULL
   OR OBJECT_ID('dbo.HospitalPayers','U') IS NULL OR OBJECT_ID('dbo.HospitalPatientPolicies','U') IS NULL
    THROW 51990, 'Run hospital_h6_billing.sql and hospital_h11_insurance.sql first.', 1;

IF OBJECT_ID('dbo.HospitalBillCoverages','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBillCoverages (
        ID            int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBillCoverages PRIMARY KEY,
        CompanyId     int            NOT NULL,
        BillId        int            NOT NULL,
        PayerId       int            NOT NULL,
        PolicyId      int            NOT NULL,
        CopayPct      decimal(5,2)   NOT NULL,
        CopayCap      decimal(18,3)  NOT NULL,
        DiscountPct   decimal(5,2)   NOT NULL,
        Amount        decimal(18,3)  NOT NULL,
        Covered       decimal(18,3)  NOT NULL,
        Copay         decimal(18,3)  NOT NULL,
        InsurerShare  decimal(18,3)  NOT NULL,
        PatientShare  decimal(18,3)  NOT NULL,
        FrozenAt      datetime2      NOT NULL,
        -- the two shares make the bill's amount, and the payer never owes more than what is covered
        CONSTRAINT CK_HospitalBillCoverages_Sum CHECK (InsurerShare + PatientShare = Amount AND InsurerShare >= 0 AND InsurerShare <= Covered AND Covered <= Amount),
        CONSTRAINT FK_HospitalBillCoverages_Bill FOREIGN KEY (BillId) REFERENCES dbo.HospitalBills(ID),
        CONSTRAINT FK_HospitalBillCoverages_Payer FOREIGN KEY (PayerId) REFERENCES dbo.HospitalPayers(ID),
        CONSTRAINT FK_HospitalBillCoverages_Policy FOREIGN KEY (PolicyId) REFERENCES dbo.HospitalPatientPolicies(ID)
    );
    -- a bill is split once
    CREATE UNIQUE INDEX IX_HospitalBillCoverages_BillId ON dbo.HospitalBillCoverages (BillId);
END

IF OBJECT_ID('dbo.HospitalBillCoverageLines','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBillCoverageLines (
        ID             int IDENTITY(1,1) NOT NULL CONSTRAINT PK_HospitalBillCoverageLines PRIMARY KEY,
        CompanyId      int            NOT NULL,
        CoverageId     int            NOT NULL,
        BillLineId     int            NOT NULL,
        Amount         decimal(18,3)  NOT NULL,
        Covered        bit            NOT NULL,
        InsurerAmount  decimal(18,3)  NOT NULL,
        CONSTRAINT CK_HospitalBillCoverageLines_Insurer CHECK (InsurerAmount >= 0 AND (Covered = 1 OR InsurerAmount = 0)),
        CONSTRAINT FK_HospitalBillCoverageLines_Coverage FOREIGN KEY (CoverageId) REFERENCES dbo.HospitalBillCoverages(ID),
        CONSTRAINT FK_HospitalBillCoverageLines_Line FOREIGN KEY (BillLineId) REFERENCES dbo.HospitalBillLines(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalBillCoverageLines_BillLineId ON dbo.HospitalBillCoverageLines (BillLineId);
    CREATE INDEX IX_HospitalBillCoverageLines_CoverageId ON dbo.HospitalBillCoverageLines (CoverageId);
END

COMMIT TRANSACTION;
