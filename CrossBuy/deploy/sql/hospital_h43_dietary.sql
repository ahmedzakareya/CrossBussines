-- ==========================================================================================
-- Hospital H43 — clinical dietary (the prototype's #/dietary, "Lunch orders — Ward 3"). A diet order is an admitted
-- patient's diet, one open at a time (a change closes the old one); a meal order is one patient's tray for one round
-- of one day, sent to the kitchen with the diet of the moment, held for a patient nil by mouth, then served.
--   HospitalDietOrders  the diet history of an admission
--   HospitalMealOrders  one row per admission, day and round
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL OR OBJECT_ID('dbo.HospitalAdmissions','U') IS NULL OR OBJECT_ID('dbo.HospitalWards','U') IS NULL
    THROW 51990, 'Run hospital_h1_facility.sql, hospital_h2_patients.sql and hospital_h3_admissions.sql first.', 1;

IF OBJECT_ID('dbo.HospitalDietOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalDietOrders (
        ID                   int IDENTITY(1,1) NOT NULL,
        CompanyId            int            NOT NULL,
        AdmissionId          int            NOT NULL,
        PatientId            int            NOT NULL,
        Diet                 nvarchar(20)   NOT NULL,
        Note                 nvarchar(300)  NULL,
        StartedAt            datetime2      NOT NULL,
        OrderedByEmployeeId  int            NULL,
        EndedAt              datetime2      NULL,
        CONSTRAINT PK_HospitalDietOrders PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalDietOrders_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalDietOrders_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalDietOrders_Diet CHECK (Diet IN (N'Regular', N'Diabetic', N'LowSalt', N'Renal', N'Soft', N'ClearFluids', N'TubeFeed', N'NPO'))
    );
    CREATE INDEX IX_HospitalDietOrders_AdmissionId_EndedAt ON dbo.HospitalDietOrders (AdmissionId, EndedAt);
    -- one open diet per admission
    CREATE UNIQUE INDEX UX_HospitalDietOrders_Open ON dbo.HospitalDietOrders (AdmissionId) WHERE EndedAt IS NULL;
END

IF OBJECT_ID('dbo.HospitalMealOrders','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalMealOrders (
        ID                int IDENTITY(1,1) NOT NULL,
        CompanyId         int            NOT NULL,
        AdmissionId       int            NOT NULL,
        PatientId         int            NOT NULL,
        WardId            int            NOT NULL,
        Day               date           NOT NULL,
        Round             nvarchar(10)   NOT NULL,
        Diet              nvarchar(20)   NOT NULL,
        Status            nvarchar(10)   NOT NULL,
        SentAt            datetime2      NOT NULL,
        SentByEmployeeId  int            NULL,
        ServedAt          datetime2      NULL,
        CONSTRAINT PK_HospitalMealOrders PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalMealOrders_Admission FOREIGN KEY (AdmissionId) REFERENCES dbo.HospitalAdmissions(ID),
        CONSTRAINT FK_HospitalMealOrders_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT FK_HospitalMealOrders_Ward FOREIGN KEY (WardId) REFERENCES dbo.HospitalWards(ID),
        CONSTRAINT CK_HospitalMealOrders_Round CHECK (Round IN (N'Breakfast', N'Lunch', N'Snack', N'Dinner')),
        CONSTRAINT CK_HospitalMealOrders_Diet CHECK (Diet IN (N'Regular', N'Diabetic', N'LowSalt', N'Renal', N'Soft', N'ClearFluids', N'TubeFeed', N'NPO')),
        CONSTRAINT CK_HospitalMealOrders_Status CHECK (Status IN (N'Sent', N'Held', N'Served')),
        -- a patient nil by mouth gets no tray
        CONSTRAINT CK_HospitalMealOrders_Npo CHECK (Diet <> N'NPO' OR Status = N'Held')
    );
    CREATE UNIQUE INDEX IX_HospitalMealOrders_AdmissionId_Day_Round ON dbo.HospitalMealOrders (AdmissionId, Day, Round);
END

COMMIT TRANSACTION;
