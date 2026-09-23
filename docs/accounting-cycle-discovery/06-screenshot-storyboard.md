# 06 — Screenshot storyboard and quality audit

The captures, each with its teaching purpose, captions and the region to highlight.

> **Superseded in part by execution pass 2.** The package now holds **80 captures — 49 English and 31 Arabic**, not the thirty this storyboard was written for. `screenshot-manifest.csv` is authoritative; the quality audit below is restated there. The execution record is [10-execution-pass-2.md](10-execution-pass-2.md); the current counts are in [08-gaps-and-blockers.md](08-gaps-and-blockers.md) §1.


**Machine-readable:** `screenshot-manifest.csv`, with every field the brief requires plus the
readiness checks and duplicate group for each file.

---

## 1. Quality audit

| Check | Result |
|---|---|
| Captures attempted | 30 |
| Files written | **30** |
| **Readiness gate failures** | **0** |
| Loading states / spinners captured | **0** — blocked by the gate, not filtered afterwards |
| Animated counters still counting | **0** — `countersSettled` true on all 30 |
| Sign-in redirects counted as coverage | **0** |
| Developer overlays visible | **0** |
| Blank or near-empty pages | **0** (minimum 400 characters of body text enforced) |
| **Exact duplicates (SHA-256)** | **1 group — explained in §1.3** |
| English / Arabic | **15 / 15** |
| Viewport | 1600 × 1000, identical for every capture |

### 1.1 How the gate works

Nothing is written to disk until, in order: the document has loaded; the network is idle (or
declared as polling); **every** spinner, overlay and skeleton is gone; **every
`[data-kt-countup]` counter has been textually stable across three polls**; the expected
selector or text is present; the URL is not the sign-in or error page; the body carries at least
400 characters; and no developer overlay is on the page.

A capture that fails the gate is **recorded as a failure with its checks, and no file is
produced** — so a missing screenshot is visible in the manifest instead of being silently absent.
`shot()` in `scripts/lib-ready.mjs` is the whole mechanism.

### 1.2 Legitimate near-identical images

`ACC-GL-01-03`, `-04`, `-05` and `-06` all show `/Accounting/Journals` and look similar at a
glance. They are **different accounting states** of the same record and must all be kept:

| File | State of JE 24213 (EN) / 24214 (AR) |
|---|---|
| `-03_after-save-draft` | just saved — **Draft, no entry number** |
| `-04_draft-in-list` | the draft located in the list, before posting |
| `-05_after-post` | **Posted**, number `JV-2026-008513` allocated (EN)  |
| `-06_after-reverse` | **Reversed**, with `JV-2026-008514` created above it |

No SHA-256 collision exists between them, so the duplicate detector agrees they differ.

### 1.3 The one real duplicate, and why it exists

`ACC-GL-01-06_after-reverse.ar.png` and `ACC-RPT-07_journals.ar.png` are **byte-identical**
(SHA-256 `6c83f90da256…`).

The cause is structural, not a capture error: **`ReverseJournal` redirects to
`/Accounting/Journals`, and that screen *is* the journal register report.** The confirmation
state after a reversal and the journal-register report are the same page at the same moment, so
in Arabic they produced the same pixels. The English pair differs only because the two captures
happened at different points in the run.

**Treatment for the guide:** use `ACC-GL-01-06` as the reversal illustration and
`ACC-RPT-07` as the register illustration, but **crop them differently** — the reversal figure
should be cropped to the two rows for `JV-2026-008515` and `JV-2026-008516`, the register figure
to the column headers and the status column. Do not print the same full-page image twice.

An alternative, if a re-capture is run later: filter the journal list before capturing
`ACC-GL-01-06` so the two are genuinely different pages.

### 1.4 What is **not** covered

No capture exists of: a confirmation dialog (the journal create path does not raise one — `IS_EDIT`
is false on create, so `CB.confirm` is skipped); a validation or rejection state; an allocation or
remaining-balance view; a bank reconciliation; a report export; or any screen as a non-administrator.
Those follow the scenarios that were not executed.

---

## 2. Storyboard — ACC-GL-01, the manual journal

Every row exists in both languages: `<id>.en.png` and `<id>.ar.png`.

