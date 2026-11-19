-- Communication — call summaries (owner, 2026-11-19: "since it is free, do it"). Idempotent.
--   CallSummaries : the words and the summary of a RECORDED call (recording already needs both sides' consent),
--                   made once, in the background, on this machine (Whisper + the local model), and shown to whoever
--                   may open that recording. One row per recording.
IF OBJECT_ID('dbo.CallSummaries') IS NULL
CREATE TABLE dbo.CallSummaries (
    RecordingId int NOT NULL CONSTRAINT PK_CallSummaries PRIMARY KEY,
    CompanyId int NOT NULL,
    CallId int NOT NULL,
    Status nvarchar(16) NOT NULL,                 -- working | done | failed
    JobId nvarchar(32) NULL,                      -- the AI service's job while it works
    Progress decimal(5,3) NULL,
    Transcript nvarchar(max) NULL,
    SummaryJson nvarchar(max) NULL,               -- {overview, points[], decisions[], actions[{owner, task}]}
    Language nvarchar(8) NULL,
    Model nvarchar(120) NULL,
    AudioSeconds decimal(9,1) NULL,
    WorkSeconds decimal(9,1) NULL,
    Error nvarchar(200) NULL,
    RequestedByEmployeeId int NOT NULL,
    CreatedAt datetime2 NOT NULL,
    UpdatedAt datetime2 NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_CallSummaries_Call' AND object_id = OBJECT_ID('dbo.CallSummaries'))
CREATE INDEX IX_CallSummaries_Call ON dbo.CallSummaries (CompanyId, CallId);
