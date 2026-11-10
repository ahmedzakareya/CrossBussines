-- ==========================================================================================
-- Hospital H60 — informed consent with an electronic signature.
--   HospitalConsentForms  the hospital's consent wording per kind, Arabic and English, versioned
--   HospitalConsents      one signed decision (given or refused): the text as read, who explained, who signed
--                         (the patient or a representative, and why), the witness, and the signature picture on
--                         the document platform with its SHA-256. Never edited or deleted: withdrawn with a reason.
--
-- Additive only (two new tables). Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalConsentForms','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConsentForms (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        Code                 nvarchar(30)   NOT NULL,
        Kind                 nvarchar(20)   NOT NULL,
        Title                nvarchar(200)  NOT NULL,
        TitleEn              nvarchar(200)  NOT NULL,
        Body                 nvarchar(max)  NOT NULL,
        BodyEn               nvarchar(max)  NOT NULL,
        Version              int            NOT NULL,
        IsActive             bit            NOT NULL,
        UpdatedAt            datetime2      NOT NULL,
        UpdatedByEmployeeId  int            NULL,
        CONSTRAINT PK_HospitalConsentForms PRIMARY KEY (ID),
        CONSTRAINT CK_HospitalConsentForms_Kind CHECK (Kind IN ('General','Procedure','Anaesthesia','Transfusion','Contrast','Photography','AgainstAdvice')),
        CONSTRAINT CK_HospitalConsentForms_Version CHECK (Version >= 1)
    );
    CREATE UNIQUE INDEX IX_HospitalConsentForms_CompanyId_Code ON dbo.HospitalConsentForms (CompanyId, Code);
END

IF OBJECT_ID('dbo.HospitalConsents','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalConsents (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        ConsentNo              nvarchar(20)   NOT NULL,
        PatientId              int            NOT NULL,
        FormId                 int            NOT NULL,
        FormVersion            int            NOT NULL,
        Kind                   nvarchar(20)   NOT NULL,
        Title                  nvarchar(200)  NOT NULL,
        Text                   nvarchar(max)  NOT NULL,
        Language               nvarchar(2)    NOT NULL,
        ProcedureName          nvarchar(300)  NULL,
        Risks                  nvarchar(1000) NULL,
        SurgeryId              int            NULL,
        AdmissionId            int            NULL,
        Decision               nvarchar(10)   NOT NULL,
        SignerKind             nvarchar(20)   NOT NULL,
        SignerName             nvarchar(200)  NOT NULL,
        SignerRelation         nvarchar(60)   NULL,
        ProxyReason            nvarchar(20)   NULL,
        Interpreter            nvarchar(200)  NULL,
        ExplainedByEmployeeId  int            NOT NULL,
        PractitionerId         int            NULL,
        WitnessEmployeeId      int            NOT NULL,
        SignedAt               datetime2      NOT NULL,
        SignatureDocumentId    bigint         NULL,
        SignatureSha256        nvarchar(64)   NULL,
        WithdrawnAt            datetime2      NULL,
        WithdrawnByEmployeeId  int            NULL,
        WithdrawReason         nvarchar(300)  NULL,
        CONSTRAINT PK_HospitalConsents PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalConsents_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalConsents_Form FOREIGN KEY (FormId) REFERENCES dbo.HospitalConsentForms(ID),
        CONSTRAINT CK_HospitalConsents_Kind CHECK (Kind IN ('General','Procedure','Anaesthesia','Transfusion','Contrast','Photography','AgainstAdvice')),
        CONSTRAINT CK_HospitalConsents_Language CHECK (Language IN ('ar','en')),
        CONSTRAINT CK_HospitalConsents_Decision CHECK (Decision IN ('Given','Refused')),
        CONSTRAINT CK_HospitalConsents_Signer CHECK (SignerKind IN ('Patient','Representative')),
        -- a representative says who they are to the patient and why they sign
        CONSTRAINT CK_HospitalConsents_Proxy CHECK ((SignerKind = 'Patient' AND ProxyReason IS NULL)
            OR (SignerKind = 'Representative' AND SignerRelation IS NOT NULL AND ProxyReason IN ('Minor','Unconscious','NoCapacity','Other'))),
        -- the witness is someone other than the clinician who explained
        CONSTRAINT CK_HospitalConsents_Witness CHECK (WitnessEmployeeId <> ExplainedByEmployeeId),
        CONSTRAINT CK_HospitalConsents_Signature CHECK ((SignatureDocumentId IS NULL AND SignatureSha256 IS NULL) OR (SignatureDocumentId IS NOT NULL AND LEN(SignatureSha256) = 64)),
        -- only a consent given can be withdrawn, and the withdrawal carries its reason
        CONSTRAINT CK_HospitalConsents_Withdraw CHECK ((WithdrawnAt IS NULL AND WithdrawReason IS NULL)
            OR (WithdrawnAt IS NOT NULL AND WithdrawReason IS NOT NULL AND Decision = 'Given'))
    );
    CREATE UNIQUE INDEX IX_HospitalConsents_CompanyId_ConsentNo ON dbo.HospitalConsents (CompanyId, ConsentNo);
    CREATE INDEX IX_HospitalConsents_CompanyId_PatientId ON dbo.HospitalConsents (CompanyId, PatientId);
    CREATE INDEX IX_HospitalConsents_CompanyId_SurgeryId ON dbo.HospitalConsents (CompanyId, SurgeryId);
END

COMMIT TRANSACTION;
