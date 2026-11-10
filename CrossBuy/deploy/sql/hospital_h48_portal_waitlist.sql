-- ==========================================================================================
-- Hospital H48 — the patient portal's earlier-slot waitlist (owner rulings 2026-11-05/08). A patient who books online
-- may ask to be told if an earlier time with the same doctor frees up. When one does, it is offered by email to ONE
-- waiting patient at a time, in the order they joined, and held for them for OfferMinutes; they take it or decline it
-- from the emailed link (on the sign-in page, as the password-reset link), and if they do not answer it passes to the
-- next. Taking it books the earlier time and releases the later booking, which is then offered in its turn.
--   HospitalPortalSettings  + OfferMinutes (how long one offer is held; 30)
--   HospitalPortalBookings  + Waitlist     (this booking waits for an earlier time)
--   HospitalPortalOffers    one row per offer emailed; only the SHA-256 of the link's token is kept
--
-- Additive only (two columns with defaults, one new table). Idempotent (COL_LENGTH / OBJECT_ID guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HospitalPortalSettings','U') IS NULL OR OBJECT_ID('dbo.HospitalPortalBookings','U') IS NULL OR OBJECT_ID('dbo.HospitalPortalAccounts','U') IS NULL OR OBJECT_ID('dbo.HospitalAppointments','U') IS NULL OR COL_LENGTH('dbo.HospitalPortalAccounts', 'Email') IS NULL
    THROW 51990, 'Run hospital_h23_portal_rules.sql, hospital_h24_portal_accounts.sql and hospital_h46_portal_password.sql first.', 1;

IF COL_LENGTH('dbo.HospitalPortalSettings', 'OfferMinutes') IS NULL
    ALTER TABLE dbo.HospitalPortalSettings ADD OfferMinutes int NOT NULL CONSTRAINT DF_HospitalPortalSettings_OfferMinutes DEFAULT (30);
IF COL_LENGTH('dbo.HospitalPortalBookings', 'Waitlist') IS NULL
    ALTER TABLE dbo.HospitalPortalBookings ADD Waitlist bit NOT NULL CONSTRAINT DF_HospitalPortalBookings_Waitlist DEFAULT (0);

IF OBJECT_ID('dbo.HospitalPortalOffers','U') IS NULL
BEGIN
    CREATE TABLE dbo.HospitalPortalOffers (
        ID                int IDENTITY(1,1) NOT NULL,
        CompanyId         int            NOT NULL,
        BookingId         int            NOT NULL,
        AppointmentId     int            NOT NULL,
        AccountId         int            NOT NULL,
        PatientId         int            NOT NULL,
        PractitionerId    int            NOT NULL,
        OfferDate         date           NOT NULL,
        StartMinute       int            NOT NULL,
        TokenHash         nvarchar(64)   NOT NULL,
        Email             nvarchar(256)  NOT NULL,
        Status            nvarchar(12)   NOT NULL,
        SentAt            datetime2      NOT NULL,
        ExpiresAt         datetime2      NOT NULL,
        RespondedAt       datetime2      NULL,
        NewAppointmentId  int            NULL,
        PassedByEmployeeId int           NULL,
        CONSTRAINT PK_HospitalPortalOffers PRIMARY KEY (ID),
        CONSTRAINT FK_HospitalPortalOffers_Booking FOREIGN KEY (BookingId) REFERENCES dbo.HospitalPortalBookings(ID),
        CONSTRAINT FK_HospitalPortalOffers_Account FOREIGN KEY (AccountId) REFERENCES dbo.HospitalPortalAccounts(ID),
        CONSTRAINT CK_HospitalPortalOffers_Status CHECK (Status IN ('Open','Taken','Declined','Lapsed','Passed')),
        CONSTRAINT CK_HospitalPortalOffers_Life CHECK (ExpiresAt > SentAt),
        CONSTRAINT CK_HospitalPortalOffers_Minute CHECK (StartMinute >= 0 AND StartMinute < 1440),
        CONSTRAINT CK_HospitalPortalOffers_Taken CHECK ((Status = 'Taken' AND NewAppointmentId IS NOT NULL) OR (Status <> 'Taken' AND NewAppointmentId IS NULL))
    );
    CREATE UNIQUE INDEX IX_HospitalPortalOffers_TokenHash ON dbo.HospitalPortalOffers (TokenHash);
    CREATE INDEX IX_HospitalPortalOffers_CompanyId_Status ON dbo.HospitalPortalOffers (CompanyId, Status);
    CREATE INDEX IX_HospitalPortalOffers_BookingId ON dbo.HospitalPortalOffers (BookingId);
    -- one open offer per time, and one per waiting booking: a freed time goes to one patient at a time
    CREATE UNIQUE INDEX UX_HospitalPortalOffers_OpenSlot ON dbo.HospitalPortalOffers (CompanyId, PractitionerId, OfferDate, StartMinute) WHERE Status = 'Open';
    CREATE UNIQUE INDEX UX_HospitalPortalOffers_OpenBooking ON dbo.HospitalPortalOffers (BookingId) WHERE Status = 'Open';
END

COMMIT TRANSACTION;
