-- ==========================================================================================
-- HR — COUNTRY RULE PACK TEMPLATES: SAUDI ARABIA, UAE, EGYPT (owner 2026-11-08: "add them, and add Egypt").
-- Same shape as the Kuwait template (hr_rule_packs.sql); a company starts from one and edits its own copy.
--
-- Figures from the laws in force on 2026-11-08, researched from law-firm and ministry summaries (not the gazette
-- text). Everything not confirmed by more than one source is marked "counsel": true — the company's legal counsel
-- confirms it before go-live:
--   SA  Labour Law (Royal Decree M/51) as amended 2025 (in force 19 Feb 2025)
--   AE  Federal Decree-Law No. 33 of 2021
--   EG  Labour Law No. 14 of 2025 (in force 1 Sep 2025) + Social Insurance Law No. 148 of 2019
--
-- Additive only (three template rows). Idempotent (NOT EXISTS per country). Requires hr_rule_packs.sql.
-- ==========================================================================================
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID('dbo.HrRulePacks','U') IS NULL
    THROW 51991, 'Run hr_rule_packs.sql first.', 1;

-- ---------------------------------------------------------------- SAUDI ARABIA
IF NOT EXISTS (SELECT 1 FROM dbo.HrRulePacks WHERE CompanyID IS NULL AND CountryCode = N'SA')
    INSERT INTO dbo.HrRulePacks (CompanyID, CountryCode, NameAr, NameEn, RulesJson, EffectiveFrom)
    VALUES (NULL, N'SA', N'السعودية — نظام العمل وتعديلات ٢٠٢٥', N'Saudi Arabia — Labour Law as amended 2025', N'{
  "currency": "SAR",
  "weekend": ["Friday", "Saturday"],
  "wageDaysPerMonth": 30,
  "hoursPerDay": 8,
  "leave": [
    { "code": "annual",    "nameAr": "سنوية",  "nameEn": "Annual",      "days": 21,  "ruleAr": "٢١ يوماً، تصبح ٣٠ يوماً بعد ٥ سنوات خدمة متصلة", "ruleEn": "21 days, rising to 30 after 5 years of continuous service", "reference": "Art. 109", "counsel": false },
    { "code": "sick",      "nameAr": "مرضية",  "nameEn": "Sick",        "days": 120, "ruleAr": "٣٠ يوماً كاملة · ٦٠ يوماً بـ٧٥٪ · ٣٠ يوماً بدون أجر في السنة", "ruleEn": "30 days full · 60 at 75% · 30 unpaid per year", "reference": "Art. 117", "counsel": false },
    { "code": "maternity", "nameAr": "وضع",    "nameEn": "Maternity",   "days": 84,  "ruleAr": "١٢ أسبوعاً مدفوعة بالكامل، منها ٦ أسابيع إلزامية بعد الولادة", "ruleEn": "12 weeks fully paid, 6 of them mandatory after birth", "reference": "Art. 151", "counsel": false },
    { "code": "paternity", "nameAr": "أبوة",   "nameEn": "Paternity",   "days": 3,   "ruleAr": "خلال ٧ أيام من الولادة", "ruleEn": "Within 7 days of the birth", "reference": "Art. 113", "counsel": false },
    { "code": "bereave",   "nameAr": "وفاة",   "nameEn": "Bereavement", "days": 5,   "ruleAr": "٥ أيام لوفاة الزوج أو أحد الأصول أو الفروع، ٣ أيام لوفاة الأخ أو الأخت", "ruleEn": "5 days for a spouse, parent or child; 3 days for a sibling", "reference": "Art. 113", "counsel": true },
    { "code": "marriage",  "nameAr": "زواج",   "nameEn": "Marriage",    "days": 5,   "ruleAr": "مدفوعة مرة واحدة", "ruleEn": "Paid, once", "reference": "Art. 113", "counsel": false },
    { "code": "hajj",      "nameAr": "حج",     "nameEn": "Hajj",        "days": 10,  "ruleAr": "١٠ إلى ١٥ يوماً مرة واحدة بعد سنتين خدمة متصلة", "ruleEn": "10 to 15 days, once, after 2 years of continuous service", "reference": "Art. 114", "counsel": true }
  ],
  "insurance": [
    { "nameAr": "المعاشات (للسعوديين)",       "nameEn": "Annuities (Saudi nationals)",    "employeePct": 9,    "employerPct": 9,    "wageCap": 45000, "counsel": true },
    { "nameAr": "ساند — التعطل عن العمل",    "nameEn": "SANED unemployment",             "employeePct": 0.75, "employerPct": 0.75, "wageCap": 45000, "counsel": false },
    { "nameAr": "الأخطار المهنية",           "nameEn": "Occupational hazards",           "employeePct": 0,    "employerPct": 2,    "wageCap": 45000, "counsel": false }
  ],
  "overtime": { "workingDayPct": 150, "restDayPct": 150, "holidayPct": 150, "dailyCapHours": 3, "yearlyCapHours": 720, "reference": "Art. 107", "counsel": true },
  "gratuity": { "firstYears": 5, "firstDaysPerYear": 15, "laterDaysPerYear": 30, "capMonths": 0, "reference": "Art. 84", "counsel": false },
  "resignation": [
    { "fromYears": 0,  "toYears": 2,    "paidPct": 0 },
    { "fromYears": 2,  "toYears": 5,    "paidPct": 33.33 },
    { "fromYears": 5,  "toYears": 10,   "paidPct": 66.67 },
    { "fromYears": 10, "toYears": null, "paidPct": 100 }
  ],
  "resignationReference": "Art. 85",
  "resignationCounsel": true,
  "loanCapPct": 10,
  "loanReference": "Art. 92"
}', '2025-02-19');

