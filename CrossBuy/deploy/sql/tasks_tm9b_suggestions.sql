-- TM-9-ب: match-suggestion table (candidates for a scheduled task with multiple matches). Idempotent.
IF OBJECT_ID('TaskMatchSuggestions','U') IS NULL
CREATE TABLE TaskMatchSuggestions (
    ID          INT IDENTITY(1,1) PRIMARY KEY,
    CompanyId   INT NOT NULL,
    TaskId      INT NOT NULL,
    EntityType  NVARCHAR(40) NOT NULL,
    EntityId    INT NOT NULL,
    Label       NVARCHAR(200) NULL,
    CreatedAt   DATETIME2 NOT NULL,
    ResolvedAt  DATETIME2 NULL
);
GO
-- one candidate per (task, movement) — the double-suggestion guard
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_TaskMatchSuggestions_TaskEntity' AND object_id = OBJECT_ID('TaskMatchSuggestions'))
    CREATE UNIQUE INDEX UX_TaskMatchSuggestions_TaskEntity ON TaskMatchSuggestions (CompanyId, TaskId, EntityType, EntityId);
GO
