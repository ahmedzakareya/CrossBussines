-- CRM C9 — the assistant. Additive only: one new table, nothing existing is altered.
--   CrmLeadInsights   a lead's behavioural score and the reasons behind it, recomputed by the CRM worker. The rule
--                     score the company configured stays in Leads.Score untouched; this adds behaviour on top of it.
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.CrmLeadInsights') IS NULL
CREATE TABLE dbo.CrmLeadInsights (
    ID int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CrmLeadInsights PRIMARY KEY,
    CompanyId int NOT NULL,
    LeadId int NOT NULL,
    Score int NOT NULL CONSTRAINT CK_CrmLeadInsights_Score CHECK (Score BETWEEN 0 AND 100),
    Band varchar(5) NOT NULL CONSTRAINT CK_CrmLeadInsights_Band CHECK (Band IN ('Hot', 'Warm', 'Cold')),
    ReasonsJson nvarchar(1000) NOT NULL,
    ComputedAt datetime2 NOT NULL,
    CONSTRAINT UQ_CrmLeadInsights UNIQUE (CompanyId, LeadId)
);

COMMIT TRANSACTION;
