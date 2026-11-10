-- ==========================================================================================
-- Hospital H54 — the follow-up of a root-cause analysis runs as REAL TASKS on the platform's task module (the
-- prototype's "reuse: follow-up tasks"): quality management opens a task per agreed action, assigned and dated, from
-- the incident; the incident lists its tasks with their live status. Only the link is stored here — the task, its
-- status, assignee and due date are the task module's.
--   HospitalIncidentTasks  one row per task opened from an incident
--
-- Additive only (one new table). Idempotent (OBJECT_ID guard).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalIncidents','U') IS NULL OR OBJECT_ID('dbo.TaskItems','U') IS NULL
    THROW 51990, 'Run hospital_h45_incidents.sql (and the task module) first.', 1;

IF OBJECT_ID('dbo.HospitalIncidentTasks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalIncidentTasks (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        IncidentId  int            NOT NULL,
        TaskId      int            NOT NULL,
        CreatedAt   datetime2      NOT NULL,
        CreatedByEmployeeId int    NULL,
        CONSTRAINT PK_HospitalIncidentTasks PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalIncidentTasks_Incident FOREIGN KEY (IncidentId) REFERENCES dbo.HospitalIncidents(ID),
        CONSTRAINT FK_HospitalIncidentTasks_Task FOREIGN KEY (TaskId) REFERENCES dbo.TaskItems(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalIncidentTasks_TaskId ON dbo.HospitalIncidentTasks (TaskId);
    CREATE INDEX IX_HospitalIncidentTasks_IncidentId ON dbo.HospitalIncidentTasks (IncidentId);
END

COMMIT TRANSACTION;
