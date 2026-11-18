-- ==========================================================================================
-- Communication (owner, 2026-11-18): the call settings belong to the whole system, on the Communication settings page.
-- Three switches from the approved prototype that the hospital's settings never had:
--   CallsInChat - write each call (missed, finished, recorded) into the conversation between the two people;
--   PatientLink - hospital only: a call may be linked to a patient, shown before answering;
--   OnCallList  - hospital only: who is on call now, at the top of People.
-- All three on by default (what the system did before this slice).
--
-- Additive only. Idempotent. Needs hospital_h176_calls.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH('dbo.HospitalCallSettings', 'CallsInChat') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD CallsInChat bit NOT NULL CONSTRAINT DF_HospitalCallSettings_CallsInChat DEFAULT (1);
IF COL_LENGTH('dbo.HospitalCallSettings', 'PatientLink') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD PatientLink bit NOT NULL CONSTRAINT DF_HospitalCallSettings_PatientLink DEFAULT (1);
IF COL_LENGTH('dbo.HospitalCallSettings', 'OnCallList') IS NULL
    ALTER TABLE dbo.HospitalCallSettings ADD OnCallList bit NOT NULL CONSTRAINT DF_HospitalCallSettings_OnCallList DEFAULT (1);

COMMIT TRANSACTION;
