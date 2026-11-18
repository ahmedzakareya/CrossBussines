-- ==========================================================================================
-- Communication (owner, 2026-11-18; the approved prototype): a member pins a conversation to the top of their own list.
-- Muting already has its column (ConversationMembers.MutedUntil).
--
-- Additive only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.ConversationMembers', 'PinnedAt') IS NULL
    ALTER TABLE dbo.ConversationMembers ADD PinnedAt datetime2 NULL;

COMMIT TRANSACTION;
