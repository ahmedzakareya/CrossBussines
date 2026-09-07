# 02 — UX Principles

1. **Context before action.** Show company, branch, period, record, and permission scope before consequential actions.
2. **Metronic first.** Adopt its component anatomy and lifecycle; extend via tokens and domain composition.
3. **One dominant job.** A screen has one page-level primary action; row and contextual actions remain secondary.
4. **Progressive disclosure.** Summary first, detail on demand; avoid walls of simultaneous controls.
5. **Trust through state.** Loading, empty, unavailable, denied, validation, error, partial, and success are designed states.
6. **Safe by design.** Destructive actions name their target and consequence; irreversible work requires explicit confirmation.
7. **Business density, not clutter.** Tables and POS can be compact while preserving scanning, target size, and hierarchy.
8. **Keyboard and touch parity.** Common flows work without a pointer and critical field flows work on touch.
9. **Bidi parity.** Layout uses logical direction; data such as codes and amounts keeps the direction that supports reading.
10. **Performance is UX.** Preserve input, render stable skeletons, paginate/virtualize large sets, and disclose background work.
11. **Authorization is explicit.** Disabled, unavailable, and denied are distinguished; the server is authoritative.
12. **Evolution is measured.** Do not expand legacy inconsistencies; migrate by shared primitive and screen family.

Apply these to existing `Views/Accounting`, `Views/Inventory`, and `Views/Crm` estates as migration criteria—not proof they currently comply.
