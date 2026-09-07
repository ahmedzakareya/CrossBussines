# CrossBusiness Platform Design System

**Status:** Official architecture and design-governance source of truth  
**Scope:** Web, responsive web, mobile, embedded views, operational screens, and future clients  
**Framework baseline:** Metronic 8 + Bootstrap 5 + Keen Icons  
**Platform identity:** CrossBusiness Blue

This system standardizes the product already on disk. It does not replace Metronic, redesign business workflows, or erase specialized POS, KDS, manufacturing, storefront, and field-operation UX. It gives those surfaces one vocabulary for color, type, spacing, state, accessibility, and interaction.

## Authority order

1. Security, authorization, localization, and domain rules remain authoritative over presentation.
2. `29-Tokens.json` is the canonical machine-readable token source.
3. `28-CSS-Tokens.css` is the reference CSS projection of those tokens.
4. Documents 01–34 define semantic use, component behavior, and governance.
5. Metronic/Bootstrap implement primitives; CrossBusiness tokens and rules decide their use.
6. Existing production UI remains legacy-compatible until migrated through `31-Migration-Guide.md`.

## Non-negotiable principles

- Blue is platform chrome and primary action. Financial green is domain-semantic, never platform primary.
- A visible control is not authorization. A hidden control is not authorization. The server remains authoritative.
- Every component has loading, empty, unavailable, denied, partial, error, and success behavior where applicable.
- Arabic/RTL and English/LTR are equal release targets. French is supported where resources exist.
- Prefer Metronic/Bootstrap primitives, shared partials, and future ViewComponents over view-local reinvention.
- Operational density is allowed when the task demands it; visual inconsistency is not.
- Accessibility, keyboard behavior, responsive behavior, and localization are acceptance criteria.

## Evidence baseline

The source audit found 13 layouts; 327+ Razor views; 1,940 card-class occurrences; 364 table-class occurrences; 233 responsive-table wrappers; 1,740 buttons; 504 modal-class occurrences; 786 badges; 569 Select2 references; 108 flatpickr references; 54 jsTree references; 36 ApexCharts references; and 22 FullCalendar references. There are 1,187 inline `style` attributes across 226 views. Evidence includes `Views/Shared`, Workspace, Reports, Accounting, Inventory, CRM, Tasks, Calendar, Comm, POS/PosApp/Hyper, Project, People/Admin, `crossbuy-brand.css`, `crossbusiness-workspace.css`, and `crossbusiness-reporting.css`.

## How to use this set

- Start with `30-UI-Rules.md` and the domain document for the screen.
- Select components from `32-Component-Catalog.md`.
- Use tokens from `29-Tokens.json`; do not introduce raw hex, spacing, shadow, or radius values.
- Check the screen state and responsive requirements in `33-Screen-Catalog.md`.
- Apply the review gates in `34-Design-Governance.md`.

## Delivery status

This is a documentation contract. `28-CSS-Tokens.css` is reference material under `docs/`; it is not wired into production. Migration requires separately approved implementation work.
