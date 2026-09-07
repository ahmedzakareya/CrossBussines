# Stage 2 — Phase 0 — FREEZE

**These documents are historical records. They must never be rewritten.**
Future corrections require **new** Stage 2 documents that reference — and supersede by pointer — the frozen record.
Correction pointers may be added to a frozen document; its conclusions may not be silently altered.

Frozen on the verification baseline in §8.

---

## 1. Frozen documents — 30

**Closure and status:** `Phase-0-Closure-Report` · `Phase-0-Freeze` (this) · `Phase-0-Completion-Report` ·
`-Report-2` · `-Report-3` · `Requirement-Status`

**Architecture and planning:** `Architecture-Review-and-Improvement-Proposal` · `Dependency-Roadmap` ·
`Engineering-Platform-Proposal` · `Enterprise-Platform-Framework-Contracts` · `Enterprise-Design-System-Proposal` ·
`Screen-Forecast` · `Business-Value-Roadmap` · `002A-Execution-Roadmap`

**IMP-001:** `Role-Source-Consolidation-Design` · `Platform-Grant-Writer-Design` · `IMP-001-Final-Delivery-Report`
**IMP-002:** `Bootstrap-Open-Governance-Design` · `IMP-002-Final-Delivery-Report`
**IMP-003:** `EF-Relational-Model-Validation-Design`
**IMP-004:** `Master-Data-Reassessment` · `Master-Data-Source-Consolidation-Design` ·
`Product-Workspace-UX-Contract` · `IMP-004-Final-Delivery-Report`

**Hypermarket (accepted as input):** `Hypermarket-Current-State-Assessment` · `Hypermarket-Business-Flow-Map` ·
`Hypermarket-Gap-Analysis` · `Hypermarket-Roadmap-Proposal` · `Hypermarket-Screen-Inventory` ·
`Hypermarket-Requirement-Status` · `Hypermarket-Risk-Register`

**Risk register:** `Risk-Register.md` — **generated; never hand-edited.**

## 2. Frozen CSV artifacts — 10

`Stage-002-Risk-Register.csv` · `Improvement-Backlog.csv` · `Role-Source-Inventory.csv` ·
`Role-Migration-Matrix.csv` · `Legacy-Role-Writer-Shutdown-Matrix.csv` · `Bootstrap-Open-Inventory.csv` ·
`EF-Relationship-Inventory.csv` · `Master-Data-Source-Inventory.csv` · `Barcode-Source-Matrix.csv` ·
`Image-and-Media-Source-Matrix.csv`

**Four generators are frozen as the only writers of their outputs:** `generate-risk-register.py` ·
`generate-imp001-artifacts.py` · `generate-imp002-inventory.py` · `generate-imp004-artifacts.py`.
**Editing a generated CSV or the generated Markdown by hand is prohibited** — regenerate instead.

## 3. Frozen architectural decisions

1. Registry + capability metadata + composition + event projections — **never a universal base entity**.
2. `ProjectMembers` and `ConversationMembers` are **business memberships**, never role rows.
3. Identity roles stay separate from module grants.
4. **`BranchUserRoles` is the sanctioned POS exception** — lane login precedes `BusinessContext`.
5. Coexistence **never unions** sources; one authoritative source per (company, scope).
6. Coexistence states: `Legacy` · `Shadow` · `Platform`.
7. Legacy writers **must stop writing at cutover**; no silent success.
8. Effective permissions come from the **real access services**, never a second engine.
9. Bootstrap-open is **policy, not a grant** — separate `BootstrapAccessPolicies` table.
10. Missing bootstrap configuration resolves to **`LegacyCompatibility`**, preserving current behaviour.
11. `ItemBarcode` authoritative; **`Item.Barcode` retained**, not deleted.
12. `ItemImage` authoritative; **`Item.ImagePath` retained**, not deleted.
13. **`ItemBarcode.UoMId` selects the sold unit** — semantics preserved byte-for-byte.
14. **A missing UoM conversion rejects the line — never factor 1.**
15. **An ambiguous barcode is rejected — never first-match.**
16. Composite revisions additive; **existing rows become revision 1**; no automatic reclassification.
17. `Item.ScaleCode` filtered uniqueness and scale-prefix routing preserved exactly.
18. Retail classification: **Shared Retail Core + Hypermarket Pack**; Hypermarket backend preserved.
19. Commerce presentation separated by design; **no data moved**.
20. EF model: `EnsureCreated` is **not** a deployment mechanism; idempotent SQL remains authoritative. One
    incompatible relationship (`FK_Branches_CountriesLookup_CountryID`) with `OnDelete(NoAction)` recommended.
