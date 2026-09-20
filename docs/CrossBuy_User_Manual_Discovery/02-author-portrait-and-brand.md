# 02 — Author identity, portrait and brand assets

What the two manuals need on their covers, how each item was found, and what is still unconfirmed.

---

## 1. The instruction, restated

> *"The owner is Ahmed. Do not assume a fuller name, job title, or biography. Do not select another
> employee's photograph or a stock avatar."*

Accordingly: no job title is proposed below, no biography is written, and the portrait selected is
the only one in the repository that the evidence chain ties to the owner's own record. Everything
is presented as a **candidate with its chain**, not as a confirmation.

---

## 2. The name

### 2.1 Candidates found

| # | Value | Where it came from | Kind of evidence |
|---|---|---|---|
| **A** | `Ahmed Zakarya` | Git commit author, the address on those commits (redacted — not needed for attribution) — **235 of 247 commits** on this branch | Repository verified |
| **B** | `ahmed zakareya` | `Employee.FullNameEn`, employee **ID 5**, development database | Runtime viewed |
| **C** | `احمد زكريا` | `Employee.FullName`, employee ID 5, development database | Runtime viewed |
| **D** | `Ahmed Zakarya` | `docs/platform/ADR-032-Mention-Resolution.md:91` — `@[Ahmed Zakarya](employee:12)` | Repository verified — **but see §2.4** |
| **E** | `Ahmed` | `docs/CrossBuy_System_Context_for_AI.md:194` — *"`Admin`/`<dev password redacted>` (= employee "Ahmed", company head)"* | Repository verified |
| **F** | `AHMEDZAKAREYA` | Windows machine name, visible in a worker-lease log line and a test failure message | Incidental |

### 2.2 How the chain was built

1. `git log --format="%an <%ae>"` — one author dominates the history (A).
2. `grep -rlni "ahmed"` across `docs/` and configuration found (D) and (E). (E) states in plain
   words that the `admin` account is employee *"Ahmed", company head*.
3. Signing in at runtime as `admin` and loading `/Portal/Choose` rendered the greeting
   **"Welcome, ahmed zakareya"** — which is the `FullNameEn` of employee ID 5. That is what links
   the sign-in account to a specific employee row; there is no e-mail match between `AspNetUsers`
   and `Employee`, so the greeting is the link.
4. Reading employee 5 directly confirmed (B) and (C). The Arabic came back as `????` in the
   console — that is the console code page, not corrupted data, so it was re-read as Unicode code
   points and decoded: `1575,1581,1605,1583,32,1586,1603,1585,1610,1575` → **احمد زكريا**, all
   within the Arabic block, no mojibake.

### 2.3 Recommendation for the covers

| Edition | Proposed attribution | Confidence |
|---|---|---|
| Arabic | **احمد زكريا** | Stored value, read directly from the owner's own employee record |
| English | **Ahmed Zakarya** | Spelling (A), from the git identity |

**Two English spellings exist and they disagree**: `Zakarya` (git identity, 235 commits) and
`zakareya` (employee record, lower-case). They are the same name transliterated twice. The git
form is proposed because it is the one the owner typed for themselves in a professional context
and it is correctly capitalised — **but this is an editorial choice and needs one word of
confirmation before either cover is printed.**

The Arabic `احمد` is stored without the hamza (`أحمد`). It is reproduced exactly as stored rather
than "corrected", because normalising somebody's name without being asked is not a typography
decision.

### 2.4 One candidate rejected, and why it matters

`ADR-032` shows `@[Ahmed Zakarya](employee:12)`. **Employee 12 does not exist in the development
database** — the query returned no row. The ADR line is an illustrative example of the mention
syntax, not a record of the owner's employee id. Had it been trusted, the portrait lookup would
have gone to the wrong record or to none at all.

### 2.5 What is deliberately absent

No job title, role, department, biography, e-mail address or contact detail is proposed. The
brief forbids assuming them, and none of the sources above establishes any of them independently.
`(E)` calls the account *"company head"* — that is a **seed-data description of a test account**,
not a statement about a person, and it is not used.

---

## 3. The portrait

### 3.1 The file

```
source        CrossBuy/wwwroot/uploads/employees/d5af6445-b974-4ad7-8b20-f377e0fc9bda.png
referenced by Employee.ProfileImage, employee ID 5  (= /uploads/employees/d5af6445-….png)
dimensions    1254 × 1254 px, square
format        PNG, 8-bit, truecolour
size          1,638,991 bytes
copied to     assets/owner/owner-portrait-original.png   (untouched, no crop, no resample)
```

