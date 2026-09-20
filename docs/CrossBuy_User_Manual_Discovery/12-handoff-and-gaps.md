# 12 — Handoff and gaps

What is ready, what is missing, and exactly what must happen before a complete manual can be
written.

---

## 1. What is ready to write from, today

| Deliverable | State |
|---|---|
| Every screen, with exact Arabic and English titles | ✅ 309 screens |
| Every field and action, with bilingual labels | ✅ 2,780 entries |
| Every table column, notice and empty state | ✅ 1,370 / 78 / 24 |
| Every registered report, with bilingual column titles | ✅ 41 reports, 305 columns |
| Every user-facing message | ✅ 223, classified |
| The permission model | ✅ 11 scopes, 14 vocabularies, 1,220 guarded routes |
| Workflow skeletons with validated screen references | ✅ 18 workflows, 93 steps |
| The bilingual glossary | ✅ 4,333 keys with per-culture sources |
| Brand kit — logo, taglines, colours, fonts, licence | ✅ collected |
| Owner name and portrait | ⚠ candidates with a full evidence chain; **needs confirmation** |
| Screenshots | ⚠ **273 English, 268 Arabic; 86% of screens have both** — see §3 |

That is roughly the whole reference layer. **What is missing is behaviour, roles and pictures.**

---

## 2. The five blocking gaps

Ordered by how much they hold up.

### Gap 1 — No action was ever performed

**What is missing.** Of 1,475 actions, **zero** were clicked. Nothing was created, posted,
approved, cancelled or deleted.

**What that costs the manual.** Every consequential sentence is unverified:

- what a screen looks like after a successful save
- which of the 104 destructive buttons confirms first, and with which of the 23 prompts
- what a validation error looks like in place
- what status a document moves to, and when
- whether an action can be reversed, and by whom
- the 14 confirmation prompts whose wording is built at runtime

**Why it was not done.** Doing it changes data, which the brief forbids.

**How to close it.** Restore a copy of the development database as a **disposable** one, point a
non-shared instance at it, and walk the 18 workflows end to end, capturing each state. That is
half a day and it converts the 34 "not verified" steps into observed ones.

### Gap 2 — Only the administrator was used

**What is missing.** What a warehouse keeper, cashier, sales representative, HR officer or
employee actually sees. Menu visibility, field-level visibility, which actions are hidden versus
refused.

**What that costs the manual.** **Part 2, the role-based quick starts, cannot be written at all.**
It is the part most users read first.

**How to close it.** Create one test account per declared role on the disposable database
(`InventoryManager`, `PurchasingOfficer`, `WarehouseKeeper`, `ChiefAccountant`, `Auditor`,
`SalesManager`, `SalesRep`, `CrmViewer`, `HrManager`, `HrOfficer`, `PayrollOfficer`, `HrViewer`,
`ProjectsAdministrator`, `ProjectsFinance`, `ProjectsViewer`, `TasksAdministrator`, cashier
roles), sign in as each, and capture the sidebar and two or three key screens. Half a day.

**Also unresolved, and quick:** what distinguishes `SuperAdmin` from `Administrator` from `Admin`.
Three roles exist and no source states the difference.

### Gap 3 — Screenshot coverage is 86%, not 100%

**273 English and 268 Arabic screenshots (541 files); 267 of 309 screens (86%) have both
languages; 35 have neither.** The missing group is concentrated in the operator surfaces — Hyper lanes (8) and the
restaurant cashier/kitchen/delivery screens (5) — which need a terminal session or an open shift,
plus record-scoped views and `/Accounting/Payroll`, which times out.

§3 has the numbers and the cause. It is a capture-environment problem, not a product problem, and
it is the easiest of the five to fix.

### Gap 4 — No report was run

**What is missing.** No report was executed, exported or printed. So: no parameter panel observed,
no output page, none of the five formats produced, and **no worked example for any of the 41
reports** — which the brief asks for explicitly.

**How to close it.** Run each report once on the disposable database, capture the parameter panel
and the first page, and export one of each format. Two to three hours.

### Gap 5 — Nothing outside the web application was examined

| | |
|---|---|
| **Mobile client** (`crossbuy_mobile`, Flutter) | Noted as present. Not built, not run, not inventoried. **Part 15.3 cannot be written.** |
| **AI service** (`crossbuy_ai`, FastAPI) | Read, not started. No AI call was made, so no insight output was seen. |
| **Password recovery** | Not exercised — it sends mail. |
| **E-mail sending** | Configured; not exercised. |

---

## 3. Screenshot coverage, and the honest reason it is partial

### What happened

Captures ran against the owner's already-running IIS Express instance. That instance had been up
for hours and held **over 1.1 GB**. Its session store is in-memory, so under that pressure a
session is **evicted mid-run**; `SessionValidationAttribute` then redirects the next navigation to
the sign-in page, and a redirect arriving mid-`goto` surfaces as *"Navigation to X is interrupted
by another navigation to Y"*.

Measured across runs: the heavier the screen, the sooner it happened. The Accounting range (161
sidebar links, large datasets) failed worst — one slice captured 10 of 45 with **34
re-authentications**.

### What was tried

