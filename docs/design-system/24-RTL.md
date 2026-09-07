# 24 — RTL

Arabic/RTL is an architectural mode, not a final CSS override.

## Rules

- Set `dir` and language from the same culture source.
- Load the matching Metronic RTL bundle.
- Author with logical properties and flex/grid; avoid physical left/right.
- Mirror directional navigation icons; do not mirror logos, media controls, charts’ time direction, numbers, codes or physical diagrams without meaning.
- Amounts and identifiers remain intelligible with Unicode bidi isolation where mixed.
- Table numeric alignment follows value semantics, not page direction.
- Select2, flatpickr, Tagify, FullCalendar, DataTables and tree/menu popovers require RTL acceptance coverage.
- Arabic text is not forced uppercase and uses Cairo metrics.

Do not maintain separate custom LTR/RTL component stylesheets. One logical stylesheet is the target. Validate truncation, focus order, drawers, action order, pagination arrows, breadcrumbs and mixed-language content in both directions.
