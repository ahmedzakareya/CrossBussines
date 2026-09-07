# 09 — Accounting UX

Accounting spans 66 `Views/Accounting` screens plus `Views/Currency` and `_LayoutAccounting.cshtml`. Financial green is permitted only for finance/accounting meaning; platform navigation and primary actions remain CrossBusiness Blue.

## Contract

Financial screens foreground entity, period, currency, status, debit/credit balance, posting/audit state, and source document. Lists use tabular numerals and aligned currencies. Journal/document editors maintain line integrity, running totals, validation summary, and explicit draft/post transitions. Statements and aging screens follow Reporting provenance and export rules.

Permissions separate view, create, edit, post, reverse, close period, and export. Locked period, missing exchange rate, imbalance, already posted, denied, and service unavailable have distinct explanations and next steps. Success names the resulting document/status; destructive reversal requires target and consequence confirmation.

Responsive layouts keep totals and critical actions visible; mobile detail may collapse supporting columns but never hides imbalance/status. RTL uses logical layout while account codes, amounts, and equations remain LTR/tabular. Keyboard entry supports predictable row movement without trapping focus. Large ledgers use server filtering/paging. Future evolution should adopt shared report, table, lookup and approval patterns while retaining accounting controls.
