# 12 — Brand drift and risks

Where the product departs from its own stated rules. Every item is measured, and every item names
the rule it departs from — this is a comparison against CrossBuy's own standards, not against an
outside opinion.

Ordered by how much a reader should care.

---

## 1. Seven product names in the browser tab

**Rule:** one product, one name.
**Measured:** 20 screens loaded; 7 distinct `<title>` product names.

```
Inventory System - CrossBuy     7 screens   ← incl. Workspace, Tasks, Reports, Calendar
Accounting System - CrossBuy    5 screens   ← incl. CRM
CrossBuy Admin                  5 screens   ← incl. Projects, Restaurant, Hyper, Chat
Warehouse System - CrossBuy     1 screen
CrossBuy POS / CrossBuy Hyper / CrossBuy      layouts not captured
```

**Cause:** three layouts set `<title>@Localizer["PageTitle"]` — a constant in that layout's
resource file. The screen cannot name itself. Every screen borrowing a layout inherits that
layout's identity.

**Why it matters commercially:** the browser tab, the bookmark, the window switcher and every
screenshot a customer takes name the wrong product. The Workspace — the platform's own flagship
surface, first item in the Platform menu — calls itself *Inventory System*.

**Also:** the one layout that says `CrossBusiness` (`_LayoutWorkspace`) has **zero references**.
The second product name does not ship today, but it is alive in 22 source files and one merge away
from the UI.

## 2. The logo's wordmark is live text in a font the product does not ship

**Rule:** a logo renders identically everywhere.
**Measured:** both SVG logos render the wordmark as `<text>`, not outlines, in
`font-family="Segoe UI, Tahoma, Arial, sans-serif"` — a Windows system font that is **neither
Cairo nor Inter**.

On macOS, Linux, or any machine without Segoe UI, the wordmark silently becomes Tahoma or Arial:
different letterforms, different width. The PNG lockups are outlines and are safe; the SVGs are
what the backend chrome uses.

**Fix:** convert the two `<text>` elements to paths. It is a one-time, low-risk change.

## 3. The brand accent and the warning semantic are the same hex

**Measured:** the logo's accent is `#F59E0B` (`crossbuy-logo.svg`, the `Buy` tspan, and the amber
rule under the sign-in footline). `--bs-warning` is also `#F59E0B`.

Nothing in the token layer distinguishes them. A developer painting a *brand accent* and a
developer painting a *caution state* both reach for the same value, and neither is wrong.

**This is the root cause of the amber problem**, and it explains why the rule has to be enforced
by review rather than by tooling. Until there is a `--cb-accent` token distinct from
`--bs-warning`, no probe can tell a brand accent from a misused warning colour.

**The good news, measured:** `btn-warning` appears **zero times** in all 372 views. The standing
rule against amber action buttons is being kept. Runtime found 13 amber elements across four
screens, and every one is a status indicator — progress bars, a dot, a 4px rail — not an action.

## 4. 77% of views paint with literal hex codes

**Rule:** `crossbuy-brand.css` is the token layer; screens consume tokens.
**Measured:** **285 of 372 views** contain at least one literal `#rrggbb`.

The more interesting split:

| | Colours | Uses |
|---|---:|---:|
| Top-40 colours that **are** token values, typed out by hand | 11 | **1,486** |
| Top-40 colours that are **not** any token | 29 | 638 |

```
#0E4A9E   758 uses   =  --cb-brand-ink        typed literally
#E7F0FF   270        =  --cb-brand-surface    typed literally
#145DBF   161        =  --bs-link-color       typed literally
#F3F7FF   104        =  --cb-blue-25          typed literally
```

**This is the good news and the bad news in one number.** The product *looks* on-brand because
developers reached for the right colours — but they hardcoded them, so a change to
`--cb-brand-ink` would move 0 of those 758 uses. The token layer has authority in the stylesheet
and none in the views.

