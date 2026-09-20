# 09 — Troubleshooting and common questions

Real messages, real causes, user-level remedies.

**Machine-readable: `messages-catalog.csv` — 223 messages, each with its kind, its Arabic and
English text where both exist, its evidence level and its source file.**

---

## 1. Where these came from

Three sources, all repository-verified:

| Source | Count | What it is |
|---|---:|---|
| Shared resources whose text reads like a message | 88 | Validation, refusals, empty states, failures |
| Exceptions thrown by the business layer | 80 | The real business rules — what refuses a posting |
| Confirmation prompts in views | 23 | What the product asks before acting |
| *(overlap and classification)* | | |
| **Total** | **223** | |

Classification:

| Kind | Count |
|---|---:|
| Business-rule refusal | 80 |
| Validation | 50 |
| Failure | 35 |
| Empty state | 32 |
| Confirmation prompt | 23 |
| Permission refusal | 3 |

> **No message below was triggered.** Each is the text the product will show; none was observed on
> screen. Two exceptions, both captured: the **HTTP 403 access-denied screen** and the trial
> balance's **"The trial balance is balanced"** notice.

---

## 2. The conditions a user will actually meet

### 2.1 "The fiscal period is closed — posting into it is not allowed"
> **الفترة المالية مقفلة — الترحيل إليها غير مسموح**

**What it means.** The posting date falls inside a period an accountant has closed.
**What to do.** Change the document date to an open period, or ask the accountant to reopen the
period. Reopening needs the `period-reopen` permission **and a recorded reason** — it is not a
silent operation.
**Escalate when:** the period should be open. Only someone holding `period-close` /
`period-reopen` can change it.
**Evidence:** repository-verified, `SharedResources`, translated in all three languages.

### 2.2 "Negative values are not allowed"
> **القيم السالبة غير مسموح بها**

**What it means.** A quantity or amount was entered below zero.
**What to do.** Correct the number. To *reverse* a movement, use the document's reversal path — a
credit note, a debit note or a write-off — not a negative line.
**Evidence:** repository-verified, translated.

### 2.3 "Currency #{0} is undefined or has no decimal precision — silent 2-decimal rounding is not allowed"
> **العملة رقم {0} غير معرّفة أو بلا دقّة عشرية — لا يُسمح بتقريب صامت**

**What it means.** A document names a currency that has no decimal precision configured. The
product refuses rather than guessing at two decimals.
**What to do.** Open **Currencies** and set the precision, then retry.
**Why it is written this way.** Rounding a currency silently is how rounding errors enter a
ledger. The refusal is deliberate.
**Escalate when:** you cannot edit currencies — that needs accounting `manage`.

### 2.4 "Add at least one category and one unit before creating items."

**What it means.** The item master needs classification and a unit of measure before it will
accept an item.
**What you will see.** A warning band on `/Inventory/Items`, and the **Add Product** button
disabled at the same time.
**What to do.** Create one category and one unit first.
**Evidence:** repository-verified; the disabled button is in the same markup.

### 2.5 "No bank or cash account. Create one under «Banks & cash» first."

**What it means.** Payroll disbursement has nowhere to pay from.
**What to do.** Create a bank account or a cash box, then return.

### 2.6 Access denied — HTTP 403

**What you see.** The **Access denied** screen, with the product shell around it.
**What it means.** You are signed in, and this route is not open to your roles.
**What to do.** Ask an administrator for the module role the screen needs. Document 03 lists which
action word each module gates.
**Evidence:** **captured** — one of the two screens observed in a non-200 state.

### 2.7 A menu row that leads nowhere, or a report you cannot find

**What it means.** A report whose permission key is unmapped answers **404, not 403** — by design,
so the catalogue cannot be probed. Its menu row is hidden by the same rule.
**What to do.** Ask an administrator to grant the report permission. `Platform.BusinessEventLog`
is deliberately unmapped on a stock install.
**This is not a broken link.**

### 2.8 Saving an employee refuses on a new installation

**What it means.** HR's `employee-manage` is in the platform's `NeverBootstrapOpen` set: unlike
most permissions it does **not** fall open when nothing has been configured. On the development
install `PlatformRoleAssignments` held **zero rows**.
**What to do.** Grant HR roles first. This is a first-run step, not a fault.
**Evidence:** repository-verified and runtime-viewed (the zero row count).

> **A known presentation defect worth documenting.** This refusal is delivered to an AJAX call.
> If a refusal is answered with a redirect instead of JSON, the browser follows it, receives
> another page with HTTP 200, and the user sees a generic failure with the reason discarded. If a
> save fails with no explanation, this is the shape to suspect.

### 2.9 "…not found" — 32 empty-state messages

`Branch not found` / **الفرع غير موجود**, `Campaign not found` / **الحملة غير موجودة**,
`Category not found`, `Course not found`, `Appraisal not found`, and 27 more.

**What it means.** The record was deleted, or you followed a stale link or bookmark.
**What to do.** Return to the list and open the record from there.