**It is a genuine studio portrait**: a person in business dress on a plain white background,
framed head-and-shoulders, centred. Square and 1254 px, it is large enough for a full-page cover
plate in either manual.

### 3.2 How it was selected, and what was rejected

`wwwroot/uploads/employees/` holds **17 files**. The selection was made by elimination, not by
appearance:

| Rejected | Count | Why |
|---|---|---|
| `420296b8…`, `46d7099b…`, `8210b7c7…`, `8b02c872…`, `a29bccff…`, `a3b36016…`, `b2811762…`, `e7d5c543…` | 8 | All exactly **1,480,739 bytes** — one image re-uploaded eight times during testing. Not tied to any employee row. |
| `sara-ali-photo.jpg`, `khaled-hassan-photo.jpg` | 2 | Named for other (seeded) employees — IDs 17 and 18. |
| `test-cairo-mgr`, `test-cairo-staff`, `test-cairo-dept`, `test-alex-mgr`, `test-alex-staff`, `test-alex-dept` | 6 | Named `test-*`, tied to seeded employees 19–24. |
| `/Backend-assets/media/avatars/300-1.jpg`, `300-3`, `300-5`, `300-9` | 4 refs | **Metronic stock avatars**, referenced by employees 1044–1046 and 2044. Explicitly excluded by the brief. |
| **Selected:** `d5af6445-…png` | 1 | The only unique file, 1,638,991 bytes, and the only one referenced by **employee 5** — the record the name chain in §2 lands on. |

Note that the signed-in header avatar visible in the captured screenshots is one of the Metronic
stock avatars, **not** this portrait. A screenshot is therefore not evidence of the owner's
appearance, and the two asset classes are kept in separate folders for that reason
(`assets/owner/` vs `screenshots/`), as the brief requires.

### 3.3 What would confirm it

The chain is: `admin` sign-in → greeting "ahmed zakareya" → employee 5 → `ProfileImage` →
this file. Every link is measured. **What no measurement can establish is that the person in the
photograph is the owner** — only the owner can confirm that. One look at
`assets/owner/owner-portrait-original.png` settles it.

### 3.4 Hashing was blocked

An attempt to hash all 17 files to prove the eight duplicates byte-identical was **refused by this
environment's data-handling policy** (personal images). The duplicate grouping above therefore
rests on file sizes from a directory listing, which is weaker but sufficient: eight files sharing
an exact byte count to the digit are one image. This is recorded rather than worked around.

---

## 4. Brand assets collected

Copied to `assets/brand/` and `assets/fonts/`. Full provenance, sizes and reference counts are in
the sibling package `docs/business-brand-discovery/asset-manifest.csv`.

### 4.1 Marks

| File | Size | Use |
|---|---|---|
| `crossbuy-logo-full.png` | 1744 × 394 | **The cover lockup.** Outlined, full colour — the one rendered on the sign-in screen |
| `crossbuy-logo-lockup.png` | 1744 × 356 | Alternate lockup |
| `crossbuy-logo.png` / `crossbuy-logo-mark.png` | 367 × 250 / 367 × 227 | Smaller raster |
| `crossbuy-mark.png` | 193 × 192 | Mark alone |
| `crossbuy-logo.svg` | 176 × 34 | Backend header — **see the warning in §4.4** |
| `crossbuy-logo-light.svg` | 210 × 44 | For dark panels |
| `crossbuy-favicon.svg`, `favicon.ico`, `favicon.png` | — | Icons. `favicon.png` is 1024 × 1158 — **not square** |
| `icon-accounting`, `icon-crm`, `icon-hr`, `icon-inventory`, `icon-manufacturing`, `icon-pos`, `icon-projects`, `icon-reports` | ~200 px | The eight module icons from the sign-in screen — usable as chapter marks |
| `login-scene.png`, `login-background.png` | 1536 × 1024, 865 × 270 | Cover artwork candidates |

### 4.2 Taglines — approved, trilingual, verbatim

