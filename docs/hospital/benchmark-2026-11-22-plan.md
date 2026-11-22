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
