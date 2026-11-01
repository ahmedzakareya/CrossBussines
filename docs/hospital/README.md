# Hospital module

A hospital vertical inside CrossBusiness, on the same application and port as every other system,
reusing Inventory, Accounting, HR, Tasks, Report Studio and the Client Portal rather than duplicating them.

## Scope ruling (owner, 2026-11-01)

- The largest scope, able to shrink by setting: a facility type (clinic, polyclinic, day-surgery centre,
  general hospital) switches modules on or off, and each module can then be changed on its own.
- Country-driven from settings: currency (decimals included), tax treatment, e-invoicing, e-claims,
  identity document and coding system come from a country pack. Countries and currencies come from the
  existing `CountriesLookup` and `Currency` tables. Country tax and e-claims values are editable defaults
  to confirm with the facility's advisers, not legal statements.
- Arabic and English throughout; every shown name is a bilingual pair.
- Floors, rooms and their interior layout are drawn by the admin, like every other setting.

## Prototype

`prototype/` is a static, clickable prototype (42 screens, sample data only) built on the app's real
Metronic 8.2.3 bundles and `crossbuy-brand.css`. It is the approved design reference for the build.

Run `prototype/start.cmd`. On first run it copies the theme bundles from `CrossBuy/wwwroot/Backend-assets`
into `prototype/assets/` (ignored by git, ~18 MB), then serves http://localhost:8765 with caching off.
The module blueprint screen inside the prototype lists what is reused, the new entities, the gaps found by
the code review, the decisions still open, and the phased roadmap.

## Build order

- Wave 0: governance and foundation (module ownership, the controller-count freeze, a clinical permission
  scope, SQL slices, a record-access log, variable payroll lines), hospital settings and country pack.
- First build unit: facility structure: room types, floors, rooms, beds and the floor/room designer.