| Resource key | العربية | English |
|---|---|---|
| `BrandTagline` | المنصّة التي تربط أعمالك | THE CROSS-BUSINESS PLATFORM |
| `BrandHeadlineLead` | .منصّة واحدة | One Platform. |
| `BrandHeadlineAccent` | .إمكانات بلا حدود | Endless Possibilities. |
| `BrandSubLine1` | .اربط فريقك وإجراءاتك وبياناتك | Connect your people, processes and data. |
| `BrandSubLine2` | .وابنِ غدًا أكثر ذكاءً | Build a smarter tomorrow. |
| `BrandFootline` | أعمال بلا حدود | BUSINESS WITHOUT BOUNDARIES |

Source: `CrossBuy/Resources/SharedResources.{ar,en,fr}.resx`. French exists for all six.

### 4.3 Colour roles

Authoritative layer: `wwwroot/Backend-assets/css/crossbuy-brand.css`. Contrast ratios below were
computed independently, not copied from the file's comments.

| Role | Token | Value | Use in the manual |
|---|---|---|---|
| Identity | `--cb-brand` | `#1877F2` | Covers, rules, chart series. **Never** small white text on it (4.23 — fails AA) |
| Heading ink | `--cb-brand-ink` | `#0E4A9E` | Chapter headings, body links — 8.43 on white |
| Fill | `--cb-brand-fill` | `#166FE5` | Callout blocks, tabs — white on it 4.73 |
| Surface | `--cb-brand-surface` | `#E7F0FF` | Tinted note panels — ink on it 7.35 |
| Accent | *(no token)* | `#F59E0B` | The amber half of the wordmark. See §4.4 |
| Success / Warning / Danger | `--bs-success` / `-warning` / `-danger` | `#16A34A` / `#F59E0B` / `#EF4444` | Step outcomes. For text use the `-text-emphasis` variants (`#14532D`, `#92400E`, `#991B1B`), all above 7:1 |

### 4.4 Two brand warnings that affect the manuals

**The SVG logo's wordmark is live text.** `crossbuy-logo.svg` and `crossbuy-logo-light.svg` render
the wordmark as `<text …font-family="Segoe UI, Tahoma, Arial, sans-serif">`, not as outlines. On
any machine without Segoe UI the letterforms change. **Use the PNG lockups on the covers**, or have
the SVG text converted to paths first.

**The brand accent and the warning colour are the same hex** — `#F59E0B` is both the amber in
`Cross`**`Buy`** and `--bs-warning`. In a manual this is a live hazard: an amber rule used as
decoration will read as a caution. Reserve amber for the masthead and use
`--bs-warning-text-emphasis` `#92400E` for anything that must actually warn.

### 4.5 Fonts

| File | Weight | Licence |
|---|---|---|
| `Cairo-Regular.ttf` | 400 | SIL Open Font License — `OFL.txt` shipped alongside |
| `Cairo-SemiBold.ttf` | 600 | " |
| `Cairo-Bold.ttf` | 700 | " |
| `Cairo-ExtraBold.ttf` | 800 | " |

**Cairo covers both scripts**, so both editions can be set in one family. Four static weights are
bundled; the OFL permits redistribution and embedding.

Three constraints carried over from the product's own reporting layer, which apply equally to a
typeset manual:

1. **Static faces only.** `ReportFontLibrary` records that Chromium's print-to-PDF does not embed a
   *variable* Cairo TTF and silently falls back to SegoeUI-Bold. The four static files above are the
   ones to embed.
2. **The font stack must be dual-script.** A Latin-first stack splits a bilingual page across two
   typefaces.
3. **Inter is not bundled.** The product loads it from `fonts.gstatic.com`, and that request
   **failed during capture**. Do not depend on Inter for the English edition — Cairo's Latin
   letterforms are complete and already licensed locally.

---

## 5. Status summary

| Item | Status |
|---|---|
| Arabic name | **Candidate, high confidence** — `احمد زكريا`, from the owner's own employee record |
| English name | **Candidate, needs one word** — `Ahmed Zakarya` (git) vs `ahmed zakareya` (employee record) |
| Portrait file | **Candidate, high confidence** — single unique image, tied to employee 5 by `ProfileImage` |
| Portrait is of the owner | **NOT CONFIRMED** — no measurement can establish this; the owner must look |
| Job title / biography | **Not collected, by instruction** |
| Logo, taglines, colours, fonts | **Collected and verified** |
| Existing author/owner attribution anywhere in the product | **None found.** No `<Authors>`, `<Company>`, `<Copyright>` or "about" screen exists — see 01 |
