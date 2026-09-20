# 01 — Baseline and coverage

What was examined, in what state, by what means — and what was not.

---

## 1. Subject and date

| | |
|---|---|
| Discovery date | **20 September 2026** |
| Repository | `C:\CrossBuy\CrossBuy` |
| Application project | `C:\CrossBuy\CrossBuy\CrossBuy` (ASP.NET Core 8 MVC, `net8.0`) |
| Branch | `reporting/studio-gap-closure` |
| Commits on branch | 247 |

### 1.1 `HEAD` moved three times during the discovery

```
ccc6a34977ee48b861db1dd9ee98f9e427b1b0d7   10:49:50 +0300   "Reporting: an Icon element, so a KPI card can say what kind of number it is"
13c8d9844f3b4175bb35c5be6df88dae8ef43021   11:07:21 +0300   "Reporting: a summary can say how it compares with the previous period"
```

(the discovery opened at `2020ee5a`, 10:05:28). Another process commits to this repository while
work is in progress. Every figure in this package was taken from a working tree that was current
at the time of its command; **re-running a command may produce a different number, and that is the
reason.** All three commits touch the reporting layer only.

### 1.2 The working tree was not clean

```
73–78 uncommitted entries at various points during the discovery
```

These belong to other work in progress. Nothing in this package modified them, and the discovery's
own files are the only addition.

### 1.3 Application version: **not available**

`CrossBuy.csproj` declares no `<Version>`, `<AssemblyVersion>`, `<FileVersion>`,
`<InformationalVersion>`, `<Product>`, `<Company>`, `<Authors>` or `<Copyright>`. There is no
`AssemblyInfo.cs` and no "About" screen.

> **Consequence for the manuals: there is no version number to print on the cover, and no publisher
> to credit.** A manual normally carries both. Something must be decided — a release number, a
> build date, or an explicit "internal build" statement — before either edition is published.

---

## 2. Runtime used

Screens were observed against **an instance that was already running on this machine** — the
owner's own IIS Express session — rather than by starting a competing one:

```
https://localhost:44368        (also http://localhost:54328, which 307-redirects to it)
process                        iisexpress, started 10:32
environment                    Development
database                       localhost \ CrossBuyDev
```

The development database was confirmed by `SELECT @@SERVERNAME, DB_NAME()` →
`DESKTOP-K8SA5SV | CrossBuyDev`. **The production server was never contacted.**

Two facts about this runtime that affect how its evidence should be read:

- **Browser Link is active** (a `browserLinkSignalR` request appears in every capture). It is the
  Visual Studio live-reload channel. It injects no visible UI, so screenshots are clean, but it
  confirms these are development-session captures.
- **A second application instance was started and then stopped** early in the discovery. It
  reported *"another process holds the background-worker lease"* and stood by — which is the
  product behaving correctly, and is why the owner's instance was used instead.

### 2.1 Configuration relevant to visible features

Read from `appsettings.json`. **No secret, password, key, token or connection string is reproduced
here or anywhere in this package.**

| Setting | Value | What a user would notice |
|---|---|---|
| `AiService:DeploymentMode` | `LocalLoopback` | AI features answer from a local service; nothing is sent to a third party |
| `AiService:DestinationClass` | `Internal` | ditto |
| `Store:Enabled` | `true` | The public storefront is switched on |
| `Store:StoreCompanyId` | `1` | The anonymous catalogue shows one company only |
| `Smtp:Enabled` | `true` | Outgoing e-mail is configured |
| `Runtime:RequireSingleWorkerProcess` | `true` | Background jobs run in exactly one process |
| `Platform:EventDispatch` | batch 50, poll 15 s, 5 attempts | Notifications arrive within ~15 s, not instantly |
| `Jwt:ExpireHours` | `168` | A mobile session lasts 7 days |
| Supported cultures | `ar`, `en`, `fr` | Three languages in the switcher |

---

## 3. Account and role used

