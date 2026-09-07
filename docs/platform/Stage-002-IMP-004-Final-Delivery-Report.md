# IMP-004 — Master Data Source Consolidation — Final Delivery Report

**IMP-004 = Completed** (design). **IMP-003 = Completed · IMP-001 = Completed · IMP-002 = Completed ·
Stage 2A = Not Started.**

No production code changed · no Master Data migration · no schema migration · no SQL executed against `CrossBuyDB2`.

---

## 1. Executive summary

IMP-004 resolved the duplicated Master Data sources of truth and defined safe evolution for items, barcodes, images,
units, packaging, attributes, variants, composites and commerce presentation. Source inspection produced three rules
that a naive cleanup would have broken silently — barcode-to-UoM resolution, missing-conversion rejection, and
ambiguous-barcode rejection — and one gap that reframed part of the work: the two fields governing fresh-food checkout
quantity have **no administration surface in production**. Five risks were raised, all High. Nothing was changed.

## 2. Original objective

Resolve duplicated sources of truth and design implementation-ready future architecture for Master Data, without
implementing the redesign, executing migration, or altering stock, COGS, POS, pricing or manufacturing behaviour.

## 3. Findings that changed the scope

1. **`IsWeighted` and `ScaleCode` have no admin screen anywhere** — writable only via SQL or a `[DevOnly]` endpoint
   (Hypermarket finding F4). They determine fresh-food checkout quantity. This moved them from "fields to migrate" to
   **New Capability Required**: the gap is an administration surface, not a data model. **RISK-049.**
2. **`ItemComponent` has no revision history**, so a historical explosion cannot be reproduced from the data —
   discovered while mapping the composite dependency. **RISK-047.**
3. **Three behavioural rules must be preserved byte-for-byte**, each of which an ordinary "tidy-up" would break (§7–§9).

## 4. Source inventory

**18 sources** in `Stage-002-Master-Data-Source-Inventory.csv` (generated, deterministic):
10 Authoritative · 2 Legacy Compatibility · 2 Ambiguous · 1 Channel-Specific · 2 New Capability Required · 1 Derived.

## 5. Product and Item terminology

Twelve canonical concepts with Arabic/English terminology, resolved by one discriminating rule: *creates or requires a
work order ⇒ Manufacturing BOM; resolves at sale ⇒ Kit / Bundle / Sales BOM; consumes by production logic ⇒ Recipe.*
`RecipeAtSale` backflush is the case spanning both and keeps its current behaviour.

The four overlapping type signals (`ItemType`, `IsComposite`, `CompositeType`, `ProductionMethod`) are **clarified, not
restructured** — stored values keep their meaning; only documentation and new records use the canonical vocabulary.
Design §4.

## 6. Barcode authority

**`ItemBarcode` authoritative; `Item.Barcode` retained as Legacy Compatibility and not deleted.** Primary designation
moves from the column to an `IsPrimary` row seeded from it. Coexistence: read the primary row, fall back to the column,
write both, nightly divergence compare. Seven cases in `Stage-002-Barcode-Source-Matrix.csv`.

## 7. UoM preservation

`UnitOfMeasure` and `UoMConversion(ItemId, From, To, Factor)` **unchanged**. Unit Sets and conversion validity dates are
additive, defaulting to "always valid" so historical reads are unaffected.

**Acceptance equation:** for every existing `(ItemId, From, To)`, `Factor` must return the identical decimal before and
after.

## 8. Missing-conversion rejection

**A unit with no conversion is rejected at add-line and never defaulted to factor 1** (`PosOrderService:547`,
`StockService:236`, Hypermarket assessment). A factor-1 fallback would silently alter every affected quantity — POS,
stock, COGS, invoices. **RISK-048**, severity **High**: impact is Critical but probability requires a deliberate change,
because the current behaviour is correct. The Unit Conversion Designer states this rule **as visible text on the page**
(UX contract §4) — the cheapest defence against a future "helpful" default.

## 9. Ambiguous-barcode rejection

**An ambiguous barcode match is explicitly rejected; the system never guesses.** Consolidation must preserve the
rejection rather than resolve it, and the Barcode Center offers **no "use first match" action at all**. **RISK-050.**

## 10. Images and media

**`ItemImage` authoritative with an `IsPrimary` row; `Item.ImagePath` retained.** Files inherit authorization from the
parent `Item` through 2B unified attachments, served by an **authorizing endpoint — never a guessable static path**
(RISK-012). Four cases in `Stage-002-Image-and-Media-Source-Matrix.csv`.

## 11. Commerce presentation separation

`Item / Product → Commerce Presentation → Store / Marketplace / B2B channel`. The five `Store*` fields become
channel-scoped presentation rows, owned by Commerce/PIM rather than Inventory. **No data is moved in this delivery**; a
read-compatible view keeps `StoreController` working (RISK-011). Design §7, UX contract §2.2.

## 12. Packaging

