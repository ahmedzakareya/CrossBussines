# 23 — Accessibility

Target WCAG 2.2 AA for all standard surfaces.

## Required gates

- Semantic landmarks and heading order.
- Complete keyboard operation with visible focus.
- 4.5:1 normal-text and 3:1 large-text/UI contrast.
- 44×44 px touch target where touch is primary; minimum 36×36 compact desktop control.
- Labels, descriptions and errors programmatically associated.
- Dialog focus trap/return; menus, tabs, trees, grids and comboboxes follow ARIA patterns.
- Status never conveyed by color alone.
- Live regions for async results, errors and progress.
- Zoom/reflow through 200% without loss of function; text spacing remains usable.
- Reduced motion supported.
- Charts and complex visuals have a textual/table alternative.

Automated checks are necessary but insufficient. Each new component receives keyboard, screen-reader, contrast, zoom, RTL and mobile manual testing. Authorization-denied state must not leak inaccessible record details into DOM, labels or tooltips.
