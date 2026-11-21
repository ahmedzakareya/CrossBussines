-- ==========================================================================================
-- Hospital H222 - pathology: the structured cancer report (a synoptic: tumour type, grade, size, margins, lymphovascular
-- invasion, pT, pN, lymph nodes) recorded with every malignant diagnosis.
--
--   HospitalPathCases   + TumourType, Grade, TumourSizeMm, Margins, MarginMm, Lvi, PT, PN, NodesExamined, NodesPositive
--
-- Columns only. Idempotent.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPathCases','U') IS NULL
    THROW 52022, 'Run the pathology slice (hospital_h216_pathology.sql) first.', 1;

IF COL_LENGTH('dbo.HospitalPathCases', 'TumourType') IS NULL ALTER TABLE dbo.HospitalPathCases ADD TumourType nvarchar(200) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'Grade') IS NULL ALTER TABLE dbo.HospitalPathCases ADD Grade nvarchar(20) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'TumourSizeMm') IS NULL ALTER TABLE dbo.HospitalPathCases ADD TumourSizeMm decimal(6,1) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'Margins') IS NULL ALTER TABLE dbo.HospitalPathCases ADD Margins nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'MarginMm') IS NULL ALTER TABLE dbo.HospitalPathCases ADD MarginMm decimal(5,1) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'Lvi') IS NULL ALTER TABLE dbo.HospitalPathCases ADD Lvi nvarchar(15) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'PT') IS NULL ALTER TABLE dbo.HospitalPathCases ADD PT nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'PN') IS NULL ALTER TABLE dbo.HospitalPathCases ADD PN nvarchar(10) NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'NodesExamined') IS NULL ALTER TABLE dbo.HospitalPathCases ADD NodesExamined int NULL;
IF COL_LENGTH('dbo.HospitalPathCases', 'NodesPositive') IS NULL ALTER TABLE dbo.HospitalPathCases ADD NodesPositive int NULL;

COMMIT TRANSACTION;
