# 08 — Screenshot programme

What was captured, in both languages, under what conditions — and what could not be.

**Machine-readable: `screenshot-manifest.csv`.** One row per capture *attempt*, not per file, so a
route that was tried and failed is present with its reason instead of being invisible.

---

## 1. Capture conditions

```
instance      https://localhost:44368        (the owner's already-running IIS Express session)
environment   Development
database      localhost \ CrossBuyDev        — production never contacted
account       the development administrator, one session
viewport      1440 × 900, headless Chromium
format        PNG, no compression loss, no crop
date          20 September 2026
```

**Read-only by construction.** The capture script signs in and navigates. The only form it ever
submits is the sign-in form. No create, edit, delete, post or approve control was clicked, so no
business record changed and no transaction ran.

### 1.1 Credentials are not in the package

`scripts/capture_manual_screens.mjs` reads `CB_USER` and `CB_PASS` from the environment and
refuses to start without them. **No password appears anywhere in this package** — verified by
`scripts/privacy_and_secret_audit.py`, which scans every text file for connection strings, keys,
tokens, private keys and personal identifiers.

### 1.2 What each capture recorded

Beyond the image, per screen: HTTP status, final URL after redirects, `<title>`, `<html lang>`,
text direction, the visible heading, counts of inputs / buttons / table rows / modals, whether a
developer overlay was visible, whether re-authentication was needed, and an automatic privacy
scan of the rendered text.

That is what makes the manifest usable as evidence rather than as a file listing.

## 2. Requirements from the brief, and how each was met

| Requirement | How |
|---|---|
| Consistent viewport, readable resolution | 1440 × 900 for every capture, no exceptions |
| Clean interface states | Each page allowed to settle; state recorded as `rendered-with-data`, `rendered-empty-state`, `rendered-but-near-empty`, `http-403`, or an error |
| Existing safe development data only | Nothing seeded, nothing created |
| No browser debugging overlays | Checked per capture. **Zero captures showed one** — Browser Link is attached to this instance but renders nothing visible |
| No exposed secrets | Privacy scan per capture; package-wide audit separately |
| No embedded annotations | Originals are unannotated. Any callouts belong in the manual, over a copy |
| Arabic and English wherever available | Two passes, each with the culture confirmed by reading `<html lang>` back before capturing |

### 2.1 Language is confirmed, not assumed

An early pass switched culture and never checked. Every screen came back `lang="en"`, and the
"Arabic pass" produced files **byte-identical** to the English ones — the switch had silently
failed. The script now navigates to a real page after switching and **aborts the whole run if
`<html lang>` is not the language requested.**

## 3. Coverage

Of 309 catalogued screens, **305 were routable and attempted** in each language. The four
excluded are framework views with no route (`/Shared/Error`, two `/Shared/Default`) and the
sign-in page, which cannot be photographed while already signed in.

### Final counts

| | Count | of 309 screens |
|---|---:|---:|
| **English screenshots** | **273** | 273 screens |
| **Arabic screenshots** | **268** | 268 screens |
| Screens with **both** languages | **267** | **86%** |
| Screens with **one** language only | 7 | 2% |
| Screens with **neither** | **35** | 11% |
| Manifest rows (capture attempts) | 611 | |
| Files with no log entry | **0** | |
| Captures showing a developer overlay | **0** | |
| Flagged for privacy review | 260 | see §6 |

Capture states across all 611 attempts:

```
rendered-with-data            435
rendered-empty-state           80     a legitimate illustration; the manual must say it is empty
error                          72     see §5
rendered-but-near-empty         9
http-403                        6     the Access-denied screen — a wanted capture
captured (log lost, recovered)  6
refused-redirected-to-signin    2     /Account/Login while signed in
http-500                        1
```

### The 35 screens with neither language

| Module | Screens | Why |
|---|---:|---|
| Hyper | 8 | Supermarket lane screens rendered by `HyperPosController` from absolute view paths; several need a terminal session |
| PosApp | 5 | Restaurant operator screens — cashier, kitchen, delivery — need an open shift |
| Accounting | 3 | Includes `/Accounting/Payroll`, which exceeded a 45-second load twice |
| Inventory | 3 | Record-scoped or form views reached only with an id |
| Shared | 3 | Framework views with no route — correctly excluded |
| Reports, Roster, Store | 6 | |
| Account, ClientPortal, Comm, Documents, EmployeeOnboarding, ProjectCloseout, Service | 7 | |

Full per-screen detail, including which language is missing and why, is in
`coverage-and-gaps.csv`.

**Coverage is partial, and the reason is environmental, not a product fault.** §5 documents it in
full, including how to reach near-100%.

## 4. What is deliberately *not* captured

