# 14 — Construction

Construction uses the standard Blue app shell and project-scoped context. It extends existing Projects/BOQ patterns rather than creating a separate ERP.

## Core patterns

- Project context header: project, contract, client, site, currency, period and commercial state.
- BOQ tree/table: hierarchical code, description, unit, quantity, rate, amount, revision state.
- Certificate/progress workflow: draft → submitted → reviewed → approved/returned → posted, with visible authority and audit.
- Variation/subcontract cards: value, cap/utilization, approval, linked documents and commercial impact.
- Site operations: mobile-first daily report, RFI, photos/files, weather, labor/equipment and offline state when approved.

## Rules

Financial green appears only for financial meaning; project health uses semantic statuses. Revised values show original/current/delta with sign and currency. Hierarchies need keyboard expansion, persistent path and virtualization for scale. Posting remains owned by Accounting and stock movement by Inventory; UI never implies Construction bypasses those writers.
