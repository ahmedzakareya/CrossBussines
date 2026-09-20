# 01 — Baseline and method

Everything in this package refers to the state described here. Read it first; nothing later is
meaningful without it.

---

## 1. The subject

| | |
|---|---|
| Repository root | `C:\CrossBuy\CrossBuy` |
| Application project | `C:\CrossBuy\CrossBuy\CrossBuy` |
| Solution | `CrossBuy.sln` — 4 projects: `CrossBuy`, `CrossBuy.Tests`, `CrossBuy.Analyzers`, `CrossBuy.Analyzers.Tests` |
| Target framework | `net8.0` (ASP.NET Core 8 MVC) |
| SDK present on the machine | `dotnet 10.0.401` — building a net8.0 target |
| UI kit | Metronic 8.3.3, served from `wwwroot/Backend-assets` |

Top-level directories beyond the solution:

```
crossbuy_ai/       FastAPI + scikit-learn service (Python)      — see 07
crossbuy_mobile/   Flutter client                                — not exercised in this discovery
deploy/            SQL and deployment material
docs/              48 design and analysis documents (this package lives here)
engineering/       engineering process material
governance/        ownership registries and check scripts
tools/             probes and conformance harnesses (authz-probe, ui-conformance, …)
```

---

## 2. The commit this describes — and the fact that it moved

```
branch   reporting/studio-gap-closure
HEAD at start of discovery
         0e9fcc79e6dd9e653c7a983dfa3f8beda3f75db3
         2026-09-20 09:31:13 +0300
         "Report Studio: the designer offers the series field and the time bucket"

HEAD at end of discovery
         2020ee5aeb5a710658b71a9d02c90a5014fa461e
         2026-09-20 10:05:28 +0300
         "Reporting tests: assert what the platform now claims, and fix one that never tested anything"

total commits on this branch   245
```

**`HEAD` advanced by one commit, 34 minutes into the work.** This repository is worked by more
than one process at a time, and the discovery ran against a moving target. The commit is stated
rather than smoothed over because a reader re-running any command in document 13 may get a
different answer, and needs to know why.

The two commits differ only in the reporting test project. No figure in this package is drawn
from `CrossBuy.Tests`, so nothing here changes between them.

## 3. The working tree was not clean

```
74 uncommitted entries
   52 modified  ( M)
   22 untracked (??)
```

These belong to work in progress that is not part of this discovery — including, by the end,
this package's own files. The consequence for a reader:

> **A file read from the working tree may not match the commit named above.** Where a finding
> depends on an exact file state, document 13 names the file and the reader can diff it against
> `HEAD` themselves.

Findings about *structure* (how many controllers, which modules exist, what the token layer
declares) are stable across this difference. Findings about the *exact contents of one view*
are the ones to re-check.

---

## 4. What was measured, and how

### 4.1 Repository extraction

Three Python scripts walked the tree and wrote the data files. They read; they never write outside
`docs/business-brand-discovery/`. Their full source and invocation are in document 13.

| Script | Produces | Method |
|---|---|---|
| `extract_brand.py` | `brand-tokens.json`, `asset-manifest.csv` | Parses every `{ … }` block of `crossbuy-brand.css` so a dark-theme override is never reported as a light value; **computes** WCAG relative luminance and contrast rather than trusting the comments; hashes every asset and searches the view/controller/CSS tree for references to it |
| `extract_catalog.py` | `business-catalog.json` | Parses `MainMenu.cs` into modules → categories → items; counts actions per controller; enumerates `DbSet<>`; collects report codes and permission keys; counts literal hex codes per view against the token values |
| `shots.mjs` / `probe2.mjs` | `screenshots/` + two JSON logs | See 4.2 |

### 4.2 Runtime observation

The application was started on this machine and observed. It was **not** modified.

```
dotnet run --launch-profile http     →   http://localhost:5268
environment                              Development
database                                 localhost / CrossBuyDev   (development, never live)
sign-in                                  admin  (local test account)
driver                                   headless Chromium via the browser-automation harness
```

The capture scripts navigate and read. The only form either of them submits is the sign-in form.
No record was created, edited or deleted.

At each screen the probe recorded, from the page itself: HTTP status, final URL after redirects,
`<title>`, `lang`, `dir`, the computed value of five brand custom properties, the resolved body
font stack, whether a Cairo face had actually loaded, the sidebar link count, and every element
painted with `rgb(245,158,11)`. Those measurements are in `screenshots/capture-log.json` and
`screenshots/probe-rtl-and-amber.json`, and they are what documents 11 and 12 cite.

### 4.3 What was deliberately not done

- **No live database.** The production server was not contacted. Every data-dependent observation
  is from the development database and is labelled as such.
- **No mobile client.** `crossbuy_mobile/` was catalogued as present and not examined.
- **No AI service run.** `crossbuy_ai/` was read, not started. Document 07 therefore describes the
  configured and coded behaviour, and says so.
- **No write of any kind** to the application, its configuration or its data.

---

## 5. Measured scale

Every figure below comes from `business-catalog.json`.

| | Count |
|---|---|
| Module menus | 10 |
| Menu destinations | 186 |
| Controllers | 57 — 42 MVC + 15 API |
| Controller actions | 1,203 |
| Razor views | 372, across 36 folders |
| Shared layouts | 11 |
| Business-logic services (`BL/*.cs`) | 134, plus 13 sub-namespaces |
| Persisted entity sets | 265, in a single `DbContext` |
| Registered report codes | 41 |
| Resource files | 891, over 308 translation units |
| Supported cultures | 3 — `ar`, `en`, `fr` |
| Design tokens | 129 names / 149 declarations |

`BL` sub-namespaces: `Approvals`, `Comm`, `Communication`, `Construction`, `Documents`, `Hr`,
`ModulePermissions`, `Platform`, `Portal`, `Reporting`, `TasksCalendar`, `Uat`, `Workspace`.

> **On the 265 entities in one `DbContext`.** This is stated as a measurement, not a criticism.
> It tells a reader that the product treats itself as *one* business system rather than a
> federation — the same conclusion the shared `Platform` namespace and the single event stream
> point at. See 04.

---

## 6. How to challenge anything here

1. Check out `2020ee5a`.
2. Re-run the command from document 13 next to the claim.
3. If the answer differs, the working tree (§3) or the commit (§2) is the likely reason — check
   both before assuming the claim was wrong.

Runtime claims need the application running as in §4.2 and cannot be reproduced from the
repository alone. They are marked **Runtime** wherever they appear.
