# 13 — Communication UX

The **new Communication Platform** is distinct from legacy `Views/Comm`, `Views/Chat`, and document comments. Existing inbox/chat/comment screens are compatibility evidence, not the future domain architecture.

The new platform should compose conversations, participants, messages, mentions, attachments, reactions, read state, delivery state and source-business context. Primary flow: open context/inbox → select thread → read from last meaningful point → compose/attach/mention → send → receive delivery feedback. Business-object links return to the authorized owner module.

Use Metronic card, drawer, dropdown, avatar, badge, form, toast and file primitives. Message chronology is semantic; composer remains reachable; unread does not rely only on color. Pending, sent, failed/retry, edited, removed, unavailable attachment, empty conversation and denied thread are explicit. Do not display content snippets when the user lacks access.

Desktop may use list/thread/details; tablet collapses details; mobile uses route-like panes with a clear back path. RTL mirrors participant and action placement while timestamps/codes retain locale readability. Keyboard supports thread navigation and composer without hijacking standard shortcuts. Virtualize long threads and lazy-load media. Future work starts compliant; legacy Comm/Chat should not be expanded as the platform pattern.
