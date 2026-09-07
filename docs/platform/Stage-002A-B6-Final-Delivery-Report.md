# CrossBusiness Platform — Stage 2A · B6 — Final Delivery Report

**Mechanism A replacement · three approved sites · verified on a fresh integrated build.**

---

## 1. What B6 changed

Mechanism A is a single line placed **before** the action switch:

```csharp
if (!await AnyRoleConfiguredAsync(context.CompanyId, ct)) return true;
```

Because it ran before the action was examined, it could not distinguish reading a balance from posting to the ledger.
On a company with no configured roles it granted **everything**, silently, with no decision record.

Three approved sites were replaced with an action-aware decision that consults `NeverBootstrapOpen` **before** it
touches the database, then an explicit stored policy.

| # | Site | Status | Tests |
|---|---|---|---|
| 1 | `AccountingAccessService.cs:60` | **CONVERTED** | 14 / 14 |
| 2 | `InventoryAccessService.cs:48` | **CONVERTED** | 8 / 8 |
| 3 | `InventoryAccessService.cs:91` | **CONVERTED** (narrows) | 10 / 10 |
| 4 | `CrmAccessService.cs:59` | **NOT CONVERTED — blocked** | — |
| 5 | `CrmAccessService.cs:98` | **NOT CONVERTED — blocked** | — |

Detail per site: `Stage-002A-B6-Accounting-Evidence.md`, `-Inventory-Evidence.md`, `-Warehouse-Scope-Evidence.md`.

## 2. Verification — this increment

Every figure below comes from a run on a **verified fresh build**; build error counts were captured explicitly, and
`--no-build` was used only after such a build.

| Phase | Result |
|---|---|
| **1** Integrated build, outputs deleted first | Debug **0 Error(s)** · Release **0 Error(s)** · TestRun **0 Error(s)** |
| **2** `Stage1F4DatabaseProofTests` | target test **4 / 4** · whole class **15 / 15 · 0 skipped** |
| **3** Focused B6 | Accounting 14 · Inventory 8 · Warehouse 10 · **combined 32 / 32 · 0 skipped** · diagnosis **3 / 3** |
| **4** Full application suite | **Failed: 0 · Passed: 1502 · Skipped: 0** (7 m 55 s) |
| **5** Analyzer + guards | analyzer build **0 Error(s)** · analyzer tests **93 / 93** · guards **52 / 52** · **CBA001 = 0 · CBA004 = 0 · CBA006 = 0** |

**Skipped = 0 across the whole suite.** The 183 skips reported on the shared tree were the SQL evidence tests skipping
for want of `CROSSBUY_TEST_SQL`; with the connection configured they execute. No coverage is claimed from a skip.

Phase 2 matters on its own: the F4 transition had been applied but **unverified for two increments**. It is now
verified — the negative moved from *allowed-but-harmless* to *denied-and-harmless*, with the zero-business-effect
proof (`reachable == 0` plus the fingerprint comparison) untouched.

## 3. Security and environment gates

| Gate | Result |
|---|---|
| Authorization debt | **143 — unchanged**; the gap set matches the baseline **id for id** (143 entries, `sha256 00dd9a62f6f8ec26`) |
| Baseline additions | **none** |
| Suppressions | **none** |
| Endpoint totals | **391 = 157 attribute + 91 in-body + 143 debt** |
| Probe databases | **0** |
| Mutation markers | **0** (`grep -rl "MUTATION [A-H]"`) |
| `CrossBuyDB2` | present, **untouched** — no SQL executed against it in this increment |
| Files owned by tabs 2–4 | **not modified** |

### The endpoint totals moved — reconciled, not waved through

The accepted Stage 1 measurement was **388 / 157 / 88 / 143**. The file now reads **391 / 157 / 91 / 143**.

The delta is **Batch A**, recorded in the baseline file at the time with its own provenance note: three mutating
endpoints and one controller were added — `PlatformGrantsApiController.Create`, `.Revoke`, `.UpdateValidity` — and all
three are **in-body authorized** through `IPlatformGrantWriter`, which resolves the actor's administration tier and
privilege ceiling via the real module access services. Hence `+3` mutating and `+3` in-body, with attribute-protected
and debt both flat.

**B6 itself moved no total**: it changed two service files, and `grep -cE "\[Http(Post|Put|Delete)\]"` returns **0**
for both. Production code was added by an earlier approved batch; nothing was reclassified, and no gap was retired
without evidence.

## 4. What B6 deliberately did NOT do

* **No CRM conversion.** Both CRM sites are byte-for-byte unchanged — `:59 return true;` and `:98 return null;`
  verified by grep this increment, and asserted by reflection in the warehouse test group so a premature conversion
  fails a test rather than passing review.
* **No data-exposure change.** B6 replaces the **permission source only**. `Accounting.read` still exposes ledger
  balances and `Inventory.read` still exposes stock costs, both unfiltered by branch. Recorded as open business
  decisions, not quietly fixed.
* **No `Inventory.purchase` split.** The action gates a PO draft, a goods receipt (creates stock) and a PO-to-invoice
  conversion (accounting effect). Per owner decision it stays **one closed action**; a test pins the
  `VOCABULARY DEFECT` reason so the decision cannot be reversed silently.
* **No bulk test rewrite.** Seven pre-B6 tests were reviewed individually; 5 renamed, 4 still-valid assertions
  preserved, 0 deleted, 0 bulk-rewritten — `Stage-002A-B6-Test-Transition-Decisions.md`.

## 5. Methodology rules this delivery is built on

Both were learned by being violated:

* **A test result from an unverified build is not evidence.** Mutation H first "passed" because the build had failed
  and `--no-build` ran a stale assembly. Every run here checks `0 Error(s)` first.
* **A grep count is a proxy; a hash against a verified artifact is the fact.** I once reported a production file
  damaged on the strength of a grep count of `0` — which was the *correct* post-conversion value. Hashes proved both
  production files intact and located the divergence in a test file.

And a third, from the diagnosis: when a security test passes for an unknown reason, **instrument the real path** —
`B6AccountingReadPathDiagnosisTests` wraps the **real** reader rather than a stub, because a stub can produce either
outcome, which was precisely the failure mode under investigation.

## 6. Residual risk

| Risk | State |
|---|---|
| Two CRM Mechanism A sites remain open | **OPEN** — blocked on business decisions 3 and 4 |
| `Accounting.read` / `Inventory.read` data breadth | **OPEN** — decisions 1 and 2 |
| Compatibility policies are temporary by design | Mitigated: bootstrap allows log at **Information**, carry a policy id, and expire |
| RISK-042 PlatformOps via accounting fallback | **Closed** — `Accounting.manage` is Never, asserted directly |
