# Hospital — third benchmark and the deep-development plan (2026-11-22)

Owner request (2026-11-22): a deeper development and a comprehensive review, comparing the hospital module with
the strongest systems. The owner approved this plan ("وثق الخطة وابدأ") and its order.

**Benchmarked against:**
- Systems: Epic, Oracle Health (Cerner), InterSystems TrakCare / IntelliCare, MEDITECH Expanse.
- Standards: HIMSS EMRAM 2022 (stages 6–7) and JCI 8th edition.
- Gulf requirements: Kuwait MOH e-services, Saudi NPHIES, UAE Malaffi / NABIDH / eClaimLink / Shafafiya.

**Starting point:**
- 229 units (H1–H229) and 1,316 hospital tests.
- Every domain is present. Depth is uneven.

## Scores (leader = 10; the Gulf mid-size minimum bar is about 6)

| # | Domain | Score | Main gaps against the leaders |
|---|--------|-------|-------------------------------|
| 1 | Registration, front desk, scheduling, queue | 8 | National-ID lookup; no-show prediction; room/equipment resources |
| 2 | Outpatient documentation | 7.5 | Ambient AI notes; full SNOMED/LOINC coding |
| 3 | CPOE & decision support | 5.5 | Licensed drug knowledge base; duplicate therapy; pregnancy/lactation; weight dosing for all ages; alert-override analytics; antimicrobial stewardship |
| 4 | Inpatient & nursing | 7.5 | Acuity staffing; smart-pump integration; capacity prediction |
| 5 | Pharmacy | 8 | Dispensing-cabinet integration; TPN; diversion analytics |
| 6 | Laboratory | 5 | **Microbiology (culture, susceptibility, cascade, antibiogram); QC (Westgard); autoverification and delta checks; age/sex reference ranges** |
| 7 | Radiology | 5.5 | Structured reporting; follow-up recommendation tracking; peer review; dose |
| 8 | Surgery, anaesthesia, CSSD | 6.5 | Preference cards; block utilisation; operative note |
| 9 | Emergency | 4.5 | Sepsis / stroke / STEMI / trauma pathways with bundle timers; ED documentation and order sets; ambulance pre-arrival |
| 10 | ICU | 4 | GCS, APACHE II; infusion titration; ventilator / line bundles |
| 11 | Maternity & neonatal | 6 | NICU; CTG interface |
| 12 | Specialty clinics | 8 | Ophthalmic device imaging link |
| 13 | Revenue cycle | 7 | DRG grouping; denial work-queue with root cause; live eligibility / pre-auth (NPHIES-style FHIR) |
| 14 | Portal & engagement | 5.5 | Secure messaging; online payment; questionnaires; proxy access; patient AI assistant |
| 15 | Interoperability | 5.5 | Outbound ADT/ORU to national exchanges; general FHIR clinical API |
| 16 | Analytics, quality, AI | 4.5 | Data warehouse; locally validated predictive models with drift monitoring; registries |
| 17 | Security, privacy, downtime | 7 | Behavioural privacy monitoring; read-only downtime copy; audit viewer |
| 18 | Supply, assets, staff | 5.5 | Biomedical preventive maintenance; acuity staffing; licence-expiry alerts |

Overall score: about 6.3 out of 10.

## The plan, in order

### Phase 1 — minimum-bar safety and accreditation gaps
1. **Laboratory**
   - Microbiology:
     - culture workflow, organism dictionary and isolates;
     - susceptibility with S/I/R and cascade reporting;
     - MDRO flags into infection control;
     - cumulative antibiogram on the first isolate per patient (CLSI M39).
   - QC with Westgard rules and Levey-Jennings charts.
   - Autoverification rules and delta checks.
   - Reference ranges by age and sex.
2. **Medication safety**
   - Duplicate therapy.
   - Pregnancy and lactation checks.
   - Weight-based dosing for every age.
   - Antimicrobial stewardship:
     - approval for restricted agents;
     - a 48–72 h time-out;
     - DOT/DDD usage reporting.
   - Alert-override analytics.
3. **Emergency**
   - Pathways with time-stamped bundles: sepsis, stroke, STEMI and trauma.
   - ED documentation and order sets.
   - Ambulance pre-arrival.
4. **ICU**
   - GCS and APACHE II.
   - Continuous infusion titration.
   - Ventilator and line bundles.

### Phase 2 — operations and revenue
- Capacity centre: predicted discharges and bed demand.
- Denial work-queue with root cause.
- DRG grouping.
- FHIR eligibility and pre-authorisation.
- Radiology:
  - structured reports;
  - follow-up tracking;
  - dose.