The 638 off-token uses are the drift proper: a grey ramp (`#EEF1F6`, `#F2F4F7`, `#E4E6EF`,
`#475467`, `#7E8299`, `#99A1B7`, `#8B94AD`, `#94A3B8`, `#64748B`, `#A1A5B7` …) that exists in no
token, plus near-misses on the semantics — `#D92D20` beside `--bs-danger #EF4444`, `#12B76A`
beside `--bs-success #16A34A`, `#B96A00` beside `--cb-warning-strong #B45309`.

### The worst offenders

| View | Literal hex | Distinct | Token refs |
|---|---:|---:|---:|
| `Admin/Polices.cshtml` | **843** | **172** | 1 |
| `Admin/EmployeeData.cshtml` | 157 | 65 | 0 |
| `Admin/AdministrativeStructure.cshtml` | 103 | 41 | 0 |
| `Accounting/MovementSummary.cshtml` | 71 | 14 | 0 |
| `Inventory/WarehouseSections.cshtml` | 44 | 38 | 0 |
| `Account/Login.cshtml` | 41 | 34 | **25** |
| `Pos/Reservations.cshtml` | 40 | 21 | 0 |
| `Inventory/ManufDashboard.cshtml` | 39 | 13 | 0 |
| `Accounting/YearEndClose.cshtml` | 35 | 4 | 0 |
| `Portal/Choose.cshtml` | 34 | 23 | 0 |

`Admin/Polices.cshtml` alone holds **172 distinct colours** — more than the entire token layer's
114 — with a single token reference. One screen contains a larger palette than the product does.

The sign-in screen is the counter-example: 34 distinct colours, but **25 token references** — the
only view that meaningfully consumes the system.

## 5. A third and fourth colour system

Beyond the brand scale and the semantics:

**Module colours** — `Portal/Choose.cshtml` assigns a colour per system, and the sign-in chips do
the same through the `icon-*.png` assets:

```
#10A36F Administrative   #1F6FEB Inventory      #7C5CFC Employees/Tasks
#0E86B8 Accounting       #F0A020 Reports        #E2445C CRM/Support
#C0399B Calendar         #0E9F84 Projects       #E0518F Hypermarket
```

Twenty-three distinct hex values in that one file, of which **exactly one** (`#0E4A9E`) is a brand
token. Two are ambers (`#F0A020`, `#F5A623`) that are neither the brand accent nor the warning
value.

A module colour system is a legitimate idea — but it is undeclared, it exists in two places
(a Razor file and a set of PNGs), and it cannot be changed in one edit.