-- ---------------------------------------------------------------- UNITED ARAB EMIRATES
IF NOT EXISTS (SELECT 1 FROM dbo.HrRulePacks WHERE CompanyID IS NULL AND CountryCode = N'AE')
    INSERT INTO dbo.HrRulePacks (CompanyID, CountryCode, NameAr, NameEn, RulesJson, EffectiveFrom)
    VALUES (NULL, N'AE', N'الإمارات — المرسوم بقانون اتحادي ٣٣ لسنة ٢٠٢١', N'UAE — Federal Decree-Law No. 33 of 2021', N'{
  "currency": "AED",
  "weekend": ["Saturday", "Sunday"],
  "wageDaysPerMonth": 30,
  "hoursPerDay": 8,
  "wageBasis": "basic",
  "leave": [
    { "code": "annual",    "nameAr": "سنوية",  "nameEn": "Annual",      "days": 30, "ruleAr": "٣٠ يوماً تقويمياً بعد سنة؛ يومان عن كل شهر بين ٦ و١٢ شهراً", "ruleEn": "30 calendar days after a year; 2 days a month between 6 and 12 months", "reference": "Art. 29", "counsel": false },
    { "code": "sick",      "nameAr": "مرضية",  "nameEn": "Sick",        "days": 90, "ruleAr": "١٥ يوماً كاملة · ٣٠ بنصف الأجر · ٤٥ بدون أجر؛ لا تستحق أثناء التجربة", "ruleEn": "15 days full · 30 at half pay · 45 unpaid; not during probation", "reference": "Art. 31", "counsel": false },
    { "code": "maternity", "nameAr": "وضع",    "nameEn": "Maternity",   "days": 60, "ruleAr": "٤٥ يوماً بأجر كامل + ١٥ يوماً بنصف الأجر", "ruleEn": "45 days full pay + 15 days half pay", "reference": "Art. 30", "counsel": true },
    { "code": "parental",  "nameAr": "والدية", "nameEn": "Parental",    "days": 5,  "ruleAr": "أيام عمل خلال ٦ أشهر من الولادة", "ruleEn": "Working days within 6 months of the birth", "reference": "Art. 32", "counsel": false },
    { "code": "bereave",   "nameAr": "وفاة",   "nameEn": "Bereavement", "days": 5,  "ruleAr": "٥ أيام لوفاة الزوج، ٣ أيام لوفاة أحد الأقارب", "ruleEn": "5 days for a spouse, 3 days for a relative", "reference": "Art. 32", "counsel": false },
    { "code": "study",     "nameAr": "دراسية", "nameEn": "Study",       "days": 10, "ruleAr": "بعد سنتين خدمة، لمن يدرس في الدولة", "ruleEn": "After 2 years of service, for study in the UAE", "reference": "Art. 32", "counsel": false }
  ],
  "insurance": [
    { "nameAr": "المعاشات (للمواطنين)", "nameEn": "Pension (UAE nationals)", "employeePct": 5, "employerPct": 12.5, "wageCap": 70000, "counsel": true }
  ],
  "overtime": { "workingDayPct": 125, "restDayPct": 150, "holidayPct": 150, "dailyCapHours": 2, "yearlyCapHours": 0, "reference": "Art. 19", "counsel": true },
  "gratuity": { "firstYears": 5, "firstDaysPerYear": 21, "laterDaysPerYear": 30, "capMonths": 24, "reference": "Art. 51", "counsel": true },
  "resignation": [
    { "fromYears": 0, "toYears": 1,    "paidPct": 0 },
    { "fromYears": 1, "toYears": null, "paidPct": 100 }
  ],
  "resignationReference": "Art. 51",
  "resignationCounsel": true,
  "loanCapPct": 20,
  "loanReference": "Art. 25",
  "notes": "Gratuity is on the BASIC wage; the 125% / 150% overtime rates are applied to the basic hourly wage."
}', '2022-02-02');

