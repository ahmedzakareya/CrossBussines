# 10 — Workspace

Workspace is the canonical integrated shell and Blue reference implementation. It is a read-oriented composition surface, not a new source of business truth.

## Panels

My Work, agenda, notifications, mentions, reports, favorites, recent activity, metrics, approvals, and quick actions. Each panel delegates authorization and data scope to its owning module.

## Required states

Data, empty, unavailable, denied, partial, failure, and loading. Empty means the service answered with no items; unavailable means the capability is not activated or reachable. Never collapse them.

## Layout

Primary work column plus attention rail on desktop; one ordered column below `lg`. Metrics reflow with `auto-fit`. Panels use the standard card contract and no more than one nested surface. Quick actions are launch points only and remain server-authorized.

## Current pattern mapping

Evolve `.cbw-card`, `.cbw-row`, `.cbw-state`, `.cbw-metric`, `.cbw-action`, Blue shell/navigation and diagnostics strip into shared components. Preserve the existing module-service delegation and three-state-plus behavior.