| Attempt | Result |
|---|---|
| Two languages in parallel | Made it worse — abandoned |
| Plain HTTP origin | The 307 to HTTPS caused its own interrupted navigations — switched to HTTPS |
| Re-authenticate and retry on detection | Helped; 221 re-authentications in one run, still incomplete |
| Short slices with a fresh sign-in each | **Best result** — light slices reached 33 of 45 |
| Retry only the missing routes, in batches of 12 | Used for the final passes |
| **Start a second, dedicated instance** | **Blocked** — `MSB3027`: the running instance holds `bin/Debug/net8.0/CrossBuy.exe`. Killing the owner's session was not an acceptable way around it |

### How to close it properly

**Stop the running instance, start a fresh one, and capture in one pass per language.** A
cold instance holds a fraction of the memory and will not evict sessions. Expected result: near
100% of the 305 routes in both languages, in about twenty minutes.

Everything needed is already in the package — `scripts/capture_manual_screens.mjs` takes
`CB_BASE`, `CB_LANG`, `CB_ROUTES`, `CB_FROM`, `CB_TO`, `CB_TAG`, and
`scripts/retry_missing.py` computes what is still outstanding.

### What is still not capturable this way

| | Why |
|---|---|
| Screens needing a record id (`/…/Details/{id}`) | Only reachable where development data supplies one |
| Modal dialogs (94 of them) | A modal is opened by a click; the capture only navigates |
| Post-action states | Require the action |
| Operator screens mid-transaction | Require an order in progress |

---

## 4. Smaller gaps, recorded

| Gap | Detail |
|---|---|
| **31 screens have no heading element** | Name them from the navigation label |
| **18 screen headings have no Arabic** | The key is shown to the user. Quote what is on screen, or fix the resource first |
| **267 fields have no caption in markup** | Listed in `coverage-and-gaps.csv`; a human must name them |
| **POS role vocabulary is not in source** | Read it off `/Pos/CashierRoles` |
| **`Marketing` is an assigned CRM role** not among those declared | Stale assignment, or declared somewhere not found |
| **Lookup sources behind 206 select2 controls** | The control is identified; the list behind it was not traced |
| **Conditional visibility / editability** | Decided server-side and in JavaScript — not established |
| **`Void` vs `Voided`** | Two spellings of one idea. Pick one per language and note it |
| **CRM → quotation conversion** | Whether it happens in the product or by retyping was not established. Commercially the most important unknown |
| **`/Accounting/Payroll` is very slow** | Exceeded 45 s twice. Needs a second look and a note for users |
| **Keyboard shortcuts** | Not collected at all — Appendix D cannot be written |
| **Photo hashing was blocked** | The environment's data-handling policy refused it; duplicate grouping rests on file sizes |

---

## 5. Decisions the owner must make

Nothing below can be resolved by more discovery.

1. **The English spelling of the name.** `Ahmed Zakarya` (git, 235 commits) or `ahmed zakareya`
   (employee record)? The git form is proposed.
2. **Is the portrait the right one?** One look at `assets/owner/owner-portrait-original.png`
   settles it.
3. **What version goes on the cover?** The product declares none.
4. **Who is the publisher?** No company, copyright holder or legal entity exists anywhere. The
   wordmark carries a **®** with no registrant named.
5. **The 19 screens with no English resource** — translate them, or let both manuals document the
   gap?
6. **Manufacturing** — document it as work in progress, as the product labels it, or omit it?
7. **The mobile client** — in scope or out?
8. **Scheduled report delivery** — describe it as present-but-disabled, or omit it?

---

## 6. Recommended sequence

| Step | Work | Effort |
|---|---|---|
| 1 | Owner confirms name spelling and portrait | minutes |
| 2 | Owner decides version, publisher, and items 5–8 above | minutes |
| 3 | Stop the running instance; take a disposable copy of the development database | 30 min |
| 4 | Re-run both capture passes on a cold instance | 30 min |
| 5 | Create one account per role; capture sidebars and key screens | half a day |
| 6 | Walk the 18 workflows end to end, capturing each state and every message | half a day |
| 7 | Run all 41 reports; capture parameters and output; export one of each format | 2–3 hours |
| 8 | Capture the 94 modals and the record-scoped screens | 2–3 hours |
| 9 | Name the 267 unlabelled fields and the 31 headingless screens | 2 hours |
| 10 | Then write | — |

Steps 3–8 need one thing this discovery could not have: **a database nobody minds breaking.**

---

## 7. What must not be claimed

> **Do not claim full coverage while undisclosed gaps remain.**

Specifically, the following must not be said of this package:

- ❌ "Every screen is documented." — 309 are inventoried; **coverage of behaviour is zero**.
- ❌ "All workflows are verified." — 18 are authored; **6 steps of 93 were actually exercised**.
- ❌ "Role permissions are documented." — they are **declared**, never observed.
- ❌ "Screenshots cover the product." — **86% of screens in both languages; 35 screens have
  none**, and §3 names them.
- ❌ "The owner's name and portrait are confirmed." — they are **candidates with a chain**.
- ❌ "The manual can now be written." — Parts 2, 13.3 and 15.3 cannot.

What *can* be said: **the reference layer is complete enough that writing can begin on Parts 1,
3–12, 14 and 16, while steps 3–8 above close the rest.**
