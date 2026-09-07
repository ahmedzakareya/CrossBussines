# 25 — Workspace Patterns

Workspace patterns compose cross-module work without taking ownership from source modules.

- **Attention panel:** ordered, bounded queue with source, age, status and direct action.
- **Schedule panel:** today/next events with timezone and Calendar link.
- **Mention/activity panel:** actor, verb, object, time and read state.
- **Saved report panel:** report name, scope, freshness and open action.
- **Quick action:** authorized shortcuts only; never bypass required workflow.
- **Panel state:** use `Views/Shared/_WorkspacePanelState.cshtml` as implementation evidence and converge on common loading/empty/error anatomy.

Panels refresh independently, preserve page stability, and link to authoritative records. Aggregation obeys company/module permissions. Do not copy edit forms, entire tables or communication threads into a widget. Workspace-specific motion follows `22-Motion-Guidelines.md`.
