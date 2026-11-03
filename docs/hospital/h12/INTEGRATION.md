# H12 Estimates & packages — code-only unit, integration notes

Status (2026-11-03): **applied and running at the owner's instruction.** The slice ran on CrossBuyDB2, the SHF-02 line is in, and :7183 serves the screens. Originally: It was built under the owner's instruction to continue the prototype without conflicting with the current restrictions: new unclaimed files only, no SQL run, no shared file, no git.

## What it is (prototype screen "Estimates & deposits")
- **Packages** (`manage`): a procedure at a fixed price.
  - It has a main service (the one the payer approves), included nights, a room class, and what it covers in both languages.
- **Estimates** (`bill`): a planned admission for one patient.
  - It is built from a package and/or services, a room class and the expected nights.
  - **Priced exactly as the patient's bill would be:** through the patient's H7 list, with `HospitalPricingService.ListForPatientAsync`, `QuoteServiceAsync` and `QuoteRoomRateAsync`.
  - **Split by H11's one rule:** `HospitalInsuranceRules.Split`, using the patient's cover today. Lines outside the list are the patient's.
  - It states the deposit to ask: the expected patient share plus a margin, rounded up.
  - A draft is re-priced on every change. Issuing freezes it, and when the contract needs approval for what it contains, issuing files the H11 approval request, or links one already waiting.
- **Not in H12 (each would change billing or accounting, which is not authorised):**
  - collecting the deposit;
  - the "open deposits for inpatients" panel with its 80% alert;
  - turning a package into a fixed-price bill.

  The screen says so in place of the prototype's "Collect deposit" button.

## Shared or foreign dependencies

| # | File | Owner | Change | Note |
|---|---|---|---|---|
| E1 | `CrossBuy/Models/Context/CrossDbContext.cs` | SHF-02, TAB-0 (Integration Owner) | +1 line after H11's: `Hospital.HospitalEstimatesModel.Configure(builder);` | **Done 2026-11-03.** **H12 needs H11 first** (`HospitalPreauths`, `HospitalPayers` and `HospitalPatientPolicies` are FK targets). |
| E2 | `CrossBuy/deploy/sql/hospital_h12_estimates.sql` + the 5 registries | TAB-1 | in place (2026-11-03); register it through TAB-1 and the generator | Three new tables. Applied 2026-11-03 and re-run with an identical object snapshot. |
| E3 | CrossBuyDB2 | owner | apply after H11 | **Done 2026-11-03** (after H11). |
| E4 | `Resources/HospitalResources*.resx` | TAB-1 | +64 keys from `docs/hospital/h12/strings.json` | 22 keys reused. `Any` was not reused (it means "Both sexes"); `Any room class` replaces it. Four keys arrive with H11's strings. |
| E5 | `CrossBuy/Models/Menu/MainMenu.cs` | UNCLAIMED | `new() { LabelAr = "التقديرات والتأمين المقدم", LabelEn = "Estimates & deposits", Action = "Estimates", Controller = "Hospital" },` in "Finance & insurance" | Owner review. |
| E6 | Report Studio dataset | reporting | "Print estimate for patient" must print through Report Studio (house rule) | No print button is shown until a dataset is registered. |
| E7 | `HospitalBillingService` | — | deposit receipt on account, the 80% alert, package billing | Design together with H11 §7 (accounting). |

## Evidence
- Tests: `CrossBuy.Tests/HospitalEstimateRulesTests.cs` (10, pure).
- `HospitalInsuranceGuardTests` also covers H12:
  - every H12 entity names a table its slice creates (`ToTable`; there is no DbSet);
  - every FK target the slice does not create is in its THROW prerequisite check.
