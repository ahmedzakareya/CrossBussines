# 34 — Notifications

Use four channels: inline status for local context; toast for brief operation feedback; notification center for durable user-relevant events (`Views/Notifications/Index.cshtml`, `_NotificationBell.cshtml`); announcements for broad messages (`_AnnouncementsBanner.cshtml`). Communication messages remain in Communication.

Toast anatomy: semantic KeenIcon, concise outcome, optional action, dismiss. Default 5 seconds; errors/actionable notices persist until dismissed/resolved. Hover/focus pauses timeout. Toasts do not steal focus and announce via appropriate live-region urgency. Deduplicate repeated events and cap visible stack.

Notification center exposes unread state, category/source, time, destination and bulk read actions. Permissions are rechecked on open. Do not expose protected content in previews. RTL mirrors layout; timestamps localize; dark mode uses semantic tokens. Email/push are delivery channels, not substitutes for in-product state.