| | Why |
|---|---|
| **The 94 modal dialogs** | A modal opens on a click. The capture only navigates. |
| **Post-action states** | Require performing the action. |
| **Validation errors in place** | Require submitting bad data. |
| **Record-scoped screens** (`/…/Details/{id}`) | Reachable only where development data supplies an id. |
| **Operator screens mid-transaction** | Require an order in progress. |
| **Any screen as a non-administrator** | Requires a second account. |

Each is a deliberate omission forced by the read-only constraint, not an oversight. Document 12
says how to close them.

## 5. The capture problem, and everything tried

### What went wrong

The instance used had been running for hours and held **over 1.1 GB**. Its session store is
in-memory. Under that pressure ASP.NET Core evicts session entries, and
`SessionValidationAttribute` — which reads `Session["Employee"]` and redirects to sign-in when it
is gone — then bounced the next navigation. A redirect arriving mid-`goto` surfaces as
*"Navigation to X is interrupted by another navigation to Y"*.

It was reproducible and load-dependent: the heavier the screen, the sooner it happened. The
Accounting range (161 sidebar links, large datasets) failed worst — one slice captured 10 of 45
with **34 re-authentications**.

### What was tried, in order

| Attempt | Result |
|---|---|
| Both languages in parallel | Worse. Abandoned. |
| Plain-HTTP origin (`:54328`) | Its 307 to HTTPS caused its own interrupted navigations. Switched to HTTPS. |
| Detect the bounce, re-authenticate, retry the route | Helped. 221 re-authentications in one run, still incomplete. |
| Short slices, fresh sign-in each | **Best.** Light slices reached 33 of 45. |
| Retry only the missing routes in batches of 10–12, repeatedly | Used for the final passes. |
| **Start a second, dedicated instance** | **Blocked.** `MSB3027` — the running instance holds `bin/Debug/net8.0/CrossBuy.exe`. Stopping the owner's debugging session was not an acceptable way around it. |

### How to get complete coverage

**Stop the running instance, start a cold one, run one pass per language.** A fresh process holds
a fraction of the memory and does not evict sessions. Expected: near 100% of 305 routes in both
languages, in roughly twenty minutes.

Everything needed ships in `scripts/`:

```bash
# one pass, one language, cold instance
set CB_USER=…  CB_PASS=…
CB_BASE=https://localhost:44368 CB_LANG=ar CB_FROM=0 CB_TO=305 \
  node <browser-automation>/browser.mjs https://localhost:44368/Account/Login \
       --script scripts/capture_manual_screens.mjs

# what is still missing, and a retry list
python scripts/retry_missing.py ar        # writes routes.json.retry
# then re-run with CB_ROUTES=<that file>

# rebuild the manifest and coverage from whatever exists
python scripts/build_manifest_and_registers.py
```

## 6. Privacy treatment

Every capture carries a per-row treatment in the manifest. The scan looks for e-mail addresses,
phone-like numbers, IBAN-like strings and 14-digit national-id patterns, and is **deliberately
over-inclusive** — a long formatted figure on a balance sheet will trip the phone-like rule. A
flag means *a human must look*, not *this is personal data*.

| Treatment | Meaning |
|---|---|
| `development data; no … detected` | Nothing matched. Development data only. |
| `development data; automatic scan flagged: … — REVIEW BEFORE PUBLICATION` | Something matched. Check before the image ships. |
| `not captured` | No file — see the row's `state` and `capture_error`. |

Two standing notes for whoever prepares the manual images:

1. **The signed-in user's name and avatar appear in the header of every authenticated screen.**
   That is the development administrator. Crop or blur the header, or re-capture under a neutral
   account, before publication.
2. **The header avatar is a Metronic stock image, not the owner's portrait.** A screenshot is
   therefore not a picture of the owner. The portrait lives in `assets/owner/` and, by
   instruction, is kept out of `screenshots/` entirely.

## 7. Two screens worth naming

**`/Accounting/TrialBalance`** captured with real figures: Debit `113,027,021.50`, Credit
`113,027,021.50`, Difference `0.00`, and the product's own dashed notice *"The trial balance is
balanced"* with a **Matched** badge. It is the single best illustration in the set — the
accounting identity shown rather than asserted — and it is the product's stated visual authority
for accounting screens.

**`/Account/AccessDenied`** captured at **HTTP 403**. Refusals are normally the hardest thing to
photograph without breaking something, and this one came free. It belongs in the troubleshooting
chapter.

## 8. Naming

```
<route-with-slashes-as-hyphens>.<lang>.png

accounting-trialbalance.en.png      /Accounting/TrialBalance, English
accounting-trialbalance.ar.png      /Accounting/TrialBalance, Arabic
inventory-items.en.png              /Inventory/Items, English
```

Deterministic, so a manual can reference a file by route without consulting a lookup — and the
manifest joins it to `screen_id`, the workflow steps and the intended manual section.
