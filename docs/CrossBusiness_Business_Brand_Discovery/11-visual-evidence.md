# 11 — Visual evidence

Twenty-four screens captured from a running instance, each with the measurements taken at the
moment of capture. This document says what each one *proved* — the images are in `screenshots/`,
the raw measurements in `screenshots/capture-log.json` and `screenshots/probe-rtl-and-amber.json`.

---

## 1. Capture conditions

```
instance     http://localhost:5268      ASPNETCORE_ENVIRONMENT=Development
database     localhost / CrossBuyDev    (development — never the live server)
account      admin                      signed in through the real sign-in form
viewport     1280 × 720, headless Chromium
date         20 September 2026
```

Only the sign-in form was submitted. Every other interaction was navigation. Nothing was created,
edited or deleted.

**All 20 primary screens returned HTTP 200 and rendered non-trivial content.** No screen was
broken at capture time.

## 2. The catalogue

| File | Screen | What it evidences |
|---|---|---|
| `01-login.png` | `/Account/Login` | The brand, in full — see §3 |
| `01-login-ar.png` | same, `culture=ar` | RTL sign-in; layout mirrors correctly |
| `02-portal-choose.png` | `/Portal/Choose` | The product's own self-description: 6 portals + 6 systems |
| `03-workspace.png` | `/Workspace/Index` | The cross-module home — titled *"Inventory System"* |
| `04-inventory-dash.png` | `/Inventory/Index` | Inventory shell, 120 sidebar links, 4 amber elements |
| `04-inventory-dash-ar.png` | same, Arabic | `dir=rtl`, `lang=ar`, Cairo loaded, RTL bundle served |
| `05-inventory-items.png` | `/Inventory/Items` | The item master — the busiest list in the product |
| `06-accounting-dash.png` | `/Accounting/Index` | Accounting shell, 161 sidebar links |
| `07-trial-balance.png` | `/Accounting/TrialBalance` | **The stated visual authority** — see §4 |
| `07-trial-balance-ar.png` | same, Arabic | The authority in RTL |
| `08-chart-of-accounts.png` | `/Accounting/ChartOfAccounts` | Tree list pattern |
| `09-crm-dashboard.png` | `/Crm/Index` | CRM wearing the Accounting shell — 161 Accounting links |
| `10-crm-pipeline.png` | `/Crm/Pipeline` | Kanban pattern; 1 amber element |
| `11-admin-hr.png` | `/Admin/Index` | HR dashboard — 6 amber elements, the most anywhere |
| `11-admin-hr-ar.png` | same, Arabic | |
| `12-tasks.png` | `/Tasks/Index` | Tasks on the Inventory shell (169 links — the most) |
| `13-projects.png` | `/Project/Dashboard` | Construction dashboard on `_LayoutBackend` |
| `14-restaurant.png` | `/Pos/Dashboard` | Restaurant dashboard |
| `15-hyper.png` | `/Hyper/Dashboard` | Supermarket dashboard |
| `16-reports-center.png` | `/Reports/Index` | The report catalogue |
| `17-report-studio.png` | `/Reports/Studio` | The layout designer |
| `18-calendar.png` | `/Calendar/Index` | Calendar |
| `19-chat.png` | `/Chat/Index` | Internal chat |
| `20-manufacturing.png` | `/Inventory/ManufDashboard` | The only *"Warehouse System"* title |

> **Three captures were deleted, and why.** The run included an "English pass" over three screens
> to sit beside the Arabic one. The resulting files were **byte-identical** (SHA-256) to the
> originals, because the default culture served to a fresh session is already English — which is
> itself the finding (document 10 §5). Keeping three duplicate PNGs would have inflated the
> evidence count without adding evidence, so they were removed and the fact recorded here.

---

## 3. What `01-login.png` shows

The sign-in screen is the only place the brand is presented deliberately, and it is well made.

**The logo as actually rendered** is a large full-colour lockup — a blue-and-amber interlocking
mark with a gradient, then the wordmark: **"Cross" in navy, "Buy" in amber**, followed by a
**® registered-trademark symbol**. This is the PNG lockup, not the `crossbuy-logo.svg` described
in document 08 §2.2 — so the live-text font problem in that SVG affects the *backend header*, not
this screen.

> The ® is a claim of a registered mark. **No registrant, entity, jurisdiction or registration
> number appears anywhere in the repository or configuration.** Worth resolving before the mark is
> used in marketing.

**The two-tone split is a genuine brand device**, used three times on one screen:

```
Cross|Buy          navy | amber      the wordmark
One Platform. | Endless Possibilities.     near-black | brand blue
Welcome | Back     near-black | brand blue
```

That is a deliberate, repeatable identity idea, and it is the most distinctive thing in the
product's visual language. It is not written down anywhere as a rule.

**The eight module chips** are rendered from the `icon-*.png` assets, each in its own colour:
Accounting (blue bar chart), Inventory (green cube), CRM (purple people), POS (amber cart),
Manufacturing (blue factory), Projects (teal folder), HR (pink people), Reports (blue/green
arrows). **This is a third colour system** — neither the brand scale nor the semantic set
(document 12 §3).

**Single sign-on is live on this install.** "or continue with — Microsoft · Google". This is not a
painted door: the view renders the block from the schemes the application actually has
configured, and the source says so —

