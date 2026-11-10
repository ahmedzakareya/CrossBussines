-- ==========================================================================================
-- Hospital H80 — chart completion (health information management). Phase 2 of the 2026-11-09 gap research.
--   HospitalChartReviews  the records department's sign-off that a discharged stay's chart is complete (one per stay)
-- The deficiencies themselves are computed from the chart and are not stored.
--
-- Additive only (one new table). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalChartReviews','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalChartReviews (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        AdmissionId            int            NOT NULL,
        PatientId              int            NOT NULL,
        ReviewedAt             datetime2      NOT NULL,
        ReviewedByEmployeeId   int            NULL,
        Note                   nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalChartReviews PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalChartReviews_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalChartReviews_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID)
    );
    CREATE UNIQUE INDEX IX_HospitalChartReviews_CompanyId_AdmissionId ON dbo.HospitalChartReviews (CompanyId, AdmissionId);
END

COMMIT TRANSACTION;
