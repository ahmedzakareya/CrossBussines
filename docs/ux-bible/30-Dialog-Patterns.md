# 30 — Dialog Patterns

Use Metronic/Bootstrap modal for short blocking decisions and focused forms; use a Metronic drawer for contextual detail/filter/longer nonblocking work; use a page for complex or deeply linkable workflows. Never nest dialogs.

Dialog anatomy: descriptive title, concise context/target, content, validation/status, secondary action, primary action, close. Focus enters at title/first useful control, is trapped correctly, Escape closes only when safe, and returns to invoker. Backdrop click must not discard consequential input. Destructive primary states target and consequence and uses danger semantics.

Disabled explains unmet prerequisites; loading prevents duplicate action; error stays in dialog with input preserved; success closes only after confirmation and announces the page update. On mobile, long dialogs become full-screen. RTL uses logical action order consistently. Motion follows 180ms enter/140ms exit and reduced-motion rules.
