# 18 — Mobile UX

Mobile is a priority transformation of the same information architecture, not a shrunk desktop page. Use Bootstrap breakpoints and Metronic drawers/offcanvas. Prefer one column, one dominant action, bottom-safe action placement where appropriate, and 44px targets.

Order content: identity/context → state/alert → primary task → critical facts → supporting detail. Tables become prioritized cards unless column comparison is essential; filters use a drawer with applied-count indicator; record tabs may become a section menu; dialogs become full-screen when form length or keyboard requires it.

Never hide an essential action only behind hover, depend on horizontal navigation, or place fixed controls under the software keyboard. Preserve input and scroll position across rotation and back navigation. Test 320px/360px/390px widths, 200% zoom, RTL, long translations, reduced motion, error states and offline/degraded behavior. `Views/PosApp` is evidence of specialized mobile operation, not a universal shell.
