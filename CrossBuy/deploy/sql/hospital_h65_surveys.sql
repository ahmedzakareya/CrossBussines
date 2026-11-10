-- ==========================================================================================
-- Hospital H65 — patient satisfaction surveys.
--   HospitalSurveyQuestions  the administrator's questions per kind of care, answered 1–5
--   HospitalSurveyResponses  one survey for ONE visit, stay or emergency visit: the recommend score (0–10), the average,
--                            a comment, anonymous or not, and the follow-up a low survey opens
--   HospitalSurveyAnswers    one score per question
--
-- Additive only (three new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalSurveyQuestions','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurveyQuestions (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        Code        nvarchar(20)   NOT NULL,
        Kind        nvarchar(10)   NOT NULL,
        Text        nvarchar(300)  NOT NULL,
        TextEn      nvarchar(300)  NOT NULL,
        SortOrder   int            NOT NULL,
        IsActive    bit            NOT NULL,
        CONSTRAINT PK_HospitalSurveyQuestions PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalSurveyQuestions_Kind CHECK (Kind IN ('Visit','Stay','Emergency'))
    );
    CREATE UNIQUE INDEX IX_HospitalSurveyQuestions_CompanyId_Code ON dbo.HospitalSurveyQuestions (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalSurveyResponses','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurveyResponses (
        ID                      int IDENTITY(1,1) NOT NULL,
        CompanyId               int            NOT NULL,
        ResponseNo              nvarchar(20)   NOT NULL,
        Kind                    nvarchar(10)   NOT NULL,
        PatientId               int            NOT NULL,
        EncounterId             int            NULL,
        AdmissionId             int            NULL,
        ErVisitId               int            NULL,
        Recommend               int            NOT NULL,
        Average                 decimal(4,2)   NOT NULL,
        Comment                 nvarchar(1000) NULL,
        Anonymous               bit            NOT NULL,
        At                      datetime2      NOT NULL,
        CollectedByEmployeeId   int            NOT NULL,
        FollowUp                nvarchar(6)    NOT NULL,
        FollowedUpAt            datetime2      NULL,
        FollowedUpByEmployeeId  int            NULL,
        FollowUpNote            nvarchar(500)  NULL,
        CONSTRAINT PK_HospitalSurveyResponses PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSurveyResponses_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalSurveyResponses_Kind CHECK (Kind IN ('Visit','Stay','Emergency')),
        -- exactly the one episode its kind names
        CONSTRAINT CK_HospitalSurveyResponses_Episode CHECK ((Kind = 'Visit' AND EncounterId IS NOT NULL AND AdmissionId IS NULL AND ErVisitId IS NULL)
            OR (Kind = 'Stay' AND AdmissionId IS NOT NULL AND EncounterId IS NULL AND ErVisitId IS NULL)
            OR (Kind = 'Emergency' AND ErVisitId IS NOT NULL AND EncounterId IS NULL AND AdmissionId IS NULL)),
        CONSTRAINT CK_HospitalSurveyResponses_Recommend CHECK (Recommend BETWEEN 0 AND 10),
        CONSTRAINT CK_HospitalSurveyResponses_Average CHECK (Average BETWEEN 1 AND 5),
        CONSTRAINT CK_HospitalSurveyResponses_FollowUp CHECK ((FollowUp IN ('None','Open') AND FollowedUpAt IS NULL)
            OR (FollowUp = 'Done' AND FollowedUpAt IS NOT NULL AND FollowedUpByEmployeeId IS NOT NULL AND FollowUpNote IS NOT NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalSurveyResponses_CompanyId_ResponseNo ON dbo.HospitalSurveyResponses (CompanyId, ResponseNo);
    CREATE INDEX IX_HospitalSurveyResponses_CompanyId_PatientId ON dbo.HospitalSurveyResponses (CompanyId, PatientId);
    CREATE INDEX IX_HospitalSurveyResponses_CompanyId_At ON dbo.HospitalSurveyResponses (CompanyId, At);
    -- one survey per visit, per stay, per emergency visit
    CREATE UNIQUE INDEX UX_HospitalSurveyResponses_Encounter ON dbo.HospitalSurveyResponses (CompanyId, EncounterId) WHERE EncounterId IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalSurveyResponses_Admission ON dbo.HospitalSurveyResponses (CompanyId, AdmissionId) WHERE AdmissionId IS NOT NULL;
    CREATE UNIQUE INDEX UX_HospitalSurveyResponses_ErVisit ON dbo.HospitalSurveyResponses (CompanyId, ErVisitId) WHERE ErVisitId IS NOT NULL;
END

IF OBJECT_ID('dbo.HospitalSurveyAnswers','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalSurveyAnswers (
        ID          int IDENTITY(1,1) NOT NULL,
        CompanyId   int            NOT NULL,
        ResponseId  int            NOT NULL,
        QuestionId  int            NOT NULL,
        Score       int            NOT NULL,
        CONSTRAINT PK_HospitalSurveyAnswers PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalSurveyAnswers_Response FOREIGN KEY (ResponseId) REFERENCES dbo.HospitalSurveyResponses(ID),
        CONSTRAINT FK_HospitalSurveyAnswers_Question FOREIGN KEY (QuestionId) REFERENCES dbo.HospitalSurveyQuestions(ID),
        CONSTRAINT CK_HospitalSurveyAnswers_Score CHECK (Score BETWEEN 1 AND 5)
    );
    CREATE UNIQUE INDEX IX_HospitalSurveyAnswers_ResponseId_QuestionId ON dbo.HospitalSurveyAnswers (ResponseId, QuestionId);
END

COMMIT TRANSACTION;
