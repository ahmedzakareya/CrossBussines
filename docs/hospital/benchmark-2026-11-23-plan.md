# Hospital — fourth benchmark (2026-11-23): re-score after H230–H258, and the next plan

Owner, 2026-11-23: «قيس وبعدين كمل بالي تشوفه» ("measure, then carry on with what you see fit").

## Re-score (leader = 10; the Gulf mid-size minimum bar is about 6)

Scored against the same leaders as the 2026-11-22 benchmark: Epic, Oracle Health, TrakCare, MEDITECH, HIMSS EMRAM and JCI 8th edition. Each score credits only what is built and was verified live or by tests.

| # | Domain | Before | Now | What moved it | Still behind the leaders |
|---|--------|-------:|----:|---------------|--------------------------|
| 1 | Registration, front desk, scheduling | 8 | 8 | — | National-ID lookup (external); no-show prediction |
| 2 | Outpatient documentation | 7.5 | 8 | Ambient documentation (H254) | Full SNOMED/LOINC coding |
| 3 | CPOE & decision support | 5.5 | 7 | Duplicate therapy, pregnancy/lactation, stewardship, override analytics | Licensed drug knowledge base (external) |
| 4 | Inpatient & nursing | 7.5 | 8 | Acuity staffing, capacity forecast | Smart-pump integration (external) |
| 5 | Pharmacy | 8 | 8 | — | Dispensing cabinets (external) |
| 6 | Laboratory | 5 | 7.5 | Microbiology, antibiogram, Westgard QC, autoverification, delta checks, age/sex ranges | LOINC on analytes |
| 7 | Radiology | 5.5 | 6.5 | Follow-up tracking, dose, ACR categories | Structured reporting; radiologist peer review |
| 8 | Surgery, anaesthesia, CSSD | 6.5 | 7 | Preference cards, block utilisation | Structured operative note |
| 9 | Emergency | 4.5 | 7 | Time-critical pathways, ambulance pre-arrival (verified live) | — |
| 10 | ICU | 4 | 6.5 | GCS, APACHE II, titration, bundles, device-days | Device integration (external) |
| 11 | Maternity & neonatal | 6 | 7 | NICU | CTG interface (external) |
| 12 | Specialty clinics | 8 | 8 | — | Ophthalmic devices (external) |
| 13 | Revenue cycle | 7 | 8 | DRG, denials work-queue, FHIR eligibility/pre-auth (verified live) | Real payer gateways (waiting on addresses) |
| 14 | Portal & engagement | 5.5 | 7.5 | Messaging, questionnaires, proxy, online payment (verified live) | Patient AI assistant |
| 15 | Interoperability | 5.5 | 7 | Outbound ADT/ORU in the background (verified live), FHIR read API | LOINC/SNOMED in what is sent |
| 16 | Analytics, quality, AI | 4.5 | 6 | Readmission model, validated locally, with drift monitoring | Disease registries; no-show model; data mart |
| 17 | Security, privacy, downtime | 7 | 8 | Behavioural privacy monitoring, downtime copy | One audit viewer of who opened what |
| 18 | Supply, assets, staff | 5.5 | 6.5 | Biomedical maintenance, acuity staffing | Licence-expiry alerts ahead of time |

**Overall: about 7.3 out of 10, up from about 6.3.** Every domain is now at or above the Gulf minimum bar.

The remaining gaps fall into two groups:
- **External.** They need a vendor, a licence or a device: the drug knowledge base, pumps, cabinets, CTG, ophthalmic devices, the civil-ID lookup and the real payer and exchange addresses.
- **Ours to build.** These are listed below.

## The plan (H259 onwards), in order

### Phase 1 — clinical documentation
1. **H259 Radiology structured reporting and peer review.**
   - Report templates per study type, with required sections.
   - A RADPEER-style score when a second radiologist reviews a signed report.
   - Discrepancy rates per radiologist.
2. **H260 Structured operative note.**
   - Written on the theatre case: procedure, findings, specimens, blood loss, implants, complications.
   - Signed by the surgeon.
   - The case cannot close without it.

### Phase 2 — coding and interoperability
3. **H261 LOINC on laboratory analytes.**
   - A LOINC code on each analyte, with a starter list for common tests.
   - Carried in outbound ORU (OBX-3) and in the FHIR Observation, next to the local code.

### Phase 3 — population health and prediction
4. **H262 Disease registries.**
   - Diabetes, hypertension and chronic kidney disease, drawn from diagnoses, labs and medicines.
   - Care gaps per patient: HbA1c overdue, BP uncontrolled, eGFR or urine albumin overdue.
   - Search and pagination.
5. **H263 No-show prediction.**
   - A logistic model trained on the hospital's own appointments: lead time, weekday, earlier no-shows, visit type.
   - Validated locally (AUC), the same way as the readmission model.
   - High-risk appointments flagged on the front desk for a reminder call.

### Phase 4 — security and staff
6. **H264 Audit viewer.**
   - One searchable list of who opened which patient file, when, and through which screen.
   - Filters by person, patient and date.
7. **H265 Licence-expiry alerts.**
   - Practitioners whose licence ends within 60 days, on a list and as a notification to the medical director.

## Progress
  - H259 — structured radiology report templates (required sections gate signing) and RADPEER peer review with per-radiologist rates.