A **new capability distinct from a unit**: `PackageType` · `ItemPackage` (contained quantity + UoM, dimensions, weight,
volume, barcode) · inner pack / retail pack / carton / pallet. It relates to Unit and Conversion by reference, never by
redefining them, and **introduces no calculation that changes historical stock quantities**. Design §8.

## 13. Attributes and variants

`ProductTemplate → AttributeDefinition → AttributeValue → VariantCombination → VariantItem`, where a `VariantItem`
**is** an `Item`. Fully additive — no existing item is touched. Design §9, UX contract §6.

## 14. Variant measurement blocker

**Still Blocked.** Source code cannot reveal how many existing items are manual variants. Two safe options are
specified (read-only measurement with anonymised aggregates, or an anonymised extract), both requiring **explicit
approval before any execution against `CrossBuyDB2`**. **No migration sizing claim is made.** RISK-034 open, and the
Variants UX shows a visible *"Blocked pending approved data measurement"* state rather than omitting the control.

## 15. Composite dependency

`ItemComponent` is read by **`StockService:319` and `:1189`** (kit explosion), **`PricingService:311`** (manufactured
classification), **`PosOrderService:302` and `:1746`** (sale-time explosion, semi-finished), `ManufService` and
`PosSetupService` — **20 write sites**. Any composite redesign touches the stock writer, pricing, POS and manufacturing
simultaneously. **RISK-033.**

## 16. Composite revision decision

Revisions are **additive**; **existing rows become revision 1** with their current effective date; **no historical
composite is reclassified**, and the designer offers no bulk-reclassify action. Legacy type signals are **displayed
alongside** the canonical type, never silently rewritten. **RISK-047.**

## 17. Weighted items and scale configuration

Preserved exactly: `Item.ScaleCode` filtered uniqueness · scale-code resolution · quantity calculation · **KWD
3-decimal rounding** · FEFO allocation · stock and COGS output · **scale-prefix routing before ordinary lookup** ·
GS1 mod-10. Added (design only): the administration surface with all nine mandated validations, a sample barcode
decoder, uniqueness validation, elevated approval and audit — closing **RISK-049**. UX contract §2.1.

## 18. Hypermarket integration

Master Data explicitly supports weighted items · scale codes and barcode format · branch tax and loyalty
configuration dependencies · PLU readiness · item search · retail categories · store presentation · UoM barcodes ·
FEFO · expiry · fresh-food data · retail packaging · receipt printing data.
**The Hypermarket backend is not rebuilt**; the shared Retail Core and settlement behaviour are preserved. Governed by
the accepted `Stage-002-Hypermarket-Current-State-Assessment.md`.

## 19. Retail classification

**Shared Retail Core + Hypermarket Pack** — adopted from the assessment's own evidence-based recommendation. This also
closes the "retail classification pending" item carried in the Phase 0 reports.

## 20. Product Workspace UX

`Stage-002-Product-Workspace-UX-Contract.md` — **16 areas** with per-area migration impact, the four designer
redesigns (Barcode Center, Unit Conversion, Composite, plus Attributes/Variants as new), universal states including
**partial-permission**, and universal audit with elevated approval on the three money-or-quantity areas.

**The sold-unit guard** is the central UX requirement: changing a barcode's `UoMId` needs **warning + permission +
audit reason + impact preview + approval**, never an inline edit.

## 21. Mobile and operational UX

The desktop workspace is **not** squeezed onto a phone. Six focused mobile workflows instead — barcode scan/lookup
(showing the resolved UoM), image capture, stock lookup, approval, **weighted-item validation** (decode a sample scale
barcode and confirm the quantity, field-verifiable), field inspection. Areas 4, 5 and 6 are **desktop-only**; tablet
gets the full workspace at `compact` density; offline is read-only with a stale indicator.

## 22. Source-of-truth coexistence

Per duplicated source: `Legacy → Shadow → NewAuthoritative → LegacyReadOnly → Retired`. **Never a union**, and **two
writers are never simultaneously authoritative**. A successful write to a non-authoritative source must never be
silently ignored — the IMP-001 RISK-038 lesson applied here.

## 23. Migration design

Thirteen phases (design §16), additive first · idempotent · resumable · auditable · reversible before cleanup · safe per
company and per module · **safe for POS, Hypermarket, WMS and Manufacturing**. **Phases 1–6 change no behaviour.**

## 24. Non-regression gates

**24 invariants, zero-tolerance**, with deterministic hashes over canonical datasets on **isolated disposable SQL
Server** databases (RISK-036), carrying `[RequiredEvidence]` so they **cannot report skipped** (RISK-026). Tolerance is
permitted only where mathematically required (currency rounding at declared scale). Any breach is a rollback trigger.
Design §15.

## 25. Risk Register changes

Merged: **RISK-046** (barcode migration could change the resolved sold unit) · **RISK-047** (composite history cannot be
reproduced) · **RISK-048** (factor-1 fallback would corrupt quantities) · **RISK-049** (weighted/scale has no admin
surface) · **RISK-050** (consolidation could introduce first-match guessing). **All five High.**

