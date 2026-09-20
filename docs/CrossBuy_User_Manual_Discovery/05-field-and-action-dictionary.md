# 05 — Field and action dictionary

Every input a user fills and every control a user presses, with the exact bilingual label.

**Machine-readable: `field-action-catalog.json` — 2,780 entries (1,305 fields + 1,475 actions),
each keyed to its screen.**

---

## 1. Fields — what was captured

| Control type | Count |
|---|---:|
| Text | 456 |
| Select (drop-down) | 397 |
| Number | 213 |
| Checkbox | 81 |
| Date | 75 |
| Textarea | 42 |
| File upload | 15 |
| Password | 6 |
| E-mail | 6 |
| Radio | 6 |
| Colour | 3 |
| Time | 2 |
| **Total** | **1,305** |

| Property | Count |
|---|---:|
| Carry a caption | 1,038 (80%) |
| Marked `required` in markup | **240** |
| Declare `min` and/or `max` | 146 |
| Declare `maxlength` | 12 |
| Use the `select2` searchable lookup | **206** |
| `readonly` | 5 |
| Model-bound (`asp-for`) | 28 |

Each entry records: `id`, control, type, bilingual label, bilingual placeholder, `required`,
`readonly`, `disabled`, `min`, `max`, `step`, `maxlength`, `lookup`, `model_binding`, and the
screen it belongs to.

### 1.1 What the numbers mean for the manual

**206 select2 lookups** — a fifth of every drop-down is a searchable lookup, not a plain list.
The manual's "shared interface conventions" chapter needs one illustration of it, because the
interaction differs: you type to filter rather than scroll. *(A note from this codebase's own
history: a `select2` control does not raise the native `change` event, so any instruction of the
form "the page updates as soon as you choose" must be verified per screen rather than assumed.)*

**Only 12 fields declare `maxlength`** against 456 text inputs. Length limits are therefore
enforced by the database and the business layer, not by the browser — so the user meets them as a
save-time error rather than as a typing limit. The manual should not promise "you cannot type more
than N characters".

**240 required fields out of 1,305 (18%)** is the *markup* count. Server-side requirements are
larger and live in the business layer — see §3 and `messages-catalog.csv`.

### 1.2 Two extraction defects found and fixed — worth knowing about

Both were caught by inspecting output rather than trusting it, and both would have put wrong text
in the manual:

1. **Checkbox captions were shifting by one field.** The Metronic form-check puts the caption in a
   `<span>` *after* the input, inside a `<label>` that opened *before* it, so a "nearest preceding
   label" rule attached each caption to the previous control. `TrackExpiry` was captioned *"Track
   batch"* and `TrackSerial` *"Track expiry"* — the two fields that decide whether stock is
   batch- or serial-tracked. Now read from the span.
2. **Unlabelled controls inherited a distant caption.** A page-size selector was captioned
   *"Status"* because that was the last label above it. A 400-character proximity cap now applies,
   and a field with no caption is recorded as having none rather than being given a wrong one.

**267 fields (20%) have no caption in the markup.** They are recorded as `label: null`. Most are
icon-adjacent or table-inline controls whose meaning comes from their column. Those need a human
to name them, and they are listed in `coverage-and-gaps.csv`.

---

## 2. Actions — what was captured

| | Count |
|---|---:|
| Total actions | **1,475** |
| Primary (`btn-primary`) | 326 |
| Destructive (`btn-danger` / `btn-light-danger`) | **104** |
| Success | 11 |
| Neutral | 1,034 |
| Submit a form | 88 |
| Open a modal dialog | **130** |
| **Icon-only, no text label** | **603 (41%)** |

Most common labels: `Cancel` 87 · `Save` 79 · `Back` 40 · `Reset` 27 · `Add` 19 · `New` 17.
Most common icon-only buttons: trash 75 · pencil 30 · eye 25 · cross 22 · plus 19.

### 2.1 41% of controls have no text

This is the single most important finding in this document for a manual writer.

Six hundred and three buttons carry an icon and nothing else. A manual cannot write *"press
Delete"* when the screen shows only a bin. Every one is catalogued with its icon name
(`(icon only: trash)`), which is enough to write *"press the bin icon at the end of the row"* —
but the **icon-only controls must be illustrated, not just named.** The screenshot programme
(document 08) exists largely for them.

### 2.2 Confirmation uses a project dialog, not the browser's

**23 confirmation prompts** were found across the views, in two shapes:

```js
confirm('@Localizer["Delete this message?"]')                       // 4 — the browser's own
confirm({ type: 'delete', text: …Localizer["Post this stock write-off?…"]… })   // the project's
```

| Dialog type | Prompts | What it signals |
|---|---:|---|
| `edit` | 9 | A save that needs confirming — *"Save settings?"*, *"Save item locations?"* |
| `native` (browser `confirm()`) | 7 | Plain yes/no |
| `delete` | 4 | Destructive |
| `generic` | 2 | e.g. *"Post these opening GL balances?"* |
| `goingActive` | 1 | Chosen at runtime between save and reject |

