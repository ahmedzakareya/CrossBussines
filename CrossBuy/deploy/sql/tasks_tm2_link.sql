-- TM-2: optional polymorphic link on TaskItems (EntityType + EntityId). Additive + idempotent + nullable. No FK.
IF COL_LENGTH('TaskItems','EntityType') IS NULL ALTER TABLE TaskItems ADD EntityType NVARCHAR(40) NULL;
GO
IF COL_LENGTH('TaskItems','EntityId') IS NULL ALTER TABLE TaskItems ADD EntityId INT NULL;
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_TaskItems_Entity' AND object_id=OBJECT_ID('TaskItems'))
    CREATE INDEX IX_TaskItems_Entity ON TaskItems (CompanyId, EntityType, EntityId);
GO
