-- ==========================================================================================
-- Hospital H41 — the blood bank (the prototype's #/bloodbank): the units on the shelf per blood group and the
-- transfusion requests with their cross-match. A unit is one bag (number, group, product, expiry) — available,
-- reserved for a request, issued or discarded; a request is the doctor's (patient, product, units), cross-matched
-- by the laboratory, its units reserved when compatible and then issued.
--   HospitalTransfusionRequests  one row per request
--   HospitalBloodUnits           one row per bag
--
-- New tables only; nothing existing changes. Idempotent (OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPatients','U') IS NULL
    THROW 51990, 'Run hospital_h2_patients.sql first.', 1;

IF OBJECT_ID('dbo.HospitalTransfusionRequests','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalTransfusionRequests (
        ID                     int IDENTITY(1,1) NOT NULL,
        CompanyId              int            NOT NULL,
        PatientId              int            NOT NULL,
        Product                nvarchar(20)   NOT NULL,
        Units                  int            NOT NULL,
        Status                 nvarchar(20)   NOT NULL,
        Note                   nvarchar(500)  NULL,
        Reason                 nvarchar(300)  NULL,
        RequestedAt            datetime2      NOT NULL,
        RequestedByEmployeeId  int            NULL,
        MatchedAt              datetime2      NULL,
        MatchedByEmployeeId    int            NULL,
        IssuedAt               datetime2      NULL,
        IssuedByEmployeeId     int            NULL,
        CONSTRAINT PK_HospitalTransfusionRequests PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalTransfusionRequests_Patient FOREIGN KEY (PatientId) REFERENCES dbo.HospitalPatients(ID),
        CONSTRAINT CK_HospitalTransfusionRequests_Product CHECK (Product IN (N'WholeBlood', N'PackedCells', N'Plasma', N'Platelets')),
        CONSTRAINT CK_HospitalTransfusionRequests_Status CHECK (Status IN (N'Requested', N'CrossMatching', N'Compatible', N'Incompatible', N'Issued', N'Cancelled')),
        CONSTRAINT CK_HospitalTransfusionRequests_Units CHECK (Units BETWEEN 1 AND 10)
    );
    CREATE INDEX IX_HospitalTransfusionRequests_CompanyId_Status ON dbo.HospitalTransfusionRequests (CompanyId, Status);
END

IF OBJECT_ID('dbo.HospitalBloodUnits','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalBloodUnits (
        ID                    int IDENTITY(1,1) NOT NULL,
        CompanyId             int            NOT NULL,
        UnitNo                nvarchar(40)   NOT NULL,
        BloodGroup            nvarchar(3)    NOT NULL,
        Product               nvarchar(20)   NOT NULL,
        ExpiresOn             date           NOT NULL,
        Status                nvarchar(20)   NOT NULL,
        RequestId             int            NULL,
        Note                  nvarchar(300)  NULL,
        ReceivedAt            datetime2      NOT NULL,
        ReceivedByEmployeeId  int            NULL,
        IssuedAt              datetime2      NULL,
        DiscardedAt           datetime2      NULL,
        CONSTRAINT PK_HospitalBloodUnits PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalBloodUnits_Request FOREIGN KEY (RequestId) REFERENCES dbo.HospitalTransfusionRequests(ID),
        CONSTRAINT CK_HospitalBloodUnits_Group CHECK (BloodGroup IN (N'O+', N'O-', N'A+', N'A-', N'B+', N'B-', N'AB+', N'AB-')),
        CONSTRAINT CK_HospitalBloodUnits_Product CHECK (Product IN (N'WholeBlood', N'PackedCells', N'Plasma', N'Platelets')),
        CONSTRAINT CK_HospitalBloodUnits_Status CHECK (Status IN (N'Available', N'Reserved', N'Issued', N'Discarded')),
        -- a reserved or issued bag always names its request
        CONSTRAINT CK_HospitalBloodUnits_Request CHECK (Status NOT IN (N'Reserved', N'Issued') OR RequestId IS NOT NULL)
    );
    CREATE UNIQUE INDEX IX_HospitalBloodUnits_CompanyId_UnitNo ON dbo.HospitalBloodUnits (CompanyId, UnitNo);
    CREATE INDEX IX_HospitalBloodUnits_Shelf ON dbo.HospitalBloodUnits (CompanyId, Status, BloodGroup, Product);
END

COMMIT TRANSACTION;
