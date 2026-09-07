# CrossBusiness UX Bible

**Status:** Official Phase 2 UX authority  
**Foundation:** Metronic 8 + Bootstrap 5 + KeenIcons  
**Design authority:** `../design-system/`  
**Scope:** How CrossBusiness screens look, behave, respond, fail, and evolve

This Bible sits **above** Metronic. It neither forks Metronic nor creates a component framework. Use a licensed Metronic primitive first, configure it with Bootstrap utilities, apply CrossBusiness design tokens, and add domain behavior only where the product requires it.

## Authority and decision order

1. Security, authorization, company isolation, audit, and domain rules.
2. Approved product/roadmap decisions in `../roadmap-v2/`.
3. Machine tokens in `../design-system/29-Tokens.json` and rules in `../design-system/30-UI-Rules.md`.
4. This Bible for information architecture, interaction, state, and screen composition.
5. Metronic/Bootstrap/KeenIcons as the implementation vocabulary.
6. Existing screens remain compatible until an approved migration; disk evidence does not automatically become a preferred pattern.

## Required workflow

For a new or changed screen: classify the screen family in `03-Information-Architecture.md`; choose its shell in `36-Layout-Patterns.md`; complete the contract in `38-Screen-Blueprints.md`; select existing components using `37-Component-Usage-Rules.md`; cover states 31–34; then pass `40-UX-Governance.md`.

## Universal screen contract

Every screen record and implementation must define: purpose, target users, business goal, layout, sections, widgets, navigation, server-enforced permissions, filters, primary/secondary actions, loading, empty, unavailable, access-denied, validation, error and success states, responsive order, RTL, dark-mode behavior, accessibility, keyboard path, expected flow, performance budget, and future evolution. `38-Screen-Blueprints.md` applies this contract to every current screen family; module chapters provide domain detail.

## Evidence baseline

The audit covers all `CrossBuy/Views` folders (327+ Razor views), the 24 files in `Views/Shared`, and assets under `wwwroot`. Representative evidence: `Views/Workspace/Index.cshtml`, `Views/Reports/Index.cshtml`, `Views/Reports/Viewer.cshtml`, `Views/Calendar/Index.cshtml`, `Views/Tasks/Index.cshtml`, `Views/Comm/Inbox.cshtml`, `Views/Accounting`, `Views/Inventory`, `Views/Crm`, `Views/People`, `Views/Project`, `Views/Pos`, `Views/PosApp`, and `Views/Hyper`. The implemented ecosystem includes Metronic/Bootstrap conventions plus Select2, flatpickr, jsTree, ApexCharts, and FullCalendar.

## Status vocabulary

Use only: **reference**, **legacy-compatible**, **implemented/not activated**, **architecture-only**, **planned**, **blocked**, or **deprecated**. A view on disk proves implementation presence, not usability, authorization correctness, activation, or verification.

## Non-negotiables

- CrossBusiness Blue owns platform chrome; financial green is restricted to financial meaning.
- Arabic/RTL and English/LTR are equal targets; localization is never simulated with hard-coded translated strings.
- A hidden control is not authorization; access is enforced server-side.
- One page-level primary action; no nested modal; maximum two card layers.
- No infinite spinner and no state conveyed by color alone.
- New components require proof that Metronic cannot supply the primitive.

This delivery is documentation only. It does not activate tokens or alter production CSS, JavaScript, Razor, or configuration.
