-- ==========================================================================================
-- Hospital H99 — the clinical canvas (the approved prototype screens cv-*): "everything drawn on paper is on the system".
--   HospitalCanvasSheets   one drawing of one visit and kind (body map, fundus, burn chart, audiogram, joints, pedigree):
--                          its free ink as vector strokes and the kind's own state; signed with the visit
--   HospitalCanvasMarks    its coded marks: finding, standard code, region and view/eye, place, size, the lesion's number
--                          across visits, "carried from the last visit — unverified", and a photo in the patient's documents
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalEncounters','U') IS NULL
    THROW 51991, 'Run hospital_h2_patients.sql and hospital_h5_consultation.sql first.', 1;

IF OBJECT_ID('dbo.HospitalCanvasSheets','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCanvasSheets (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        PatientId            int            NOT NULL,
        EncounterId          int            NOT NULL,
        Kind                 nvarchar(10)   NOT NULL,
        Status               nvarchar(10)   NOT NULL,
        InkJson              nvarchar(max)  NOT NULL,
        StateJson            nvarchar(max)  NOT NULL,
        CreatedAt            datetime2      NOT NULL,
        CreatedByEmployeeId  int            NULL,
        UpdatedAt            datetime2      NULL,
        SignedAt             datetime2      NULL,
        SignedByEmployeeId   int            NULL,
        CONSTRAINT PK_HospitalCanvasSheets PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCanvasSheets_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalCanvasSheets_Encounter FOREIGN KEY (EncounterId) REFERENCES dbo.HospitalEncounters(ID),
        CONSTRAINT CK_HospitalCanvasSheets_Kind CHECK (Kind IN ('Body','Fundus','Burn','Audio','Joints','Pedigree')),
        CONSTRAINT CK_HospitalCanvasSheets_Status CHECK (Status IN ('Draft','Signed')),
        CONSTRAINT CK_HospitalCanvasSheets_Signed CHECK ((Status = 'Draft' AND SignedAt IS NULL) OR (Status = 'Signed' AND SignedAt IS NOT NULL)),
        CONSTRAINT CK_HospitalCanvasSheets_Json CHECK (ISJSON(InkJson) = 1 AND ISJSON(StateJson) = 1)
    );
    CREATE UNIQUE INDEX IX_HospitalCanvasSheets_EncounterId_Kind ON dbo.HospitalCanvasSheets (EncounterId, Kind);
    CREATE INDEX IX_HospitalCanvasSheets_CompanyId_PatientId_Kind ON dbo.HospitalCanvasSheets (CompanyId, PatientId, Kind);
END

IF OBJECT_ID('dbo.HospitalCanvasMarks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalCanvasMarks (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        PatientId             int            NOT NULL,
        SheetId               int            NOT NULL,
        LesionNo              int            NOT NULL,
        Finding               nvarchar(20)   NOT NULL,
        FindingCode           nvarchar(30)   NULL,
        Region                nvarchar(20)   NULL,
        Side                  nvarchar(1)    NULL,
        X                     decimal(7,2)   NOT NULL,
        Y                     decimal(7,2)   NOT NULL,
        SizeMm                decimal(6,1)   NULL,
        Carried               bit            NOT NULL,
        CarriedFromMarkId     int            NULL,
        PhotoDocumentId       bigint         NULL,
        CreatedAt             datetime2      NOT NULL,
        CreatedByEmployeeId   int            NULL,
        VerifiedAt            datetime2      NULL,
        VerifiedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalCanvasMarks PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalCanvasMarks_Sheet FOREIGN KEY (SheetId) REFERENCES dbo.HospitalCanvasSheets(ID),
        CONSTRAINT FK_HospitalCanvasMarks_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalCanvasMarks_Side CHECK (Side IS NULL OR Side IN ('F','B','R','L')),
        CONSTRAINT CK_HospitalCanvasMarks_Lesion CHECK (LesionNo > 0),
        CONSTRAINT CK_HospitalCanvasMarks_Size CHECK (SizeMm IS NULL OR (SizeMm >= 0 AND SizeMm <= 500)),
        CONSTRAINT CK_HospitalCanvasMarks_Carried CHECK (Carried = 0 OR CarriedFromMarkId IS NOT NULL)
    );
    CREATE INDEX IX_HospitalCanvasMarks_CompanyId_SheetId ON dbo.HospitalCanvasMarks (CompanyId, SheetId);
    CREATE INDEX IX_HospitalCanvasMarks_CompanyId_PatientId_LesionNo ON dbo.HospitalCanvasMarks (CompanyId, PatientId, LesionNo);
END

COMMIT TRANSACTION;
