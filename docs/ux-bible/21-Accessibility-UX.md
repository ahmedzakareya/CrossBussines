# 21 — Accessibility UX

Target WCAG 2.2 AA. Accessibility is a release criterion, including authenticated and operational screens.

Use semantic HTML supplied by Bootstrap/Metronic before ARIA. Every control has a programmatic name; focus is visible; DOM and visual order agree; headings form an outline; landmarks identify shell regions. Contrast meets 4.5:1 for normal text and 3:1 for large text/UI graphics. Color is never the only state cue.

Keyboard: skip link → global controls → local navigation → page heading/actions → content. No trap except a correctly implemented modal; restored focus returns to the invoker. Tables expose headers; trees, tabs, menus, dialogs, comboboxes and calendars follow their expected patterns. Charts provide summary and data/table alternative.

Validation links summary errors to fields; live regions announce async results sparingly. Respect zoom/reflow, forced colors and `prefers-reduced-motion`. Test English LTR and Arabic RTL with keyboard and screen reader. Plugin use (Select2, jsTree, FullCalendar, flatpickr, ApexCharts) requires configuration/testing; library presence does not prove compliance.
