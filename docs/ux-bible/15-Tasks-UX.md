# 15 — Tasks UX

Tasks is an existing module (`Views/Tasks/Index.cshtml`, `Board.cshtml`, `Details.cshtml`, `Hours.cshtml`, `Report.cshtml`). Its list, Kanban, record, time, and reporting views remain domain capabilities.

Task purpose is to make ownership, priority, due state, dependencies and next action obvious. List supports scanning/filtering; board supports stage flow; detail owns complete context, comments/files/history; hours records work; reports summarize without becoming task storage.

Permissions govern visibility, assignment, field edit, stage transition, time entry and deletion separately. Loading uses stable row/card skeletons. Empty is filter-aware. Unavailable preserves draft input. Denied reveals no task detail. Invalid transition and optimistic conflict offer refresh/reconcile. Success announces the exact change.

Kanban drag has keyboard and menu alternatives; movement never relies on color. Mobile favors list and full-screen detail, with board as horizontal lanes only when usable. RTL mirrors lanes consistently. Large boards paginate/virtualize and defer detail. Future integration with Calendar/Workspace/Communication links to the task source of truth.