### 2.10 A report runs but is not quite what you asked for

The reporting platform distinguishes **`Warning`** — *"the report was produced, but something the
caller asked for was not honoured"* — from **`Error`** — *"the report was not produced."*

Named warning reasons include `rows_truncated`, `column_dropped`, `sort_dropped`,
`grouping_dropped`, `filter_field_not_filterable`, `parameter_out_of_range`,
`parameter_not_allowed`, `template_not_visible`.

**What to do.** Read the warning before using the numbers. `rows_truncated` in particular means
the figures are incomplete — narrow the parameters and run it again.

### 2.11 You are asked to confirm

23 confirmation prompts exist, in five treatments: `edit` (9), native browser (7), `delete` (4),
`generic` (2), and one chosen at runtime. Examples, verbatim:

- *Delete this message?* / **حذف الرسالة؟**
- *Post this stock write-off? The quantities will be permanently…*
- *Post these opening GL balances?*
- *Remove this labor line and reverse its entry?*
- *Save item locations?*

**14 of the 23 build their wording in JavaScript**, so their exact text must be read off the
screen before the manual quotes it.

### 2.12 A payroll or heavy screen takes a long time

`/Accounting/Payroll` and `/Accounting/PayrollDisbursement` **exceeded a 45-second load timeout**
during capture. They are the slowest screens found.
**What to do.** Wait rather than re-submitting. Re-submitting a payroll run is exactly the kind of
action that should not be repeated on a hunch.
**Evidence:** runtime-measured.

### 2.13 You were not notified about something

Notifications are not instant. The dispatch worker polls every **15 seconds**, batches 50, and
retries 5 times with 30-second backoff.

**And a real limit:** **inventory emits no platform events at all.** A goods receipt, a transfer
or a stock count produces no notification. Money documents, tasks and calendar events do. This is
a product limitation, not a configuration problem, and the manual should say so plainly.

### 2.14 A template will not save in Report Studio

**What it means.** Platform-scope templates are refused: *"Platform templates are created by
deployment, not by a tenant."*
**What to do.** Change **Available to** away from Platform, then save.

### 2.15 A screen you bookmarked returns 404

**What it means.** The default route has **no `action=Index`**, so `/Accounting` is a 404 while
`/Accounting/Index` works. Only seven controllers have a bare-name route (`Comm`, `Calendar`,
`Reports`, `Workspace`, `Chat`, `BusinessEventMonitor`, `Tasks`).
**What to do.** Bookmark the full `/Controller/Action` URL.
**Evidence:** runtime-measured — four such 404s during capture.

---

## 3. Questions the manual should answer, with what is known

| Question | Answer | Evidence |
|---|---|---|
| Why can I not switch company? | You cannot. Your company comes from your employee record. A second company means a second employee record. | Repository-verified |
| Why is my colleague's screen different from mine? | Menu rows are hidden by permission using the same predicate the screen enforces, so a hidden row is one you could not open anyway. | Repository-verified |
| Why is part of the French interface in English? | 19 screens have no English resource and 15 no French; two of the missing partials render on every page. The fallback shows the key, which is written as an English phrase. | Repository-verified |
| Why is an employee's name in Arabic on the English screen? | The English twin column was never filled. Fill both name fields on entry. | Repository-verified |
| Can I undo a posting? | Reversal exists for several document types (`Reversed`, `Cancelled`, `Void`). **Which actions are reversible, and by whom, was not established.** | ⚠ not verified |
| Will it warn me before deleting? | 23 prompts exist; **which of the 104 destructive buttons they cover is not established.** | ⚠ not verified |
| How do I print? | Never from the screen. Register a dataset and use `/Reports/Viewer`. No screen prints its own HTML. | Repository-verified |
| Can I schedule a report by e-mail? | **No.** Built but disabled: the worker is off by design and a null mail sender is registered. | Repository-verified |
| Is my data sent to an AI provider? | **No.** Configured as `Internal` / `LocalLoopback`; the egress policy refuses personal data everywhere and free text to any external processor. | Repository-verified |

---

## 4. What this chapter must not do

Per the brief: **no speculative troubleshooting, and no database instructions.**

Accordingly, nothing here tells a user to run SQL, edit a table, clear a cache or restart a
service. Every remedy is an action a user or an administrator can take **inside the product**, and
where the answer is "ask an administrator" that is said rather than worked around.

## 5. What is still missing

| Gap | Why it matters |
|---|---|
| **No message was seen on screen** except the 403 and the trial-balance notice | A manual should show the banner, not just quote the words |
| **The 80 business-layer refusals are English source strings** | What an Arabic user reads for each was not observed |
| **14 confirmation prompts build their text at runtime** | Their wording cannot be quoted yet |
| **Reversibility per action** | The single most asked support question is unanswered |
| **Which destructive buttons confirm** | Same |
| **Password recovery** | Not covered at all — exercising it sends mail |

All six close the same way: trigger the conditions on a disposable database and capture what
appears. Document 12.