| | |
|---|---|
| Account | The built-in development administrator (`admin`) |
| Resolves to | Employee record ID 5 — the account the greeting on `/Portal/Choose` names |
| Identity roles present in the database | `SuperAdmin`, `Administrator`, `Admin`, `Auditor`, `PlatformOps` |
| Module role assignments present | Inventory 1, Accounting 1, CRM 5 |
| Platform grant rows (`PlatformRoleAssignments`) | **0** |

**Everything in this package was observed through one administrator session.** No second account
was created — creating one would have been a data change, which the brief forbids. The consequence
is stated plainly in document 03 and repeated here because it limits every runtime claim:

> **Viewing a screen as an administrator proves that the screen exists and renders. It proves
> nothing about what a warehouse keeper, a cashier or an employee sees.**

The zero platform grants are themselves a finding: with no rows in `PlatformRoleAssignments`, any
capability held behind a platform grant is closed on this install. That is why some HR actions
refuse even for an administrator (document 09).

---

## 4. Evidence labels used throughout this package

| Label | Means |
|---|---|
| **Repository verified** | Read from the source tree. Durable, re-checkable, and independent of data. |
| **Runtime viewed** | The screen was loaded in a browser, signed in, and its HTTP status, title, language, direction and control counts recorded. **It renders. Its buttons were not pressed.** |
| **Behaviour tested safely** | A non-mutating behaviour was exercised — a language switch, a navigation, a refusal. |
| **Not verified** | Neither read nor observed, or observed too shallowly to describe. |
| **Not implemented / disabled** | Found, and found to be absent, switched off, or unreachable. |

> **Viewing a screen does not prove that its actions work.** Of 1,475 catalogued actions, **zero**
> were clicked in a way that would change data. Every action in this package is
> repository-verified only.

---

## 5. What was examined

### 5.1 In code — complete passes

| Surface | Count | Output |
|---|---|---|
| Razor views | 372 files → **309 screens + 63 partials** | `screen-catalog.json`, `partials-catalog.json` |
| Controllers | 57 files → **1,220 routable actions** | `route-protection.csv` |
| Form fields | **1,305** | `field-action-catalog.json` |
| Actions (buttons, button-links) | **1,475** | `field-action-catalog.json` |
| Table columns | **1,399** | `screen-catalog.json` |
| Notices and alerts in views | **210** | `screen-catalog.json` |
| Empty / loading states | **24** | `screen-catalog.json` |
| Modal dialogs | **94** | `screen-catalog.json` |
| Localisation keys resolved in ar/en/fr | **4,333** | `bilingual-glossary.csv` |
| Report datasets / codes / declared columns | 15 files / **41 codes** / **305 columns** | `report-catalog.json` |
| User-facing messages | **206** | `messages-catalog.csv` |
| Role and action vocabularies | 14 vocabularies across 11 platform scopes | `role-permission-matrix.csv` |
| Business event types | **47** in 11 families | `reference/workflow-raw-evidence.json` |
| Status values | **52** distinct strings | `reference/workflow-raw-evidence.json` |

### 5.2 At runtime

