-- ==========================================================================================
-- Hospital H36 — who asks and who gives, per supply unit (owner ruling, 2026-11-05: "that is a SETTING that can
-- change", not fixed roles in code). One row = one person's duty on one unit:
--   Request  — opens, fills, submits and cancels the unit's requisitions
--   Approve  — approves quantities, picks, dispatches (posts the transfer) and cancels a sent request
-- A unit with nobody named for a duty keeps the hospital's role for it (admit / pharmacy), so nothing stops before
-- the setting is made; naming people makes them the duty's holders. A hospital administrator (`manage`) always may.
--
-- NEW TABLE ONLY — nothing existing changes. Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalSupplyUnits','U') IS NULL
    THROW 51990, 'Run hospital_h32_supply.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSupplyUnitPeople','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSupplyUnitPeople (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int           NOT NULL,
        UnitId      int           NOT NULL,
        EmployeeId  int           NOT NULL,
        Duty        nvarchar(10)  NOT NULL,
        CreatedAt   datetime2     NOT NULL,
        CONSTRAINT PK_HospitalSupplyUnitPeople PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSupplyUnitPeople_Unit FOREIGN KEY (UnitId) REFERENCES dbo.HospitalSupplyUnits(ID),
        CONSTRAINT CK_HospitalSupplyUnitPeople_Duty CHECK (Duty IN (N'Request', N'Approve'))
    );
    CREATE UNIQUE INDEX IX_HospitalSupplyUnitPeople_Unit_Employee_Duty ON dbo.HospitalSupplyUnitPeople (CompanyId, UnitId, EmployeeId, Duty);
END

COMMIT TRANSACTION;