21. Two writers only — `JournalEntryService` (GL) and `StockService` (stock) — unchanged.
22. Design tokens feed Metronic/Bootstrap, **not the reverse**.
23. Stage 2A begins with evidence-backed guardrails; the other 18 analyzers remain trigger-based.
24. **Master Data precedes WMS and Manufacturing expansion.**

## 4. Frozen terminology

`Item · Product · Service · Asset · Kit · Bundle · Sales BOM · Manufacturing BOM · Recipe · Configurable Product ·
Variant · Packaging Unit`, disambiguated by: *creates or requires a work order ⇒ Manufacturing BOM; resolves at sale ⇒
Kit / Bundle / Sales BOM; consumes by production logic ⇒ Recipe.*

Classification vocabularies frozen: source-of-truth (Authoritative · Legacy Compatibility · Derived ·
Channel-Specific · Duplicate · Ambiguous · Deprecated Candidate · New Capability Required) · bootstrap policy (6
categories) · module treatment (Preserve · Extend · Refactor · Partial Platform Build · Major Extension · Full Platform
Build) · migration impact (6 values).

## 5. Frozen identifiers

**`RISK-001` … `RISK-050`** — 50 risks. **`IMP-001` … `IMP-019`** — improvements. **`CORR-001`, `CORR-002`** —
the WMS and Manufacturing reclassifications. Inventory ids: `SRC-*`, `BO-*`, `MD-*`, `BC-*`, `IM-*`, `W-*`, `EF-*`.

**Legacy ids (`R1…R21`, `R-01…R-32`) are preserved in accepted documents and in the CSV `legacy_id` column. They are
never renamed.** Mapping: `IMP-001-Final-Delivery-Report.md` §1.

**Reserved: `RISK-051+` and `IMP-020+`** for Stage 2A.

## 6. Frozen completion reports

`IMP-001-Final-Delivery-Report` (20/20) · `IMP-002-Final-Delivery-Report` (20/20) ·
`EF-Relational-Model-Validation-Design` (IMP-003, 7/7 gates) · `IMP-004-Final-Delivery-Report` (22/22) ·
`Phase-0-Closure-Report`.

## 7. Frozen metrics

| Metric | Value |
|---|---|
| Stage 2 documents | **30** |
| CSV artifacts | **10** |
| Generators | **4** |
| Risks | **50** — 8 Critical · 25 High · 14 Medium · 3 Low |
| Improvements | **27** — incl. 6 rejected, reasons retained |
| Required-Now IMPs completed | **4 of 4** |
| Stage 1 maturity (frozen) | **57.60 / 100** |
| Authorization backlog (frozen) | **143** of 388 mutating actions |
| Company constants (frozen) | 13 controllers · 908 references |

## 8. Frozen verification baseline

| Check | Value |
|---|---|
| Application build, **Razor enabled** | **0 errors** |
| Full suite, **SQL evidence enabled** | **766 total · 766 passed · 0 failed · 0 skipped** |
| Risk Register Markdown hash | `e2992de27bf99efeacd3bb11eb006d6b` |
| Risk Register CSV hash | `456d314bed12d3e67b3c2d168e2fb971` |
| Deterministic regeneration | **verified — identical across consecutive runs** |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |

Any future claim that differs from this baseline must be re-measured, not assumed.

## 9. Rules after freeze

1. **No frozen document is rewritten** — corrections are new documents with pointers.
2. **No generated artifact is hand-edited** — regenerate from its generator.
3. **No identifier is renumbered.**
4. **No frozen decision is reversed silently** — reversal requires a new document stating the evidence that changed.
5. **The verification baseline is the reference** for every later comparison.