- Theatre preference cards and block utilisation.
- Biomedical preventive maintenance.
- Acuity staffing.

### Phase 3 — interoperability and the patient
- Outbound ADT/ORU feeds (Malaffi-style minimum dataset).
- General FHIR clinical API.
- Portal:
  - messaging;
  - payment;
  - questionnaires;
  - proxy access.
- NICU.

### Phase 4 — differentiators
- Arabic ambient documentation. No leader has shown it in production for Gulf Arabic.
- Locally validated predictive models with drift monitoring:
  - deterioration;
  - sepsis;
  - discharge.
- Behavioural privacy monitoring.
- Read-only downtime copy.

## Verification gaps (not confirmed from primary sources)
- The EMRAM 2022 criteria text and its thresholds.
- The NPHIES implementation guide version and its authentication model.
- Kuwait's MOH interfaces for e-sick-leave and notifiable diseases.

## Progress
- Units continue from H230.
- Each unit is committed as usual. Each unit's line is added here when it lands.
- **H230 (5a339ac0)** — microbiology:
  - cultures, isolates, S/I/R with cascade reporting;
  - MRSA/VRE/ESBL/CRE/CRAB/CRPA flags;
  - a positive blood culture is critical;
  - a verified MDRO opens a suspected infection-control case.
  - Verified live on TEST patient 25 (LAB-000017 → IC-000004).
- **H231 (f6d0da3f)** — the cumulative antibiogram (first isolate per patient per organism), on screen and in Report Studio.
- **H232 (967c6d6e)** — lab quality control: Levey-Jennings charts and Westgard rules. An out-of-control analyte blocks patient results.
- **H233–H242 (one commit)**:
  - H233 — lab: age/sex reference ranges, the delta check, and autoverification.
  - H234 — medication safety: duplicate therapy and pregnancy/lactation checks.
  - H235 — antimicrobial stewardship:
    - approval of restricted agents;
    - the 48–72 h time-out;
    - days of therapy.
  - H236 — alert-override analytics.
  - H237 — ED time-critical pathways and ambulance pre-arrival:
    - pathways for sepsis, stroke, STEMI and trauma;
    - every step timed from the door.
  - H238 — ICU:
    - GCS and APACHE II;
    - infusions titrated within a protocol;
    - ventilator, central-line and catheter bundles with device-days.
  - H239 — the capacity centre:
    - beds now and a four-day forecast;
    - ward occupancy;
    - discharge plans with their barriers.
  - H240 — the denial work-queue: root cause, owner, appeal deadline, and the overturn rate.
  - H241 — DRG coding:
    - the group table is entered by the administrator;
    - the grouper;
    - a frozen weight per stay and the case-mix index.
  - H242 — FHIR eligibility and pre-authorisation:
    - each payer's gateway is entered by the administrator and stays off until then;
    - the file exchange keeps working until the gateway is on.
- **H243–H245 (af0a3f77)**:
  - H243 — radiology:
    - structured categories (BI-RADS, LI-RADS, Lung-RADS, TI-RADS, PI-RADS);
    - follow-ups tracked to the answering study;
    - dose with effective-dose estimate, reference-level flag and 12-month cumulative dose.
  - H244 — theatre: block time and utilisation; preference cards.
  - H245 — biomedical engineering over the fixed assets:
    - device register;
    - out of service / back in service;
    - safety tests and recalls;
    - preventive maintenance through the asset module.
- **H246–H248 (b9ab59cc)**:
  - H246 — acuity-based staffing.
  - H247 — outbound HL7 ADT/ORU feeds (off until enabled).
  - H248 — FHIR R4 clinical read API behind a ClinicalRead key.
- **H249–H251 (d9181286)** — portal:
  - secure messaging;
  - questionnaires (PHQ-9, GAD-7, AUDIT-C);
  - proxy scope and end date;
  - online bill payment link (entered by the administrator; empty = the cashier).
- **H252–H256**:
  - H252 — NICU:
    - day of life and postmenstrual age;
    - growth velocity and feeds per kg.
  - H253 — LACE readmission model, validated on local discharges: AUC, calibration, PSI drift.
  - H254 — Arabic/English ambient documentation:
    - the internal speech service;
    - a rule-based SOAP draft;
    - the recording is never stored.
  - H255 — behavioural privacy monitoring from the access log.
  - H256 — read-only downtime copy through Report Studio.
  - H257 — while in test: the outbound feeds send every minute in the background (owner-approved line), and a
    Development-only test exchange stands in for the payer gateway, the feeds' receiver and the payment provider.
