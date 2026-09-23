# 07 — Proposed guide outlines

A bilingual chapter sequence that follows the accounting cycle, written for accountants rather
than for engineers.

---

## The voice

Every section answers the same seven questions, in business language:

> **What is this step? Why is it needed? Who does it? What do I enter? What should happen?
> How do I check it? How do I fix a mistake?**

Engineering evidence — routes, source types, file and line references — stays in this discovery
package and is *linked*, never inlined. A reader of the guide should never meet
`AccountingController:1163`.

## Cover

| | Arabic edition | English edition |
|---|---|---|
| Direction | RTL | LTR |
| Typeface | Cairo (400/600/700/800), bundled, OFL | Cairo |
| Attribution | **احمد زكريا** | **Ahmed Zakarya** — *spelling unresolved, see 08* |
| Portrait | `assets/owner/owner-portrait-original.png` (1254×1254) | same |
| Logo | `assets/brand/crossbuy-logo-full.png` — **the PNG lockup, not the SVG** (the SVG's wordmark is live text in a font the product does not ship) | same |
| Version | **none — the product declares no version. A decision is required before printing.** | |

---

## Chapter sequence

Both editions carry the **same** chapters in the same order. `[✅]` marks material this package
can already illustrate with observed evidence; `[◻]` marks material that needs the execution
pass in [08](08-gaps-and-blockers.md).

```
Part 1 — Before you post anything
  1.1  How CrossBuy thinks about accounting                              [✅]
       one ledger, 54 kinds of business event, every module posting through one service
  1.2  Your company, your branch, and why there is no company switcher    [✅]
  1.3  The chart of accounts: what you can and cannot post to             [✅]
       includes: six accounts REQUIRE a cost centre
  1.4  Financial years and periods                                        [✅]
       13 periods a year — twelve months and an adjustment period
  1.5  Cost centres and projects: tagging an entry                        [◻]
  1.6  Currencies, rates and precision                                    [◻]
  1.7  Tax codes                                                          [◻]
  1.8  Document numbering: when your entry gets its number                [✅]
       a draft has NO number; posting allocates it

Part 2 — Opening the books
  2.1  Opening general-ledger balances                                    [◻]
  2.2  Opening customer and supplier balances                             [◻]
  2.3  Opening stock                                                      [◻]
  2.4  Not counting the same thing twice                                  [◻]

Part 3 — The daily ledger
  3.1  Writing a manual journal                                           [✅ fully executed]
  3.2  Saving a draft — and why that is not posting                       [✅]
  3.3  Posting, and who is allowed to                                     [✅]
       the approval threshold and the segregation-of-duties rule, and the fact that
       BOTH ARE OFF when the threshold is zero
  3.4  Correcting a posted entry: reversal                                [✅ fully executed]
  3.5  Posting into a closed period                                       [◻]

Part 4 — Buying
  4.1  Purchase order → goods receipt → supplier invoice                  [◻]
  4.2  Goods received but not yet invoiced                                [◻]
  4.3  Freight, duty and landed costs                                     [◻]
  4.4  Paying a supplier: part now, the rest later                        [◻]
  4.5  Returning goods to a supplier                                      [◻]
  4.6  Reading the supplier statement and the aging report                [✅ report captured]

Part 5 — Selling
  5.1  Quotation → order → delivery → invoice                             [◻]
  5.2  Collecting: partial receipts and final settlement                  [◻]
  5.3  A customer returns goods                                           [◻]
  5.4  Reading the customer statement and the aging report                [✅ report captured]
  5.5  Electronic invoicing status — and why it is not accounting         [✅ repository]
       it is DEFERRED in this build and makes no submission

Part 6 — Cash and banks
  6.1  Cash boxes and bank accounts                                       [◻]
  6.2  Moving money between them                                          [◻]
  6.3  Bank reconciliation                                                [◻]

Part 7 — Stock and its cost
  7.1  What a stock movement does to the ledger                           [◻]
  7.2  Counts, adjustments and write-offs                                 [◻]
  7.3  Do transfers change the accounts, or only the location?            [◻]
  7.4  Checking stock value against the ledger                            [◻]

Part 8 — Fixed assets
  8.1  Buying and capitalising an asset                                   [◻]
  8.2  Monthly depreciation                                               [◻]
  8.3  Selling or scrapping an asset                                      [◻]

Part 9 — Payroll
  9.1  What the payroll run calculates                                    [✅ repository]
  9.2  Posting the payroll                                                [◻]
  9.3  Paying it, and what stays as a liability                           [◻]

Part 10 — Connected modules
  10.1 The restaurant and retail tills                                    [◻]
  10.2 Projects: advances, progress billing, retention                    [◻]
  10.3 Manufacturing: materials, work in progress, finished goods         [◻]
  10.4 What is not connected yet                                          [✅]

Part 11 — Foreign currency
  11.1 Invoicing in another currency                                      [◻]
  11.2 Gains and losses when you settle                                   [◻]
  11.3 Revaluing at period end — and reversing it after                   [◻]

Part 12 — Closing
  12.1 The monthly close, step by step                                    [◻]
  12.2 The trial balance, and what to do when it does not balance         [✅ executed]
  12.3 Reading the balance sheet, income statement and cash flow          [✅ captured]
  12.4 The year-end close and retained earnings                           [◻]
  12.5 Reopening a closed period — and the reason you must record         [◻]

Part 13 — Reports
  13.1 Finding the report you need                                        [✅]
  13.2 Reading each report, column by column                              [✅ 9 reports]
  13.3 Exporting and printing                                             [◻ untested]
  13.4 Making a report agree with the ledger                              [✅ + honest gaps]

Part 14 — When something goes wrong
  14.1 Messages you may see, and what they mean                           [◻]
  14.2 Correcting a mistake safely                                        [✅ reversal]
  14.3 When to call the chief accountant                                  [✅]

Appendices
  A  Arabic–English accounting glossary
  B  Account reference: what each account is for
  C  The 54 business events and where each one posts                      [✅]
  D  Index
```

### Arabic outline

```
الجزء ١   قبل أن تُرحّل أي قيد
الجزء ٢   فتح الدفاتر
الجزء ٣   اليومية
الجزء ٤   المشتريات
الجزء ٥   المبيعات
الجزء ٦   النقدية والبنوك
الجزء ٧   المخزون وتكلفته
الجزء ٨   الأصول الثابتة
الجزء ٩   الرواتب
الجزء ١٠  الوحدات المرتبطة
الجزء ١١  العملات الأجنبية
الجزء ١٢  الإقفال
الجزء ١٣  التقارير
الجزء ١٤  عند حدوث خطأ
الملاحق   أ المصطلحات · ب دليل الحسابات · ج أحداث الترحيل · د الفهرس
```

**Identical coverage, chapter for chapter.** Where a screen has no Arabic text, the Arabic
edition quotes what the screen really shows and marks it — it does not invent Arabic the reader
will not find.

---

## What can be written today

| Part | State |
|---|---|
| **Parts 1, 3, 13 and the reversal half of 14** | **Writable now** from observed evidence |
| Parts 4.6, 5.4, 12.2, 12.3 | Writable — reports captured in both languages |
| Everything else | **Blocked** on the execution pass |

Roughly **a quarter of the guide** can be written from this package. That is a real advance on
the previous discovery, which could write none of the transactional chapters — but it is a
quarter, not a whole, and [08](08-gaps-and-blockers.md) says exactly what closes the rest.

---

## Illustration rules

1. Every figure is a real capture from `screenshots/`. **No mock-ups, no decorative substitutes.**
2. A figure showing an unverified expectation must be labelled as such. **There are none in this
   package** — all 30 captures are observed states.
3. Highlight regions are named per figure in [06](06-screenshot-storyboard.md); annotate a copy,
   never the original.
4. Arabic figures for the Arabic edition, English for the English. Both exist for all 15 states.
5. The owner's portrait appears on the cover only, and is never composited into a screenshot.