> *"Configure Microsoft or Google in settings and the buttons appear; configure neither and this
> whole block does not exist — which is the difference between an offer and a painted door."*

`Program.cs` registers `AddMicrosoftAccount` and `AddGoogle` conditionally; `AccountController`
has `ExternalLogin` and `ExternalLoginCallback`. **SSO is a real, shipped product capability that
appears in no module menu and no design document.**

**A fourth tagline is baked into a photograph.** The coffee mug in the background artwork reads
*"CrossBuy — Smarter Business Together."* It is pixels, not a resource string: it cannot be
translated, cannot be changed without a new photograph, and contradicts the three taglines that
*are* localised (document 02 §2).

Other measured details: the language switcher sits inside the sign-in card (`EN ⌄`); the Sign In
button is brand-fill blue with white text and an arrow; the tagline is set as a rule-flanked
letterspaced eyebrow; the footline `BUSINESS WITHOUT BOUNDARIES` carries a small amber rule above
it — amber used as brand accent, exactly as document 08 §2.1 predicted.

---

## 4. What `07-trial-balance.png` shows

The stated visual authority, and it earns the title.

- **The accounting identity is shown, not claimed.** Three outlined tiles —
  `Debit 113,027,021.50` · `Credit 113,027,021.50` · `Difference 0.00` — at display size, side by
  side, so the reader verifies the balance by looking.
- **Then it is stated in words**, in a dashed-border notice tinted `--cb-brand-surface`:
  *"The trial balance is balanced — Total debit balances match total credit balances exactly with
  no differences."* with a solid brand-fill badge reading **✓ Matched**.
  This is the **dashed-notice-coloured-by-kind** pattern from document 09 §4.3, caught in the act.
- The header carries the CrossBuy logo — **and the amber "Buy" is clearly visible in the backend
  chrome**, confirming that amber is identity, not an accident.
- One action, top right: **Print** — the Report Studio path (document 06 §7), not a browser print.
- **The sidebar proves the CRM finding**: Platform · Reporting · General · Currencies & FX ·
  Receivables · **CRM** · Payables · Banks & cash · Payroll · Tax · Fixed assets · Financial
  reports. CRM sits as a peer category inside the Accounting menu (document 03 §4).
- A right-hand icon rail (calendar, contacts, tasks, scheduling) — a second navigation axis that
  appears in no menu model.

Measured on this screen: `--cb-brand #1877F2`, `--bs-primary #1877F2`, 161 sidebar links,
**zero amber elements**.

---

## 5. Cross-screen measurements

### 5.1 Page titles — seven names across twenty screens

Recorded in full in document 02 §5. The measurement: `<title>` varied by *layout*, never by
screen. Seven distinct product names.

### 5.2 Sidebar size

| Screen | Sidebar links |
|---|---:|
| `/Tasks/Index` | **169** |
| Accounting, CRM (all) | 161 |
| Inventory, Reports, Studio, Chat | 120 |
| `/Admin/Index`, `/Pos/Dashboard` | 118 |
| `/Inventory/ManufDashboard` | 109 |
| `/Project/Dashboard` | 97 |
| `/Hyper/Dashboard` | 94 |
| `/Calendar/Index` | 89 |
| `/Workspace/Index` | 86 |

A user opening Tasks is offered 169 links, 161 of which belong to Accounting and Inventory. That
is not a menu; it is an index.

### 5.3 Amber, located precisely

Thirteen elements across four screens were painted `rgb(245,158,11)`. Every one was identified:

| Screen | Count | What they are |
|---|---:|---|
| `/Admin/Index` | 6 | 5 × a 4px status rail (`position-absolute start-0 top-0 w-4px h-100`) + 1 progress bar |
| `/Inventory/Index` | 4 | `progress-bar bg-warning` |
| `/Accounting/Index` | 2 | `progress-bar bg-warning` |
| `/Crm/Pipeline` | 1 | `bullet bullet-dot bg-warning` |

**Not one is an action button.** Confirmed against the source: `btn-warning` appears **zero
times** in all 372 views. The standing rule against amber action buttons is being kept. What
remains is amber as a *status* colour on progress bars, a dot and a rail — which is the semantic
use the token layer defines, and is a separate question from the ban.

### 5.4 Fonts

Every screen resolved `Cairo` first. On the Arabic pass,
`document.fonts.check('600 16px Cairo')` returned `true` on all four screens, and the loaded
families were `Cairo`, `Inter`, `keenicons-outline`.

**One font failed to load, on both runs:**

```
net::ERR_ABORTED  https://fonts.gstatic.com/s/inter/v20/UcC73FwrK3iLTeHuS_nVMrMxCp50SjIa1ZL7.woff2
```

An Inter weight, from Google. Cairo is bundled locally and survived; Inter is remote-only and did
not. Document 08 §3.3.

### 5.5 Console health

Across the full second run: **zero console errors**, two failed requests — the Inter font above,
and one `404` on `/api/reports/studio/assets/2` while the Studio loaded a template's assets.

The first run additionally produced four `404`s from the capture script's own bad URLs
(`/Inventory`, `/Accounting`, `/Crm`, `/Admin` — no `action=Index` default; document 05 §0). Those
were the script's error, and they are recorded because they found a real routing fact.

**A product that renders twenty screens with no console errors is in good runtime health.**
