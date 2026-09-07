# 22 — Notifications and Toasts

## Layers

- Inline validation: field-specific.
- Alert/banner: page or workflow state requiring attention.
- Toast: brief confirmation of an action already understood.
- Notification center: durable, user-addressed events.
- Workspace attention panel: delegated summary, not a separate source.

Toasts use success/info/warning/error semantics, plain text, an optional action, and 5–8 second duration; errors with required action persist. Never put critical details only in a toast. Duplicate events coalesce.

Notification rows show unread state, title, safe summary, time, source/entity, visibility and deep link. Mark-read is a real authorized write, not a visual-only shortcut. Event-projected notifications distinguish dispatch failure from the underlying business event.

Use the existing shared confirmation/toastr infrastructure rather than view-local SweetAlert/Toastr configuration.