| | |
|---|---|
| Routes attempted | **305**, in **two languages** |
| Screens captured | see `screenshot-manifest.csv` and document 08 |
| Behaviour tested safely | sign-in; culture switch `en` ⇄ `ar` with `<html lang>`/`dir` confirmed; navigation; one refusal (`/Account/AccessDenied`, HTTP 403) |
| Database reads | Role tables and one employee record (the owner's), for identity and role evidence only |

### 5.3 Not accessible, not attempted

| Area | Status | Why |
|---|---|---|
| Production database and install | **Not verified** | Out of scope by instruction |
| Every POST / command action (390 of them) | **Not verified** | Clicking them changes data |
| Any role other than administrator | **Not verified** | Creating a user is a data change |
| Mobile client (`crossbuy_mobile/`) | **Not verified** | Flutter app, not exercised |
| AI service (`crossbuy_ai/`) | **Not verified** | Read, not started |
| Screens requiring a record id (`/…/Details/{id}`) | **Partially verified** | Only reachable with an existing record; see document 08 |
| Report output files (PDF/XLSX/CSV) | **Not verified** | Generating one is an action |
| Scheduled report delivery | **Not implemented / disabled** | Worker off by design; `NullReportMailSender` registered |
| E-mail sending | **Not verified** | Configured, not exercised — sending is an action |

---

## 6. Coverage matrix

Per module. "Screens" is from the catalogue; "captured" is filled from `screenshot-manifest.csv`.
Workflow and role columns state the honest level reached.

| Module | Screens | Actions | Fields | Code | Runtime | Roles | Workflows |
|---|---:|---:|---:|---|---|---|---|
| Inventory | 67 | high | high | ✅ complete | ✅ routes attempted | ⚠ declared only | ✅ documented |
| Accounting | 61 | high | high | ✅ | ✅ | ⚠ declared only | ✅ |
| Admin (HR) | 30 | high | high | ✅ | ✅ | ⚠ declared only | ✅ |
| CRM | 25 | med | med | ✅ | ✅ | ⚠ declared only | ✅ |
| Project | 17 | med | med | ✅ | ✅ | ⚠ declared only | ✅ |
| Pos (Restaurant) | 16 | med | med | ✅ | ✅ | ❌ no vocabulary found | ✅ |
| People (self-service) | 12 | med | med | ✅ | ✅ | ⚠ declared only | ⚠ partial |
| Hyper | 9 | low | low | ✅ | ✅ | ❌ | ⚠ partial |
| Tasks | 9 | med | med | ✅ | ✅ | ⚠ declared only | ✅ |
| Account (sign-in, profile) | 6 | low | med | ✅ | ✅ | n/a | ✅ |
| ClientPortal | 5 | low | low | ✅ | ✅ | ❌ | ⚠ partial |
| PosApp (cashier/kitchen) | 5 | med | low | ✅ | ⚠ operator screens | ❌ | ⚠ partial |
| Service (companies/branches) | 5 | low | med | ✅ | ✅ | ⚠ | ✅ |
| Workspace | 5 | low | low | ✅ | ✅ | n/a | ✅ |
| Currency | 4 | low | med | ✅ | ✅ | ⚠ | ✅ |
| Reports / Studio | 4 | med | med | ✅ | ✅ | ✅ 7 keys | ✅ |
| Calendar | 3 | low | low | ✅ | ✅ | ✅ 2 roles | ⚠ partial |
| Comm (e-mail) | 3 | low | med | ✅ | ✅ | ✅ 3 roles | ⚠ partial |
| Home / Store | 5 | low | low | ✅ | ✅ | n/a | ⚠ partial |
| Brand, Roster, Documents, FileManager, Announcements, Approvals, Notifications, Chat, BusinessEventMonitor, EmployeeOnboarding, ProjectCloseout, RestaurantIntelligence, Portal | 13 | low | low | ✅ | ✅ | ⚠ | ⚠ partial |
| **Total** | **309** | **1,475** | **1,305** | | | | |

Legend — ✅ sufficient to write instructions · ⚠ partial, gaps named in document 12 ·
❌ not established.

The machine-readable version, with a row per screen, is `coverage-and-gaps.csv`.

---

## 7. The honest summary of this baseline

**Strong enough to write from:** every screen, field, action, column, message and localisation key
is inventoried from source, with exact bilingual text. That is the bulk of a manual.

**Not strong enough yet, and named rather than hidden:**

1. **No action was ever performed.** Every "click Save and the invoice posts" sentence in the
   eventual manual rests on reading code, not on watching it happen.
2. **One role was used.** The entire access chapter is declared, not observed.
3. **No version and no publisher** exist to put on a cover.
4. **Record-scoped screens** (anything needing an id) were only reachable where development data
   happened to supply one.

Document 12 turns these into a specific list of what must be done before a complete manual can be
written.
