# 21 — Dialogs

Use a dialog for a short focused decision or edit that should preserve page context. Use a page for long, multi-section, navigable or linkable work; use a drawer for contextual detail/composition.

## Sizes

Small confirmation 400–480 px; standard form 600–720 px; large structured form up to 960 px. On small screens dialogs become near-full-screen with safe-area padding and persistent actions.

## Anatomy and behavior

Accessible title, optional description, body, error summary, and footer with secondary cancel then primary action in logical order. Focus moves inside, is trapped, and returns to the trigger. Escape closes only when safe; backdrop click must not discard dirty/critical work.

Consequential actions use the shared `CBConfirm` contract and specify object/action/impact. Never stack two modals. Close menus/popovers before opening a dialog. A destructive confirmation uses danger styling only for the confirming action.
