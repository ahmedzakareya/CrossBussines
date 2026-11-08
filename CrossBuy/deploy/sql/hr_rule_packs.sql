-- ==========================================================================================
-- HR — COUNTRY RULE PACKS (owner ruling 2026-11-08: "the system may run in more than one country with more than one
-- system"; every legal figure is a setting, never code).
--
--   HrRulePacks   one row per COUNTRY TEMPLATE (CompanyID NULL) and one per COMPANY (its own editable copy, cloned
--                 from a template the first time the company's HR opens its rules). The rules are one JSON document
--                 (RulesJson), so Saudi Arabia and the UAE are added as DATA in the same shape, never as code.
--
-- Seeds the KUWAIT template (Law 6/2010 figures from the HR prototype). Values marked "counsel": true are defaults
-- that the company's legal counsel confirms before go-live.
--
-- Additive only (one new table + one template row). Idempotent (OBJECT_ID / NOT EXISTS guards).
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HrRulePacks','U') IS NULL
BEGIN
    CREATE TABLE dbo.HrRulePacks (
        ID              int IDENTITY(1,1) NOT NULL,
        CompanyID       int            NULL,          -- NULL = a country template; otherwise the company's own copy
        CountryCode     nvarchar(2)    NOT NULL,      -- ISO 3166-1 alpha-2: KW, SA, AE …
        NameAr          nvarchar(200)  NOT NULL,
        NameEn          nvarchar(200)  NOT NULL,
        RulesJson       nvarchar(max)  NOT NULL,
        Version         int            NOT NULL CONSTRAINT DF_HrRulePacks_Version DEFAULT (1),
        EffectiveFrom   date           NULL,
        UpdatedAt       datetime2      NOT NULL CONSTRAINT DF_HrRulePacks_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedByEmployeeId int        NULL,
        CONSTRAINT PK_HrRulePacks PRIMARY KEY (ID),
        CONSTRAINT CK_HrRulePacks_Json CHECK (ISJSON(RulesJson) = 1)
    );
END;

-- one template per country, one pack per company
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_HrRulePacks_Template' AND object_id = OBJECT_ID('dbo.HrRulePacks'))
    CREATE UNIQUE INDEX UX_HrRulePacks_Template ON dbo.HrRulePacks (CountryCode) WHERE CompanyID IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_HrRulePacks_Company' AND object_id = OBJECT_ID('dbo.HrRulePacks'))
    CREATE UNIQUE INDEX UX_HrRulePacks_Company ON dbo.HrRulePacks (CompanyID) WHERE CompanyID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM dbo.HrRulePacks WHERE CompanyID IS NULL AND CountryCode = N'KW')
    INSERT INTO dbo.HrRulePacks (CompanyID, CountryCode, NameAr, NameEn, RulesJson, EffectiveFrom)
    VALUES (NULL, N'KW', N'الكويت — قانون العمل في القطاع الأهلي ٦/٢٠١٠', N'Kuwait — Private Sector Labour Law 6/2010', N'{
  "currency": "KWD",
  "weekend": ["Friday", "Saturday"],
  "wageDaysPerMonth": 26,
  "leave": [
    { "code": "annual",    "nameAr": "سنوية",  "nameEn": "Annual",      "days": 30,  "ruleAr": "بعد ٦ أشهر خدمة، تُستثنى العطل الرسمية وأيام الراحة؛ تتراكم ٢٫٥ يوم شهرياً", "ruleEn": "After 6 months of service; excludes official holidays and rest days; accrues 2.5 days a month", "reference": "Art. 70", "counsel": true },
    { "code": "sick",      "nameAr": "مرضية",  "nameEn": "Sick",        "days": 75,  "ruleAr": "١٥ يوماً كاملة · ١٠ بـ٧٥٪ · ١٠ بـ٥٠٪ · ١٠ بـ٢٥٪ · ٣٠ بدون أجر", "ruleEn": "15 full · 10 at 75% · 10 at 50% · 10 at 25% · 30 unpaid", "reference": "Art. 69", "counsel": false },
    { "code": "maternity", "nameAr": "وضع",    "nameEn": "Maternity",   "days": 70,  "ruleAr": "٧٠ يوماً مدفوعة + حتى ٤ أشهر بدون أجر", "ruleEn": "70 days paid + up to 4 months unpaid", "reference": "Art. 24", "counsel": false },
    { "code": "hajj",      "nameAr": "حج",     "nameEn": "Hajj",        "days": 21,  "ruleAr": "مرة واحدة في الخدمة، للمسلم بعد سنتين خدمة متصلة", "ruleEn": "Once per career, Muslim, after 2 years of continuous service", "reference": "Art. 76", "counsel": true },
    { "code": "bereave",   "nameAr": "وفاة",   "nameEn": "Bereavement", "days": 3,   "ruleAr": "لوفاة قريب حتى الدرجة الثانية", "ruleEn": "Death of a relative up to the second degree", "reference": "Art. 77", "counsel": false },
    { "code": "iddah",     "nameAr": "عدة",    "nameEn": "Iddah",       "days": 130, "ruleAr": "٤ أشهر و١٠ أيام للمسلمة، ٢١ يوماً لغير المسلمة", "ruleEn": "4 months 10 days (Muslim), 21 days (non-Muslim)", "reference": "Art. 77", "counsel": false }
  ],
  "insurance": [
    { "nameAr": "التأمين الأساسي",  "nameEn": "Basic insurance",         "employeePct": 5,   "employerPct": 10,  "wageCap": 1500, "counsel": false },
    { "nameAr": "التأمين التكميلي", "nameEn": "Supplementary insurance", "employeePct": 5,   "employerPct": 10,  "wageCap": 1250, "counsel": true },
    { "nameAr": "زيادة المعاش",     "nameEn": "Pension increase",        "employeePct": 2.5, "employerPct": 1,   "wageCap": 2750, "counsel": false },
    { "nameAr": "تأمين ضد البطالة", "nameEn": "Unemployment",            "employeePct": 0.5, "employerPct": 0.5, "wageCap": 2750, "counsel": false },
    { "nameAr": "المكافأة المالية", "nameEn": "Financial remuneration",  "employeePct": 2.5, "employerPct": 0,   "wageCap": 1500, "counsel": false }
  ],
  "overtime": { "workingDayPct": 125, "restDayPct": 150, "holidayPct": 200, "dailyCapHours": 2, "yearlyCapHours": 180, "reference": "Art. 66-68", "counsel": false },
  "gratuity": { "firstYears": 5, "firstDaysPerYear": 15, "laterDaysPerYear": 30, "capMonths": 18, "reference": "Art. 51", "counsel": true },
  "resignation": [
    { "fromYears": 0,  "toYears": 3,    "paidPct": 0 },
    { "fromYears": 3,  "toYears": 5,    "paidPct": 50 },
    { "fromYears": 5,  "toYears": 10,   "paidPct": 66.67 },
    { "fromYears": 10, "toYears": null, "paidPct": 100 }
  ],
  "resignationReference": "Art. 53",
  "resignationCounsel": true,
  "loanCapPct": 10,
  "loanReference": "Art. 59"
}', '2010-02-21');

COMMIT TRANSACTION;
