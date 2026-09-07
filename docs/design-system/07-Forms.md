# 07 — Forms

Labels are visible and persistent; placeholders are examples, not labels. Required fields use text/symbol plus validation semantics. Help precedes error; errors appear adjacent to the field and in an accessible summary for long forms.

## Controls

- Standard height: 40 px; compact tables/filters: 36 px; touch-critical: at least 44 px.
- Use `form-control`/`form-select`; Select2 for searchable remote lookups; Tagify only for genuine multiple tokens.
- Amount fields are end-aligned, tabular, currency-labeled, and never assume two decimals.
- Dates use locale-aware display with stable server values; date range exposes both bounds.
- A switch changes a setting; a checkbox selects; radio chooses one mutually exclusive option.

## Behavior

Disable submit during processing and retain entered data on recoverable errors. Dirty forms warn before navigation. Server validation is authoritative. Dependent fields explain why they are disabled. Lookup results show enough identity to disambiguate records. Destructive edit actions use the shared confirmation contract, never `window.confirm`.

## Layout

One column by default; two columns only for short related fields. Long text, lookup, validation, and financial totals span full width. Group fields with headings rather than nested cards.
