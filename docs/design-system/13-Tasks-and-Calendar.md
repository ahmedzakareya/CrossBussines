# 13 — Tasks and Calendar

Tasks and Calendar are existing modules. The design system integrates them; it does not rebuild them.

## Ownership

Tasks owns task state, assignment, priority, due dates, dependencies, checklists, time and task rules. Calendar owns calendar events, attendees, recurrence, availability, reminders, resources and unified scheduling presentation. Communication owns comments, mentions and timeline rendering.

## Task surfaces

List/table, board/Kanban, detail, quick create/edit dialog, timesheet, filters, KPI widgets and Workspace rows. Status chips and progress must agree; overdue is a derived warning, not a new task status. Board cards remain keyboard movable and have a non-drag alternative.

## Calendar

Use FullCalendar’s month/week/day/list views, Blue selection, distinct personal/company scope, accessible event names and explicit all-day semantics. Create/edit dialogs retain the current owner/attendee/location patterns. Color is supplementary; event type and status remain textual.

## Unified agenda

Order by local display time using the shared time contract. Private calendar events may show a busy slot without title/owner. Tasks and events retain their owning links and permissions. Mobile defaults to agenda/list rather than compressing a desktop month grid.
