-- ==========================================================================================
-- Communication (owner, 2026-11-18; the approved prototype #/comm-group): a group call started from a group conversation
-- belongs to it — the conversation shows "a group call is on now · Join", the list marks it live, and its details list
-- the group's calls. NULL = a call started elsewhere (every call before this slice).
--
-- Additive only. Idempotent. Needs hospital_h176_calls.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCalls', 'ConversationId') IS NULL
    ALTER TABLE dbo.HospitalCalls ADD ConversationId int NULL;

COMMIT TRANSACTION;
GO

-- a separate batch: the index names the column the batch above adds
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_HospitalCalls_Conversation' AND object_id = OBJECT_ID('dbo.HospitalCalls'))
    CREATE INDEX IX_HospitalCalls_Conversation ON dbo.HospitalCalls (CompanyId, ConversationId) WHERE ConversationId IS NOT NULL;
