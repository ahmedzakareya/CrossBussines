-- ============================================================================
-- CRM 3-7b(i) — user-defined custom fields on CRM entities. Idempotent. No GL impact.
-- ============================================================================

IF OBJECT_ID('dbo.CrmCustomFields','U') IS NULL
CREATE TABLE dbo.CrmCustomFields (
    ID         int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID  int NOT NULL,
    EntityType nvarchar(20) NOT NULL,               -- Lead | Opportunity | Account | Activity
    FieldKey   nvarchar(50) NOT NULL,
    Label      nvarchar(200) NOT NULL,
    LabelEn    nvarchar(200) NULL,
    FieldType  nvarchar(15) NOT NULL CONSTRAINT DF_CrmCF_Type DEFAULT('Text'),
    Options    nvarchar(1000) NULL,
    Required   bit NOT NULL CONSTRAINT DF_CrmCF_Req DEFAULT(0),
    SortOrder  int NOT NULL CONSTRAINT DF_CrmCF_Sort DEFAULT(0),
    IsActive   bit NOT NULL CONSTRAINT DF_CrmCF_Active DEFAULT(1)
);
GO
IF OBJECT_ID('dbo.CrmCustomFieldValues','U') IS NULL
CREATE TABLE dbo.CrmCustomFieldValues (
    ID         int IDENTITY(1,1) NOT NULL PRIMARY KEY,
    CompanyID  int NOT NULL,
    FieldId    int NOT NULL,
    EntityType nvarchar(20) NOT NULL,
    EntityId   int NOT NULL,
    Value      nvarchar(2000) NULL
);
GO
IF OBJECT_ID('dbo.CrmCustomFieldValues','U') IS NOT NULL AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CrmCFV_Entity' AND object_id=OBJECT_ID('dbo.CrmCustomFieldValues'))
    CREATE INDEX IX_CrmCFV_Entity ON dbo.CrmCustomFieldValues(EntityType, EntityId);
GO
