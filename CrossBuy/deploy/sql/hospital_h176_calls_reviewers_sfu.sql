-- ==========================================================================================
-- Hospital H176 (reviewers + media server) - the people named to review every call recording, and the hospital's
-- internal media server (LiveKit) for group calls of more than four people.
--
-- Additive only. Idempotent. Needs hospital_h176_calls.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCallSettings', 'RecordingReviewers') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD RecordingReviewers nvarchar(400) NULL;
IF COL_LENGTH('dbo.HospitalCallSettings', 'SfuUrl') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD SfuUrl nvarchar(300) NULL;
IF COL_LENGTH('dbo.HospitalCallSettings', 'SfuApiKey') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD SfuApiKey nvarchar(100) NULL;
IF COL_LENGTH('dbo.HospitalCallSettings', 'SfuApiSecret') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD SfuApiSecret nvarchar(200) NULL;

COMMIT TRANSACTION;
