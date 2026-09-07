# 14 — Calendar UX

Calendar is an existing module implemented at `Views/Calendar/Index.cshtml`, using FullCalendar and flatpickr. It must be evolved, not treated as greenfield.

Users view commitments, navigate date ranges, switch supported views, filter calendars, and create/edit authorized events. The current period and timezone are always apparent. Month is overview; week/day supports scheduling; mobile defaults to agenda when grid cells would become unusable.

States cover initial skeleton, empty period, calendar source unavailable, denied private event, conflicting/invalid times, save failure with retained input, and success announced with date/time. Drag/resize is an enhancement: every operation has keyboard/form equivalence and permission checks. Confirm destructive deletion and recurring-event scope.

RTL follows FullCalendar-supported direction and logical controls; event time text remains readable. Dark mode uses tokens, not plugin defaults alone. Load bounded date windows, cancel obsolete requests, and avoid refetching unchanged sources. Future evolution integrates Tasks and Communication through links/events, never duplicated records.
