# Stage-Tasks-Calendar-Integration-04 — Business Events

**Contracts defined and tested. Nothing is raised.**
`CrossBuy/BL/TasksCalendar/TaskCalendarIntegrationContracts.cs`

---

## 1. Why nothing is raised in this increment

`RecordAsync` must run inside the caller's ambient transaction immediately before commit **with no swallowing
catch**, and it throws without one. Raising `Task.Created` would make the event kernel a hard runtime dependency of
creating a task.

And an event name is validated against the registry — where neither `Task` nor `CalendarEvent` yet exists. So the
ordering is forced: **registry, then raising**. Contracts now.

## 2. Task events (9, all v1)

`Task.Created` · `Task.Assigned` · `Task.Reassigned` · `Task.StatusChanged` · `Task.DueDateChanged` ·
`Task.BecameOverdue` · `Task.Completed` · `Task.Reopened` · `Task.Cancelled`

## 3. Calendar events (9, all v1)

`CalendarEvent.Created` · `.Updated` · `.Rescheduled` · `.Cancelled` · `CalendarAttendee.Added` · `.Removed` ·
`CalendarReminder.Triggered` · `CalendarEvent.Started` · `.Completed`

**`Rescheduled` is deliberately separate from `Updated`.** Moving a meeting three days and renaming it are not the
same fact: one is worth interrupting every attendee for and the other is not. Collapsing them is how notification
fatigue starts. Asserted by `Reschedule_is_a_separate_event_from_update`.

## 4. The envelope every event carries

`CompanyID` · aggregate id (`TaskID` / `CalendarEventID`) · `ActorID` (null = system, which is meaningful, not
missing) · `OccurredAtUtc` · `CorrelationID` · `SourceModule` — plus `Previous*`/`New*` pairs where a transition has
two sides. Asserted by `Every_business_event_carries_the_full_envelope`.

## 5. Sensitive-data classification

Every field is `Operational`, `Personal` or `Confidential`, and every contract carries a
`DeliberatelyExcluded` list — because an omission that is not stated reads as an oversight the next person "fixes".

**Never in a payload:** task `Description`, attachments, `BillRate`/`CustomerId`, timesheet cost rates, meeting
descriptions/agendas, attendee email addresses, personal-event location.

Asserted by `No_event_payload_carries_a_description_or_an_attachment` and
`No_event_payload_carries_commercial_task_data`.

## 6. All-day is expressed structurally, not by convention

`CalendarEvent.Created` carries **both** `StartUtc`/`EndUtc` (`DateTime?`) **and**
`StartLocalDate`/`EndLocalDate` (`DateOnly?`), plus `IsAllDay` and `TimeZoneID`.

An all-day event has **no** UTC instant — writing it as midnight UTC is precisely the bug that moves a public
holiday to the previous evening. Asserted by
`A_calendar_event_expresses_all_day_as_a_local_date_and_timed_as_utc`.

## 7. Consumers

None are implemented. Timeline, Communication, Reporting and AI consumers belong to their own tabs; the
`ProposedConsumers` column is a request, not a work item for TAB 4.
