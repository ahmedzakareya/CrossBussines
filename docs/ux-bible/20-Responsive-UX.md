# 20 — Responsive UX

Responsive behavior is defined by available content space, not device branding. Bootstrap/Metronic breakpoints are implementation primitives; screen blueprints define what reflows.

| Width posture | Default composition |
|---|---|
| Narrow | one column, drawer filters/nav, full-width critical actions |
| Medium | one/two columns, collapsible context, touch spacing |
| Wide | aside + content, multi-panel where relationships matter |
| Very wide | cap reading/form width; expand analytical comparison intentionally |

At each transition define: content order, retained actions, navigation replacement, table strategy, dialog/drawer choice, sticky regions, and overflow. No breakpoint may remove required context or authorization feedback. Use logical CSS direction via the design-system contract. Validate text reflow at 400%, not only viewport emulation.
