-- ==========================================================================================
-- Tasks templates — additive English descriptions.
--
-- Adds nullable English-description columns to existing task-template tables.
-- This is intentionally separate from tasks_ecosystem_checklist_dependencies_templates.sql:
-- that original slice is already recorded in CrossBuyDev and CrossBuyCert, so its content and
-- identity must remain immutable. The original slice created the tables; this successor only
-- adds the optional English fields and does not backfill or alter existing rows.
--
-- Idempotent: each column is added only when absent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF OBJECT_ID(N'dbo.TaskTemplates', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.TaskTemplates', N'DescriptionEn') IS NULL
    ALTER TABLE dbo.TaskTemplates ADD DescriptionEn nvarchar(1000) NULL;
GO

IF OBJECT_ID(N'dbo.TaskTemplateItems', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.TaskTemplateItems', N'DescriptionEn') IS NULL
    ALTER TABLE dbo.TaskTemplateItems ADD DescriptionEn nvarchar(2000) NULL;
GO
