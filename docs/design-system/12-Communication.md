# 12 — Communication

This document governs the new Communication Platform. Legacy `BL/Comm` email screens are compatibility surfaces and must not define the future collaboration language.

## Universal collaboration surface

Entity header, visibility label, thread list, comments, composer, mentions, attachments, reactions, participants/watchers, audit/history, and timeline. It may embed in a page or open as a drawer; the same service and permission rules apply.

## Comments

Show author, role/context where useful, timestamp, edited/deleted state, visibility, body, attachments, reactions and permitted actions. Internal notes and customer-visible replies must be explicit types. Soft-deleted content uses an auditable tombstone, not disappearance.

## Mentions

Trigger suggestions with `@`; group results by people/role/entity type; expose enough identity to disambiguate. Keyboard arrows, Enter, Escape and screen-reader announcements are required. Unresolvable role mentions remain unavailable—not silently broadened.

## Chat and attachments

Chat is chronological, with delivery/failure state, retry, unread divider and date separators. Attachments show type, name, size, scan/upload state, download authority and failure recovery. Never infer visibility from file location.

## Timeline

Communication owns unified rendering; business events remain facts. Timeline cards use actor, action, entity, time, visibility and safe metadata. Modules do not create a second general timeline.