**Storefront assets** are a separate logo set at a different aspect ratio (215×66 against the
backend's 176×34), duplicated across `wwwroot/assets/` and `wwwroot/assets-en/`. Two logo systems,
four copies.

## 6. Inter is loaded from the internet

**Measured, both capture runs:**
```
net::ERR_ABORTED  https://fonts.gstatic.com/s/inter/v20/…woff2
```

Cairo is bundled four ways (static TTFs, embedded resources, and remote) and survived. **Inter is
remote-only and failed.** For an on-premise ERP — behind a corporate firewall, on a site with no
internet, in a country where `fonts.gstatic.com` is blocked — half the typography is not
guaranteed.

The product already knows the fix: bundle Inter the way Cairo is bundled.

There is a second consequence: every page load reports the user's IP to Google. For a product
handling payroll and financial data, that is a privacy question worth an explicit answer,
especially given how carefully the AI layer governs egress (document 07).

## 7. Five layouts serve fourteen modules

**Measured:** a user in Tasks is shown **169 sidebar links**, 161 of which belong to Accounting
and Inventory. CRM users navigate the Accounting menu (161 links). The Workspace shows 86.

Beyond the wrong title (§1), the practical cost is that **the shell does not tell a user where
they are**. A salesperson in CRM sees Payroll, Fixed assets, VAT returns and Year-end close in
their sidebar.

## 8. Smaller findings, measured

| Finding | Evidence |
|---|---|
| **`/Module` is a 404.** The default route has no `action=Index`. Only 7 controllers have a bare-name route. Any printed or emailed `/Accounting` link is dead. | Runtime: 4 × 404 |
| **A fourth tagline is baked into a photograph.** The mug in the sign-in artwork reads *"Smarter Business Together"* — unlocalisable, uneditable, and not one of the three tagline resources. | `01-login.png` |
| **A ® with no registrant.** The wordmark carries a registered-trademark symbol; no entity, jurisdiction or number appears anywhere in the repository or configuration. | `01-login.png`, config |
| **No publisher identity at all.** No company name, address, VAT number or copyright holder in any configuration file. | `appsettings.json` |
| **`favicon.png` is 1024×1158** — not square, 253 KB. A browser will letterbox or squash it. | `asset-manifest.csv` |
| **102 of 171 catalogued assets have no reference** found in the view/controller/CSS/JS tree. A list to audit, not to delete — some are referenced by paths this scan did not reconstruct. | `asset-manifest.csv` |
| **Two shared partials have no English or French resource** — `_NotificationBell` and `_EntityConversation` render on every page, so the bell and the comment box are English throughout the French UI. | Document 10 §3 |
| **The default culture is English** for an Arabic-first product whose only complete language is Arabic. | Runtime: every screen `lang=en` before any switch |
| **SSO ships and is undocumented.** Microsoft and Google sign-in are configured and live; they appear in no menu, no design document and no product description. | `01-login.png`, `Program.cs:712,724` |
| **`_LayoutWorkspace.cshtml` is dead** — zero references, and the only user-visible `CrossBusiness`. | grep |
| **Scheduled report delivery is built but not commissioned** — the worker is off by design, the mail sender is `NullReportMailSender`. | Document 06 §5 |
| **Inventory emits no business events** — 52 entity sets, 34 screens, 0 of 47 event types. Users will hear about an invoice and not about a goods receipt. | Document 04 §3 |

---

## 9. What this list does *not* say

It would be dishonest to present the above without the counterweight, because the same measurements
produced both.

- **The token layer is better than most commercial design systems.** 129 tokens, every role chosen
  against a *computed* contrast ratio, the reasoning written beside the value, roles split by
  function, framework tokens aliased rather than forked, dark mode guarded against leakage. It was
  independently re-measured for this discovery and its own claims held.
- **The amber rule is being kept.** Zero `btn-warning` in 372 views.
- **The view layer's localisation is clean.** Zero Arabic localizer keys; 25 hardcoded Arabic
  literals in 372 views, nine of them legitimate.
- **Twenty screens rendered with zero console errors.**
- **The AI egress boundary is exemplary** (document 07) — and it is the proof that this team *can*
  enforce a rule when it decides to. The egress matrix is enforced by a chokepoint and asserted by
  test. The colour system is enforced by nothing.

**That contrast is the actual finding of this document.** CrossBuy does not have a standards
problem; it has an *enforcement* problem, and only in the visual layer. The repository already
contains the tooling to fix it — `tools/ui-conformance/` holds `btn-variant-audit.mjs`,
`button-contrast-probe.mjs`, `alert-variant-audit.mjs`. What is missing is a check that fails a
build when a view types a colour instead of naming one.

---

## 10. If only three things were done

1. **Give every screen its own title.** Replace `@Localizer["PageTitle"]` with
   `@ViewData["Title"] — CrossBuy` in the three layouts, and decide whether the product is
   *CrossBuy* or *CrossBusiness*. Cost: one afternoon. Effect: the product stops misnaming itself
   in every bookmark and screenshot.
2. **Add a `--cb-accent` token and outline the logo's wordmark.** Separating the brand amber from
   the warning amber makes the amber rule *checkable*; outlining the SVG text makes the logo
   render the same everywhere. Cost: small. Effect: the identity becomes enforceable.
3. **Bundle Inter, and add a hardcoded-hex check to CI.** The first removes an external dependency
   and a privacy question from every page load. The second stops the drift in §4 from growing —
   starting with a ratchet on new views rather than a rewrite of the 285 existing ones.
