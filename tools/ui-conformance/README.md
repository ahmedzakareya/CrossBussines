# CrossBusiness — UI Conformance Verification

**One ERP. One visual language. The Inventory module is the authority.**
This tool decides, mechanically, whether that is still true. It is **verification only** — it modifies
no view, layout, stylesheet, script or resource.

```powershell
.\run.ps1                # both halves -> PASS or FAIL
.\run.ps1 -StaticOnly    # source gates only (CI with no browser/server)
.\run.ps1 -Bless         # record screenshot baselines (deliberate, dedicated commit)
```

---

## 1. Two halves, and why the split is not cosmetic

| Half | Where | Decides | Cost |
| --- | --- | --- | --- |
| **Static** | `CrossBuy.Tests/UiConformance/UiConformanceTests.cs` | everything decidable from source | ~300 ms, no browser |
| **Rendered** | `ui-conformance.mjs` (Playwright) | visual · responsive · RTL · accessibility | needs the app running + a sign-in |

Layout, responsive, RTL and accessibility regressions **cannot** be found by grepping Razor. Pretending
otherwise is how a suite goes green while the product is broken. So they live in the second half, and
**a run that cannot reach the app reports NOT VERIFIED and exits non-zero — never PASS.**

---

## 2. What the static gates enforce

| Gate | Rule |
| --- | --- |
| 1 | Every governed screen renders through `_LayoutInventory.cshtml` |
| 2 | The authority declares only its two approved shells (`_LayoutInventory`, `_LayoutManufacturing`) |
| 3 | No view carries a live `cbw-*` class (the retired design language) |
| 4 | The retired stylesheet and layout have **no active referrer** — this is the deletion gate |
| 5 | A governed screen links only an approved stylesheet |
| 6 | No in-scope stylesheet outside the brand file defines design tokens |
| 7 | No inline `<style>` defines design tokens |
| 8 | Every governed table is a Metronic `table-row-*` variant |
| 9 | The authority matches its recorded baseline |
| 10 | The exception list has no stale entries — **it may only shrink** |

**Governed:** Inventory (authority) · Reports · Workspace · BusinessEventMonitor · Tasks · Calendar.
The legacy sign-in, portal and POS-terminal screens are **out of scope** — they are separate products
with their own shells and were never claimed by this rule. Asserting over them would produce dozens of
findings nobody agreed to own, and a gate that cries wolf gets switched off.

### 2.1 The distinction that makes gate 6 and 7 trustworthy

A **design token** is a hardcoded colour, a typeface, or a custom-property *definition* — something
others inherit. Overflow, cursor, `max-width` and `prefers-reduced-motion` rules are not a language;
they constrain a box.

`var(--bs-primary, #13433a)` **consumes** the shared language rather than competing with it, so `var()`
expressions are stripped before the colour scan. Without that single rule the gate would flag correct
code, and people would learn to ignore it.

Worked example, from one file: `.cbev-personal { background: var(--bs-primary, #13433a) }` passes;
`.cbev-company { background: #1b84ff }` fails. Three lines apart.

---

## 3. The exception list — shrink-only

`baseline/known-exceptions.json` pins divergences that exist today, each with **file · reason ·
severity · owner**.

Why pin rather than simply fail: the tree already contains divergences owned by other tabs. A gate that
failed outright would be switched off within a day; a gate that passed silently would be worthless.
Pinning keeps the build green **and** keeps the debt visible and attributed.

Two rules make it safe, and gate 10 enforces the second:

1. A **new** divergence fails the build.
2. A pinned divergence whose file is gone **also** fails — forcing the list to shrink instead of rotting
   into a permanent amnesty.

This mirrors `authorization-baseline.json`, which the platform already governs the same way.

---

## 4. Baselines

`baseline/authority-baseline.json` records the authority's aggregate hash:

```
aggregate = sha256( concat( "<repo-relative-path>:<sha256-of-file>\n" ) , sorted by ordinal path )
```

Drift is **not automatically wrong** — the Inventory owner may change their own module. It must be
*surfaced*, because every other module is measured against it and silent re-baselining would make
"matches Inventory" unfalsifiable. Re-blessing is a deliberate act by the module owner, in a dedicated
commit, never bundled with a feature change.

`baseline/screenshots/` holds rendered baselines, recorded with `-Bless`. Same discipline.

---

## 5. Running the rendered half

```powershell
$env:UI_BASE_URL = 'https://localhost:44368'
$env:UI_USER     = 'someone@alprimecap.com'
$env:UI_PASS     = '...'
.\run.ps1
```

**Credentials are mandatory, and the tool refuses to run without them.** Every governed route is
`[SessionValidation]`. An unauthenticated run would follow the redirect to `/Account/Login`, screenshot
the login page eleven times, find no violations on it, and report PASS. That exact failure mode is why
the check is a hard error rather than a warning.

Each run renders **11 routes + the Inventory control**, × 2 cultures, × 3 viewports (1440 / 992 / 390).
The control is rendered in the *same session*, so conformance is judged against a live Inventory render
rather than a remembered one.

Per route it asserts: HTTP < 400 · `<html dir>` matches the culture · no horizontal page overflow ·
no critical/serious axe violation (WCAG 2 A/AA) · no console error · no failed request · screenshot
within `maxDifferingRatio` (0.5%, enough to absorb antialiasing and live data, not enough to hide a
restyle). A **page-size change is reported as a layout regression** rather than crashing the diff.

---

## 6. Output

Exactly one verdict: **PASS** or **FAIL**. On FAIL, every finding carries **file/route · check ·
reason · severity · owner**, sorted Critical → Low, with pixel diffs in `artifacts/`.

`artifacts/report.json` holds the machine-readable run.

**On PASS the tool recommends cleanup; it never performs it.** Deleting the retired stylesheet is the
Workspace owner's act, in a dedicated cleanup commit.

---

## 7. CI

```powershell
.\run.ps1 -StaticOnly     # every build - fast, hermetic
.\run.ps1                 # before a release, against a deployed instance
```

The static half is hermetic and belongs on every build. The rendered half needs a running app, so it
belongs on a stage where one exists — and its absence is reported as NOT VERIFIED, never as a pass.
