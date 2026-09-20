# CrossBuy — Business & Brand Discovery

**Discovery date:** 20 September 2026
**Subject:** the CrossBuy application at `C:\CrossBuy\CrossBuy`
**Method:** repository extraction plus observation of a running instance. Nothing in the
application, its configuration or its database was changed.

---

## What this package is

A discovery record, not a proposal. It answers three questions and keeps them separate:

1. **What business does this product do?** — modules 01–06.
2. **What does it look like, and by what rules?** — 07–09, 11.
3. **Where do the stated rules and the built product disagree?** — 10, 12.

Every number in these documents came from a command, and document 13 lists the commands.
Where a claim could not be verified it is written as unverified, not rounded up.

---

## The two kinds of evidence, and why the distinction matters

| Marked | Means |
|---|---|
| **Repository evidence** | Read from the source tree at the pinned commit. Durable and re-checkable. |
| **Runtime evidence** | Measured in a browser against a running instance on `http://localhost:5268`, signed in, on the development database. Reflects that one install, that data and that culture. |

A thing can be true in the repository and false at runtime. Both appear in this package. The
clearest example: the source declares ten module menus, and the running product serves every one
of them — but the running product also titles the Workspace screen *"Inventory System"*, which no
reading of the source would have suggested. Neither kind of evidence is sufficient alone.

---

## The documents

| # | Document | Answers |
|---|---|---|
| 01 | [Baseline and method](01-baseline-and-method.md) | What exactly was examined, at which commit, in what state |
| 02 | [Product and business identity](02-product-and-business-identity.md) | What the product calls itself and promises |
| 03 | [Module catalogue](03-module-catalogue.md) | The ten modules and all 186 destinations |
| 04 | [Business domain model](04-business-domain-model.md) | The 265 things the product stores |
| 05 | [End-to-end journeys](05-end-to-end-journeys.md) | How work actually moves through it |
| 06 | [Reporting and KPIs](06-reporting-and-kpis.md) | What it measures and how a report is produced |
| 07 | [AI capabilities audit](07-ai-capabilities-audit.md) | What is AI, what is statistics, and what leaves the building |
| 08 | [Visual identity](08-visual-identity.md) | Colour, type, logo — the token layer as authored |
| 09 | [Design system and components](09-design-system-and-components.md) | The shell, the layouts, the recurring patterns |
| 10 | [Localisation and bilingual behaviour](10-localisation-and-bilingual.md) | Three languages, two directions, measured coverage |
| 11 | [Visual evidence](11-visual-evidence.md) | 27 captured screens and what each one proved |
| 12 | [Brand drift and risks](12-brand-drift-and-risks.md) | Where the product departs from its own rules |
| 13 | [Evidence register](13-evidence-register.md) | Every command, file and measurement behind the above |

## The data files

| File | Contents |
|---|---|
| `business-catalog.json` | Modules, menu tree, 57 controllers, 372 views, 265 entities, 41 report codes, localisation and drift metrics |
| `brand-tokens.json` | All 129 design tokens with **computed** WCAG contrast for every colour, dark-theme overrides, gradients |
| `asset-manifest.csv` | 171 brand marks and font files with size, hash and where each is referenced |
| `assets/` | Copies of the logo, favicon and font files |
| `screenshots/` | 24 PNGs plus `capture-log.json` and `probe-rtl-and-amber.json` — the measurements taken at the moment each screen was photographed |
| `scripts/` | The four extraction scripts exactly as run, so every number can be regenerated |

---

## The three findings that matter most

**1. The product is one platform wearing seven names.** The wordmark is *CrossBuy* and the tagline
is *"THE CROSS-BUSINESS PLATFORM"*, in three languages, consistently. But the browser tab says
`CrossBuy`, `CrossBuy Admin`, `Inventory System - CrossBuy`, `Warehouse System - CrossBuy`,
`Accounting System - CrossBuy`, `CrossBuy Hyper`, `CrossBuy POS` — and the Workspace layout says
`CrossBusiness`. The title comes from the *layout*, not the screen, so the Workspace, Tasks,
Reports and Calendar all identify themselves as "Inventory System". See 02 and 12.

**2. The colour system is excellent and unenforced.** `crossbuy-brand.css` is a genuine design
system — 129 tokens, every role chosen against a measured contrast ratio, the reasoning written
into the file. Then 285 of 372 views paint with literal hex codes anyway. The four most-used
colours in the view tree *are* token values, typed out by hand 1,293 times between them. See 08
and 12.

**3. Nothing leaves the building.** The AI layer has a real governance boundary — a
classification × destination matrix that refuses personal data everywhere and free text to any
external processor — and today's configuration routes everything to a local service on
`localhost:8000`. The Anthropic client exists; no approved external provider does. See 07.

---

## Scope and honesty notes

- The working tree was **not clean**: 74 uncommitted entries belonging to other work. Findings
  are pinned to a commit, and 01 says which.
- `HEAD` **moved during the discovery**. Both commits are recorded in 01.
- Runtime evidence is from **one development install with development data**. Row counts,
  dashboard figures and anything else data-dependent are not generalised.
- No feature was implemented, no behaviour changed, no database written, nothing committed or
  deployed.
