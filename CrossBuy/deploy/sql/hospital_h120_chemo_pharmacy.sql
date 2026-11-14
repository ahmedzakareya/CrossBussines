-- ==========================================================================================
-- Hospital H120 — a chemotherapy cycle's drugs go to the pharmacy. The pharmacy links each drug the regimens name to the
-- stock item it prepares it from; a cycle given opens a pharmacy request (source 'Oncology'), and the cycle keeps it.
--   HospitalChemoDrugItems            one drug's stock link (the drug folded to a key, the item, mg per unit)
--   HospitalChemoCycles.DispenseId    the pharmacy request that carries the cycle's drugs
--   HospitalDispenses.Source          may be 'Oncology'
--
-- Creates one table and adds one nullable column; changes no data. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalChemoCycles','U') IS NULL OR OBJECT_ID('dbo.HospitalDispenses','U') IS NULL OR OBJECT_ID('dbo.Items','U') IS NULL
    THROW 51990, 'Run hospital_h10_pharmacy.sql and hospital_h88_oncology.sql first.', 1;

IF OBJECT_ID('dbo.HospitalChemoDrugItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChemoDrugItems (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        DrugKey               nvarchar(100)  NOT NULL,
        ItemId                int            NOT NULL,
        MgPerUnit             decimal(18,4)  NOT NULL,
        UpdatedAt             datetime2      NOT NULL,
        UpdatedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalChemoDrugItems PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChemoDrugItems_Item FOREIGN KEY (ItemId) REFERENCES dbo.Items(ID),
        CONSTRAINT CK_HospitalChemoDrugItems_MgPerUnit CHECK (MgPerUnit > 0)
    );
    CREATE UNIQUE INDEX UX_HospitalChemoDrugItems_Drug ON dbo.HospitalChemoDrugItems (CompanyId, DrugKey);
END

IF COL_LENGTH('dbo.HospitalChemoCycles', 'DispenseId') IS NULL
    ALTER TABLE dbo.HospitalChemoCycles ADD DispenseId int NULL;

COMMIT TRANSACTION;
GO
-- the reference names the new column, so it compiles in a batch of its own
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- a pharmacy request may now come from oncology (the rule was Outpatient / Inpatient / Emergency)
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_HospitalDispenses_Source' AND definition NOT LIKE '%Oncology%')
BEGIN
    ALTER TABLE dbo.HospitalDispenses DROP CONSTRAINT CK_HospitalDispenses_Source;
    ALTER TABLE dbo.HospitalDispenses WITH CHECK ADD CONSTRAINT CK_HospitalDispenses_Source CHECK (Source IN ('Outpatient','Inpatient','Emergency','Oncology'));
END

IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_HospitalChemoCycles_Dispense')
    ALTER TABLE dbo.HospitalChemoCycles WITH CHECK ADD CONSTRAINT FK_HospitalChemoCycles_Dispense FOREIGN KEY (DispenseId) REFERENCES dbo.HospitalDispenses(ID);

COMMIT TRANSACTION;