**Totals: 50 risks — 8 Critical · 25 High · 14 Medium · 3 Low.** Previously 45 (8/20/14/3).
**Delta: +5 High, +5 total. No Critical added.**

RISK-048's severity is **justified, not defaulted**: impact Critical, probability Medium, because **the current
behaviour is correct** — the risk guards a future regression rather than recording a present defect.

## 26. Completed artifacts

| Artifact | Note |
|---|---|
| `Stage-002-Master-Data-Source-Consolidation-Design.md` | authoritative consolidated design |
| `Stage-002-Product-Workspace-UX-Contract.md` | 16 areas |
| `Stage-002-Master-Data-Source-Inventory.csv` | **generated** — 18 sources |
| `Stage-002-Barcode-Source-Matrix.csv` | **generated** — 7 cases |
| `Stage-002-Image-and-Media-Source-Matrix.csv` | **generated** — 4 cases |
| `generate-imp004-artifacts.py` | the generator |
| `Stage-002-Risk-Register.md` + `.csv` | **both regenerated from one canonical source** |

**Consolidated rather than duplicated** (permitted by the brief): the UoM dependency map (design §8), composite
dependency map (§11), weighted-item and scale contract (§12 + UX §2.1), commerce presentation separation (§7), variant
measurement plan (§10), non-regression gates (§15) and migration/rollback (§16). Referenced, not duplicated:
`Stage-002-Hypermarket-Current-State-Assessment.md` · `Stage-002-Master-Data-Reassessment.md` ·
`Stage-002-Enterprise-Design-System-Proposal.md`.

## 27. Verification state

| Check | Result |
|---|---|
| Application build, **Razor enabled** | **0 errors** |
| Full suite, **SQL evidence enabled** | **766 total · 766 passed · 0 failed · 0 skipped** |
| Risk Register generated twice | **identical hashes** |
| Markdown hash | `e2992de27bf99efeacd3bb11eb006d6b` |
| CSV hash | `456d314bed12d3e67b3c2d168e2fb971` |
| Risk totals | **50 — 8 Critical · 25 High · 14 Medium · 3 Low** |
| Scratch databases remaining | **0** |
| `CrossBuyDB2` | **present and untouched** |

No skipped evidence test is counted as coverage — there are none.

## 28. Known limitations

1. **Variant migration cannot be sized** until measurement is approved (RISK-034). This is the single largest unknown
   in Stage 2C scope.
2. **Composite revision history is designed, not built** — until it exists, a component edit still makes a historical
   explosion unreproducible (RISK-047).
3. **Weighted/scale fields remain unmanageable in production** until the admin screen is built (RISK-049).
4. **The three preserved rules are documented, not enforced by a test yet.** Until the non-regression suite exists,
   nothing mechanically prevents a factor-1 fallback or a first-match barcode resolution being introduced.
5. **`InventoryController` retains 267 hardcoded company references** (RISK-003); the Class A sweep must land in 2C
   **before** the new screens, as its own commit.
6. **No per-item measurement of divergence** between `Item.Barcode`/`ItemBarcode` or `Item.ImagePath`/`ItemImage` — the
   duplication is confirmed, the *extent* is not. Needs the same approved read-only access as the variant measurement.

## 29. Items explicitly not implemented

Any schema change · any migration · the Product Workspace or any of its 16 areas · Barcode Center · Unit Conversion
Designer · Composite Designer · Attributes/Variants · the weighted-item admin screen · Commerce Presentation tables ·
packaging tables · variant tables · the non-regression suite · Stage 2A analyzers.

## 30. Requirement status

| Gate | Status |
|---|---|
| 1–9 UX contract: 16 areas, Retail and Commerce, weighted-item, Barcode Center, Unit Conversion, Composite, Attributes/Variants, mobile, migration impact | **Completed** |
| 10–14 RISK-046…050 merged | **Completed** |
| 15 Deterministic regeneration | **Completed** |
| 16 Final report exists | **Completed** — this document |
| 17 Final verification passes | **Completed** (§27) |
| 18–21 No code / migration / schema / `CrossBuyDB2` SQL | **Completed** |
| 22 Stage 2A not started | **Confirmed** |

**IMP-004 = Completed. 22 of 22.**

## 31. Recommended next step

**Close Phase 0.** All four Required-Now designs (IMP-001…IMP-004) are complete; the outstanding Phase 0 items are the
three source-generated catalogs (Domain, Capability Map, API), which the roadmap places in **2D** and which block
nothing.

Two things to settle at closure, both decisions rather than work:

1. **Approve the read-only measurement** (variants, plus barcode/image divergence extent). Three separate items now
   depend on it, and Stage 2C cannot be sized without it.
2. **Confirm Stage 2A's scope gate.** RISK-037 and RISK-040 together mean Wave 2's authorization would evaluate
   bootstrap-open on **both** the HR path and the financial path — so the Grant Writer should be scheduled with 2A, not
   after it.

**Stopping for review. Phase 0 not closed. Stage 2A not started.**
