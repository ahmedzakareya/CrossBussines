-- ==========================================================================================
-- Hospital H272 - the eye clinic's own tests are procedures of the clinic, not radiology.
-- OCT and the visual-field test were filed as Radiology with no exam set up: the eye visit's "Order OCT + visual
-- fields" sent them to the radiology queue, and "EYE-OCT" was guessed as a CT scan (the code holds "CT"). They are done
-- in the eye clinic and billed on the visit, like the ECG and the echo (Procedure).
--
--   HospitalServices.Category  'Radiology' -> 'Procedure'  for the eye clinic's OCT / VF services
--
-- Data only. Idempotent: rows already Procedure, and rows with a radiology exam set up, are left as they are.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

UPDATE s SET s.Category = 'Procedure'
FROM dbo.HospitalServices s
WHERE s.Category = 'Radiology'
  AND s.Code IN ('EYE-OCT', 'EYE-VF', 'OCT', 'VF')
  AND NOT EXISTS (SELECT 1 FROM dbo.HospitalRadExams x WHERE x.CompanyId = s.CompanyId AND x.ServiceId = s.ID);

PRINT CONCAT('eye tests moved to Procedure: ', @@ROWCOUNT);

COMMIT TRANSACTION;
