# 18 — Animation

Motion explains state change; it is never decoration that delays work.

## Durations

- Instant feedback: 80 ms.
- Hover/focus/color: 120 ms.
- Small enter/exit: 180 ms.
- Drawer/modal: 240 ms.
- Complex layout: maximum 320 ms.

Use standard ease-out for entry, ease-in for exit, and ease-in-out for movement. Animate opacity and transform; avoid layout-heavy width/height animation for large regions. Hover lift is at most 2 px and must not move adjacent layout.

Honor `prefers-reduced-motion`: remove parallax, smooth scrolling, repeated pulse and nonessential transforms; keep immediate state feedback. Loading indicators may rotate but require textual state for long operations.