-- ---------------------------------------------------------------- EGYPT
IF NOT EXISTS (SELECT 1 FROM dbo.HrRulePacks WHERE CompanyID IS NULL AND CountryCode = N'EG')
    INSERT INTO dbo.HrRulePacks (CompanyID, CountryCode, NameAr, NameEn, RulesJson, EffectiveFrom)
    VALUES (NULL, N'EG', N'مصر — قانون العمل ١٤ لسنة ٢٠٢٥', N'Egypt — Labour Law No. 14 of 2025', N'{
  "currency": "EGP",
  "weekend": ["Friday", "Saturday"],
  "wageDaysPerMonth": 30,
  "hoursPerDay": 8,
  "leave": [
    { "code": "annual",    "nameAr": "سنوية",  "nameEn": "Annual",      "days": 21,  "ruleAr": "١٥ يوم عمل في السنة الأولى، ٢١ بعدها، ٣٠ بعد ١٠ سنوات أو سن الخمسين، ٤٥ لذوي الهمم", "ruleEn": "15 working days in the first year, 21 thereafter, 30 after 10 years or age 50, 45 for people of determination", "reference": "Labour Law 14/2025", "counsel": true },
    { "code": "casual",    "nameAr": "عارضة",  "nameEn": "Casual",      "days": 7,   "ruleAr": "تُخصم من السنوية، بحد أقصى يومين متتاليين", "ruleEn": "Deducted from annual leave, at most 2 days in a row", "reference": "Labour Law 14/2025", "counsel": true },
    { "code": "sick",      "nameAr": "مرضية",  "nameEn": "Sick",        "days": 180, "ruleAr": "تُصرف من التأمينات: ٧٥٪ من الأجر ثم ٨٥٪ بعد ٩٠ يوماً", "ruleEn": "Paid by social insurance: 75% of wage, then 85% after 90 days", "reference": "SI Law 148/2019", "counsel": true },
    { "code": "maternity", "nameAr": "وضع",    "nameEn": "Maternity",   "days": 120, "ruleAr": "٤ أشهر، حتى ٣ مرات طوال الخدمة، دون شرط مدة خدمة", "ruleEn": "4 months, up to 3 times in a career, no service requirement", "reference": "Labour Law 14/2025", "counsel": false },
    { "code": "hajj",      "nameAr": "حج أو عمرة", "nameEn": "Hajj or Umrah", "days": 30, "ruleAr": "شهر مرة واحدة في الخدمة بعد ٥ سنوات", "ruleEn": "One month, once in a career, after 5 years", "reference": "Labour Law 14/2025", "counsel": true }
  ],
  "insurance": [
    { "nameAr": "التأمينات الاجتماعية", "nameEn": "Social insurance", "employeePct": 11, "employerPct": 18.75, "wageCap": 14500, "counsel": true }
  ],
  "overtime": { "workingDayPct": 135, "restDayPct": 200, "holidayPct": 200, "dailyCapHours": 2, "yearlyCapHours": 0, "reference": "Labour Law 14/2025", "counsel": true },
  "gratuity": { "firstYears": 0, "firstDaysPerYear": 0, "laterDaysPerYear": 0, "capMonths": 0, "reference": "SI Law 148/2019 — end-of-service is paid by social insurance", "counsel": true },
  "resignation": [
    { "fromYears": 0, "toYears": null, "paidPct": 100 }
  ],
  "resignationReference": "SI Law 148/2019",
  "resignationCounsel": true,
  "loanCapPct": 10,
  "loanReference": "Labour Law 14/2025"
}', '2025-09-01');

-- An earlier run of this file seeded the UAE template without its wage basis: set it, once.
UPDATE dbo.HrRulePacks SET RulesJson = JSON_MODIFY(RulesJson, '$.wageBasis', 'basic')
 WHERE CompanyID IS NULL AND CountryCode = N'AE' AND JSON_VALUE(RulesJson, '$.wageBasis') IS NULL;

COMMIT TRANSACTION;
