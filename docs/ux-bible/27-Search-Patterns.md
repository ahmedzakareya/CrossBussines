# 27 — Search Patterns

Use four scopes: global platform search, module search, collection keyword search, and lookup/combobox. Label scope and searchable fields; never imply global coverage if results are partial.

Metronic input-group/search and Select2 primitives are preferred. Start keyword search after 2–3 characters where remote, debounce 250–350ms, cancel obsolete requests, and expose Clear. Enter submits; arrows navigate suggestions; Escape closes; focus returns predictably. Results identify object type, primary label, supporting context and authorized destination.

States: idle hint, searching, results count, no match, filter-constrained no match, unavailable, denied category, and recoverable error. Do not leak inaccessible result snippets/counts. Mobile search may become full-screen; RTL mirrors icons/actions while identifiers retain direction. Preserve query on navigation back when safe.
