-- ==========================================================================================
-- Hospital H83 — a queue ticket never repeats in the waiting room.
--   The queue number is ONE sequence for the whole hospital's day ("A-001", "A-002", … across every clinic), handed out
--   when a patient is marked arrived — by the desk or by the self check-in kiosk. HospitalOutpatientService.MoveAsync
--   takes it under sp_getapplock (company + day), which stops two callers that both take the lock. This index is the
--   guarantee for EVERY writer (an import, a seed, a future screen): the database refuses a second patient holding the
--   same ticket on the same day, whoever writes it.
--
-- Additive only (one filtered unique index). Idempotent (sys.indexes guard). Refuses to run — and changes nothing — if
-- the table already holds a duplicate ticket, listing them, so a duplicate is cleaned deliberately, never silently.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID('dbo.HospitalAppointments') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.HospitalAppointments') AND name = 'UX_HospitalAppointments_OneTicketPerDay')
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.HospitalAppointments WHERE QueueNumber IS NOT NULL
               GROUP BY CompanyId, AppointmentDate, QueueNumber HAVING COUNT(*) > 1)
    BEGIN
        SELECT CompanyId, AppointmentDate, QueueNumber, COUNT(*) AS Holders
        FROM dbo.HospitalAppointments WHERE QueueNumber IS NOT NULL
        GROUP BY CompanyId, AppointmentDate, QueueNumber HAVING COUNT(*) > 1;
        RAISERROR('H83: duplicate queue tickets exist (listed above); clean them before adding UX_HospitalAppointments_OneTicketPerDay.', 16, 1);
        RETURN;
    END
    CREATE UNIQUE INDEX UX_HospitalAppointments_OneTicketPerDay
        ON dbo.HospitalAppointments (CompanyId, AppointmentDate, QueueNumber)
        WHERE QueueNumber IS NOT NULL;
END