The `type` decides the dialog's colour and its confirm-button wording, so the manual's conventions
chapter can describe the three treatments once and then refer back to them.

**14 of the 23 build their text in JavaScript at runtime**, so the wording cannot be read from
source. They are recorded as existing, with their dialog type, and their exact words have to be
captured from the screen.

> **A first pass reported "zero confirm() prompts".** It searched only for a quoted string literal
> and so matched `@Localizer[` as the message on six of them. The correction is recorded here
> rather than quietly fixed, because the wrong version would have told a manual writer that
> nothing in this product asks before deleting.

What is still unknown: **which of the 104 destructive buttons is covered by one of these 23
prompts.** 130 buttons open a modal, and a modal may itself be the confirmation. Establishing the
mapping needs the buttons pressed — document 12.

### 2.3 The product is command-driven, not form-driven

Measured across the controllers:

```
form-then-save action pairs (GET form → POST same name)       15
POST actions with no matching GET form (command endpoints)   390
```

Only 15 screens follow the classic show-form / post-form pattern. **390 actions are commands
posted by JavaScript** from a button or a modal, with the row updating in place.

For the manual this changes the shape of every instruction. The step is not *"fill the form and
press Save, then the page reloads"*; it is *"press the button, the dialog closes, the row
changes"*. 49 screens were identified as AJAX-driven for exactly this reason, and the manual's
conventions chapter should state the pattern once: **a mutation updates the row; the page does not
reload.**

---

## 3. What this dictionary cannot tell you

The brief asks, for each action: when it is available, what must be supplied, what it confirms,
the resulting status, related records, and whether it can be reversed. Here is the honest split.

| Asked for | Status |
|---|---|
| Exact AR/EN labels | ✅ **complete** — 2,780 entries |
| Required / optional, type, format, min, max, defaults | ✅ from markup; ⚠ server-side rules are separate |
| Lookup sources | ⚠ the control is identified (`select2`); **the source list behind it was not traced** |
| Conditional visibility / editability | ❌ **not established** — decided server-side and in JavaScript |
| Calculated and read-only values | ⚠ 5 fields are `readonly` in markup; calculated values are computed in the business layer |
| When an action is available | ❌ **not established** |
| Confirmation prompts | ⚠ 23 found, with their dialog type; 14 build their text at runtime and **were not read**; which destructive buttons they cover is **not established** |
| Resulting status | ⚠ the vocabulary is known (52 values, document 06); which action produces which is **not traced per action** |
| Whether it can be reversed | ⚠ known for the document types with `Reversed`/`Cancelled`/`Void` events; **not established generally** |

### 3.1 Validation messages are catalogued separately

223 user-facing messages are in `messages-catalog.csv`, classified:

| Kind | Count |
|---|---:|
| Business-rule refusals (thrown by the business layer) | 80 |
| Validation messages | 50 |
| Failure messages | 35 |
| Empty states | 32 |
| Confirmation prompts | 23 |
| Permission refusals | 3 |

**The 80 business-rule refusals are the real validation rules** — the ones a user meets when a
posting, a period close or a stock movement is refused. They are English source strings thrown
from the business layer; where a screen localises them it does so at the controller. The Arabic
manual must quote what the Arabic screen shows, which for these is **not guaranteed to be Arabic**
— flagged in `messages-catalog.csv` and in document 10.

### 3.2 The high-risk actions the brief singles out

The brief asks for particular attention to posting, cancellation, deletion, approvals, period
closing, stock movements, payroll and financial adjustments. Status for each:

| Area | What is known | What is missing |
|---|---|---|
| **Posting** | 21 services post to the ledger; `Posted` is a status; `JournalEntry.Reversed` is the only journal event | Which button posts, what it validates, what the user sees on success |
| **Cancellation / reversal** | `Cancelled`, `Reversed`, `Void`, `Voided` are all assigned in code | **`Void` and `Voided` are two spellings of one idea** — the manual must not present them as two states |
| **Deletion** | 104 destructive buttons; 23 confirmation prompts exist, 4 of them `type: 'delete'` | Which button maps to which prompt |
| **Approvals** | 4 silos: Leave, Request, Inventory, ProjectBilling | The approve/reject interaction itself |
| **Period close** | Two dedicated permissions, `period-close` and `period-reopen`; the source documents the sequence `Open → SoftClosed → Closed` and reopening as requiring a recorded reason | The screens and messages involved |
| **Stock movements** | `StockService` is the authority | Stock emits **no** platform events, so there is no event trail to describe |
| **Payroll** | The computed line is fully known: base, allowances, gross, late minutes, overtime minutes, absent days, overtime pay, late penalty, absence penalty, employee SI, company SI, tax, net, and `EarningExpense = gross + overtime − latePenalty − absencePenalty` | The run itself; `/Accounting/Payroll` timed out twice during capture |
| **Financial adjustments** | FX revaluation, depreciation runs and year-end close exist as screens | Their sequences |

Every "what is missing" cell has the same cause: **performing the action would change data.**
Document 12 proposes how to close them on a disposable database.