| ID | Teaching purpose | Highlight | Caption (EN) | Caption (AR) |
|---|---|---|---|---|
| `ACC-GL-01-01_empty-form` | Where a journal begins, and that two buttons — not one — end it | The **Save draft** / **Post** pair, bottom right | A new journal entry: the date, the description and one empty line | قيد يومية جديد: التاريخ والبيان وسطر واحد فارغ |
| `ACC-GL-01-02_filled-before-save` | What a balanced entry looks like **before** it is committed | The `Total 5000.00 / 5000.00` row and the green **Balanced ✓** badge | Debit equals credit — the entry may now be saved or posted | المدين يساوي الدائن — يمكن الآن حفظ القيد أو ترحيله |
| `ACC-GL-01-03_after-save-draft` | That saving is **not** posting | The **Draft** status badge, and the empty entry-number column | Saved as a draft. It has no entry number and is not in the ledger | حُفظ كمسودة. لا يحمل رقم قيد ولم يدخل الدفاتر بعد |
| `ACC-GL-01-04_draft-in-list` | How to find a draft again | The row for `ZZ-DISCOVERY GL-01`, and the actions at its end | The draft waiting in the journals list | المسودة في انتظار الترحيل ضمن قائمة القيود |
| `ACC-GL-01-05_after-post` | The moment the entry enters the ledger | The newly allocated number `JV-2026-008513` and the **Posted** badge | Posted. The number is allocated now, not when it was saved | تم الترحيل. رقم القيد يُخصَّص الآن، لا عند الحفظ |
| `ACC-GL-01-06_after-reverse` | That a correction adds an entry rather than erasing one | Both rows: `JV-2026-008513` marked **Reversed**, and the new `JV-2026-008514` | Reversed. The original stays, and a mirror entry is created | تم العكس. القيد الأصلي يبقى ويُنشأ قيد عكسي مقابل له |

## 3. Storyboard — reports

| ID | Teaching purpose | Highlight | Caption (EN) | Caption (AR) |
|---|---|---|---|---|
| `ACC-RPT-01_trial-balance` | The one check that proves the books hold together | The three tiles: Debit, Credit, **Difference 0.00** | The trial balance: total debits equal total credits | ميزان المراجعة: إجمالي المدين يساوي إجمالي الدائن |
| `ACC-RPT-02_balance-sheet` | Position at a date | The Assets / Liabilities / Equity tiles | The balance sheet: what the company owns and owes | الميزانية العمومية: ما تملكه الشركة وما عليها |
| `ACC-RPT-03_income-statement` | Result for the period | The result figure | The income statement: revenue less expenses | قائمة الدخل: الإيرادات ناقص المصروفات |
| `ACC-RPT-04_cash-flow` | Where cash moved | Opening, movement and closing | The cash-flow statement | قائمة التدفقات النقدية |
| `ACC-RPT-05_ar-aging` | How overdue the receivables are | The aging bands and the totals row | Receivables by age: current, 31-60, 61-90, over 90 | أعمار ديون العملاء: جارٍ، ٣١-٦٠، ٦١-٩٠، أكثر من ٩٠ |
| `ACC-RPT-06_ap-aging` | The same for what is owed | The totals row | Payables by age | أعمار ديون الموردين |
| `ACC-RPT-07_journals` | Where every posting can be found | The Status column, showing Draft, Posted and Reversed side by side | The journal register: every entry and its status | سجل القيود: كل قيد وحالته |
| `ACC-RPT-08_customer-statement` | That a statement starts by choosing a party | The party picker | Choose a customer to produce a statement | اختر عميلاً لإصدار كشف حساب |
| `ACC-RPT-09_vendor-statement` | The supplier equivalent | The party picker | Choose a supplier to produce a statement | اختر مورّداً لإصدار كشف حساب |

**Every caption above describes observed behaviour.** None illustrates an unverified expectation.

---

## 4. Untranslated interface, flagged for the Arabic edition

Found while capturing, and the guide must not paper over it:

| Where | What the Arabic user sees |
|---|---|
| Reversing entry description | **Arabic only**, auto-generated — «قيد عكسي JV-2026-008513». Correct in Arabic; the **English** edition will show Arabic text here |
| Journal `Status` values | Stored as English literals (`Draft`, `Posted`, `Reversed`). Whether the Arabic screen translates them for display was **not confirmed per screen** |
| `JournalType` values | Same: `Manual`, `Auto`, `Reversing` |

## 5. Naming

```
ACC-GL-01-02_filled-before-save.ar.png
ACC-RPT-01_trial-balance.en.png
<scenario>-<step>_<state>.<lang>.png
```

Stable, sortable, and joinable to `screenshot-manifest.csv` on `screenshot_id` + `language`.

## 6. Privacy

Every capture is of **synthetic activity on an isolated disposable database**
(`CrossBuyAcctTest`), and the manifest says so per row. The pre-existing demonstration data
copied from the development database is visible in the reports — customer and vendor names in
the aging reports in particular. Before publication:

- crop or blur the party-name column in `ACC-RPT-05` and `ACC-RPT-06`, or re-run them filtered to
  the synthetic records only;
- the signed-in user's name and avatar appear in the header of every screen — the avatar is a
  Metronic stock image, **not** the owner's portrait, which is kept in `assets/owner/`.
