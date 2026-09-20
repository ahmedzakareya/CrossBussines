# CrossBuy — User Manual Discovery Package

**Discovery date:** 20 September 2026
**Subject:** the CrossBuy application at `C:\CrossBuy\CrossBuy`
**Purpose:** collect everything needed to write two fully illustrated user manuals — Arabic (RTL,
Cairo) and English (LTR) — under the CrossBuy visual identity, with the owner's name and portrait
on the covers.

> **This package is evidence collection, not the manuals.** No manual chapter is written here.
> Nothing in the application, its configuration or its database was changed; nothing was
> committed or deployed.

---

## Read these three things first

**1. No action was ever performed.** 1,475 actions were catalogued from source and **none was
clicked**. Screens were opened and read. Nothing was created, posted, approved, cancelled or
deleted. Every "press Save and it posts" statement in this package is what the code and the
screen's own text say — not what was observed.

**2. One role was used.** A single development administrator session. The access chapter
describes the model *as declared*; it cannot describe what a warehouse keeper or a cashier sees.

**3. The product declares no version and no publisher.** No `<Version>`, `<Company>`, `<Authors>`
or `<Copyright>` anywhere, and no "About" screen. **Neither cover can carry a version number or a
publisher until someone decides one.**

---

## The documents

| # | Document | Answers |
|---|---|---|
| 01 | [Baseline and coverage](01-baseline-and-coverage.md) | What was examined, in what state, and the coverage matrix |
| 02 | [Author, portrait and brand](02-author-portrait-and-brand.md) | The owner's name and portrait, how each was found, and the brand kit |
| 03 | [Roles and access](03-roles-and-access.md) | The three permission layers, every declared role and action |
| 04 | [Screen inventory](04-screen-inventory.md) | All 309 screens, with exact bilingual titles |
| 05 | [Field and action dictionary](05-field-and-action-dictionary.md) | 1,305 fields and 1,475 actions |
| 06 | [Workflows](06-workflows.md) | 18 end-to-end workflows, 93 validated steps |
| 07 | [Reports and analytics](07-reports-and-analytics.md) | 41 reports, 305 bilingual columns, Report Studio |
| 08 | [Screenshot programme](08-screenshot-programme.md) | What was captured, what failed, and why |
| 09 | [Troubleshooting and FAQ](09-troubleshooting-and-faq.md) | Real messages, causes, user-level remedies |
| 10 | [Bilingual terminology](10-bilingual-terminology.md) | 4,333 keys, and where the languages disagree |
| 11 | [Manual structure and traceability](11-manual-structure-and-toc.md) | The proposed contents for each edition |
| 12 | [Handoff and gaps](12-handoff-and-gaps.md) | What must still be done before writing |

## The machine-readable inventories

| File | Contents |
|---|---|
| `screen-catalog.json` | 309 screens: route, layout, bilingual heading and breadcrumb, columns, fields, actions, notices, empty states, modals, guards, source location |
| `field-action-catalog.json` | 2,780 entries — every field and every action, keyed to its screen |
| `workflow-catalog.json` | 18 workflows, 93 steps; **every route validated against the screen catalogue** |
| `report-catalog.json` | 41 report codes, 305 columns with Arabic and English titles, formats, engines, permissions |
| `role-permission-matrix.csv` | 73 rows — every declared role and action word, per module scope |
| `route-protection.csv` | 1,220 routable actions with the attributes guarding each |
| `bilingual-glossary.csv` | 4,333 interface keys in ar / en / fr, with the source and status of each |
| `messages-catalog.csv` | 223 user-facing messages, classified |
| `screenshot-manifest.csv` | One row per capture attempt — screen id, language, file, route, role, state, date, privacy treatment, verification level, manual section |
| `coverage-and-gaps.csv` | One row per screen — what is covered, what is missing |
| `evidence-register.csv` | Every claim, the command behind it, and its evidence level |
| `partials-catalog.json` | 63 shared partials and dialogs |

## The assets

| Folder | Contents |
|---|---|
| `assets/owner/` | The owner's portrait at original resolution — **kept separate from screenshots, by instruction** |
| `assets/brand/` | Logo lockups, marks, favicons, the eight module icons, sign-in artwork |
| `assets/fonts/` | Cairo Regular / SemiBold / Bold / ExtraBold + `OFL.txt` |
| `screenshots/` | **273 English + 268 Arabic captures** (541 files; 86% of screens in both languages), plus the raw capture logs |
| `scripts/` | All seven extraction and capture scripts, exactly as run |

---

## Evidence labels

Used consistently throughout. **Viewing a screen does not prove that its actions work.**

| Label | Means |
|---|---|
| **Repository verified** | Read from the source tree |
| **Runtime viewed** | Loaded in a browser, signed in; status, title, language, direction and control counts recorded |
| **Behaviour tested safely** | A non-mutating behaviour was exercised — sign-in, language switch, navigation, a refusal |
| **Not verified** | Neither read nor observed, or observed too shallowly |
| **Not implemented / disabled** | Found, and found absent, switched off, or unreachable |
| **Blocked** | Attempted and prevented — the reason is recorded |

---

## The five findings that will shape the manuals

**1. 41% of controls have no text.** 603 of 1,475 actions are icon-only. A manual cannot write
"press Delete" when the screen shows a bin. These must be *illustrated*, not just named — which
is most of what the screenshot programme is for.

**2. The product is command-driven, not form-driven.** 15 form-then-save pairs against **390
command endpoints**. The instruction shape is "press the button, the row updates" — not "submit
the form and the page reloads".

**3. Arabic is the more complete language.** 308 translation units against 293 French and 289
English. 19 screens have no English resource, 15 no French, **12 neither** — including two
partials that render on *every* page. The risk is the reverse of the usual one: do not let the
English edition quietly become the thin one, and do not invent Arabic for the 18 screen headings
that have none.

**4. The browser tab lies.** The title comes from the *layout*, so `/Workspace`, `/Tasks`,
`/Reports` and `/Calendar` all say **"Inventory System"**, and every CRM screen says **"Accounting
System"**. Never tell a reader to look at the title bar.

**5. A first-run trap.** `PlatformRoleAssignments` held **zero rows**, and HR's `employee-manage`
is in `NeverBootstrapOpen` — it refuses until granted, even for an administrator. Saving an
employee fails on a fresh install until HR roles are granted. That belongs in Part 4, not in
troubleshooting.

---

## Owner identity — status

| Item | Status |
|---|---|
| Arabic name **احمد زكريا** | Candidate, high confidence — from the owner's own employee record |
| English name **Ahmed Zakarya** | Candidate — **two spellings disagree**; needs one word of confirmation |
| Portrait `assets/owner/owner-portrait-original.png` | Candidate, high confidence — the only unique image, tied to employee 5 |
| That the portrait is of the owner | **NOT CONFIRMED** — no measurement can establish it; the owner must look |
| Job title / biography | **Not collected, by instruction** |

Full chain of evidence, and the candidates that were rejected, in document 02.

---

## Scope notes

- Development database only (`CrossBuyDev`). **Production was never contacted.**
- No credential, secret, connection string or token appears anywhere in this package.
- No other employee's personal information is reproduced. Screenshots contain development data;
  every capture carries a privacy treatment in the manifest, and anything an automatic scan
  flagged is marked **REVIEW BEFORE PUBLICATION**.
- `HEAD` moved three times during the discovery — see 01 §1.1.
- Document 12 lists what remains before a complete manual can be written. **Coverage is not
  claimed to be complete.**
