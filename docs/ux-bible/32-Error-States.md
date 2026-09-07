# 32 — Error States

Errors are classified: validation, conflict/stale data, permission/session, connectivity/timeout, dependency unavailable, not found, rate/capacity, and unexpected. Each message states what failed, impact, retained work, safe next action, and reference/time when useful.

Inline error for a field; component banner for local failure; page result for blocking failure; toast only for transient feedback that does not require reading/action. Never expose stack traces, SQL, secrets or sensitive identifiers. Retry must be safe and idempotent; preserve input/filter/context. Access denied is deliberate and nonrevealing, not a generic 500.

Focus moves to an error summary only for blocking form submission; otherwise announce politely. Use icon + text, not color alone. Offline/reconnect status persists while relevant. Repeated background errors consolidate rather than produce toast storms.
