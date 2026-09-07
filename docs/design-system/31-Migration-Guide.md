# 31 — Migration Guide

Migration is incremental and behavior-preserving. Do not combine visual migration with business-rule changes.

## Phase 0 — Governance

Appoint Design System and Integration owners; freeze token names; add documentation checks; inventory screenshots; define visual-regression baselines.

## Phase 1 — Token adapter

Generate production tokens from `29-Tokens.json`; map them to Bootstrap/Metronic variables; add light/dark/RTL tests. Do not remove legacy green overrides yet. New Blue screens consume the adapter.

## Phase 2 — Shared primitives

Standardize page header, card/panel, state display, buttons, status chip, filters, server table, confirmation/toast, lookup, dialog and attachment. Extract behavior only after two proven uses. Add component demos/tests.

## Phase 3 — Layout consolidation

Move from 13 layouts toward App, Operational and Embed shells. Begin with smallest duplicated layouts and keep route/navigation behavior unchanged. Never rewrite the 4,700-line layouts simultaneously.

## Phase 4 — Platform products

Migrate Workspace and Reporting first because they already use Blue and define cross-platform patterns; then Communication, Security/administration and Construction.

## Phase 5 — Legacy modules

Prioritize shared layouts and high-frequency surfaces: Tasks/Calendar, CRM, Inventory, Accounting, HR/People, Projects. Preserve financial green only inside Accounting semantics. POS/KDS/hyper/manufacturing/storefront retain operational structure while adopting tokens and accessibility.

## Per-screen procedure

1. Capture current behavior, screenshots, roles, languages and widths.
2. Classify shell and catalog components.
3. Replace raw values with semantic tokens.
4. Replace duplicated markup/JS with approved components.
5. Add missing states and accessibility without workflow change.
6. Verify permissions and server behavior unchanged.
7. Test light/dark contract, RTL/LTR, mobile and print/export where relevant.
8. Record exceptions and deprecations.

## Rollback

Each migration slice remains independently reversible at the UI layer. Do not delete the old shared pattern until every consumer is migrated and visual/functional gates pass.
