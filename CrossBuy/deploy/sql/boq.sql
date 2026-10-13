-- Projects & Contracting — P1 BOQ (Bill of Quantities). ESTIMATE/OPERATIONAL only: no GL impact.
-- Work items per project (linked to ProjectId), with client unit price + estimated cost breakdown. Idempotent.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID('dbo.BoqItems','U') IS NULL
BEGIN
    CREATE TABLE dbo.BoqItems (
        ID              int IDENTITY(1,1) NOT NULL CONSTRAINT PK_BoqItems PRIMARY KEY,
        CompanyID       int           NOT NULL,
        ProjectId       int           NOT NULL,
        ParentId        int           NULL,            -- self-ref: NULL = main item/section header; else sub-item
        SortOrder       int           NOT NULL CONSTRAINT DF_BoqItems_Sort DEFAULT(0),
        Code            nvarchar(30)  NULL,
        [Description]   nvarchar(500) NOT NULL,
        Unit            nvarchar(30)  NULL,            -- متر/طن/عدد… free text (flexible)
        Quantity        decimal(19,4) NOT NULL CONSTRAINT DF_BoqItems_Qty DEFAULT(0),
        UnitPrice       decimal(19,4) NOT NULL CONSTRAINT DF_BoqItems_Price DEFAULT(0),
        MaterialCost    decimal(19,4) NULL,
        LaborCost       decimal(19,4) NULL,
        SubcontractCost decimal(19,4) NULL,
        EquipmentCost   decimal(19,4) NULL,
        CreatedAt       datetime2     NULL
    );
    CREATE INDEX IX_BoqItems_Project ON dbo.BoqItems (CompanyID, ProjectId, SortOrder);
END
