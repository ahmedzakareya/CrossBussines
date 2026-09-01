using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    /// <summary>
    /// DEFECT A — the strict timeline presentation for the Task and CalendarEvent families.
    ///
    /// WHAT WAS BROKEN. EntityRegistry has marked both families SupportsTimeline = true since they were
    /// onboarded, so TimelineProjectionConsumer took TimelineEventPresenter.TryPresent — the STRICT path —
    /// and every event answered "no timeline presentation registered". On CrossBuyDev that was 512 dispatch
    /// rows sitting at Attempts = 5 (Task.Created 159, Task.Assigned 159, Task.StatusChanged 109,
    /// Task.BecameOverdue 37, Task.Completed 36, and 12 CalendarEvent.*), while NotificationProjection for
    /// the very same events was Done. The read path hid it: TimelineProjectionService renders through the
    /// LENIENT Present(), which falls back to the canonical action name, so screens looked plausible while
    /// the durable projection failed on a loop.
    ///
    /// These tests assert the strict path specifically. A test that only exercised Present() would pass
    /// against the broken build, because the lenient fallback never fails.
    /// </summary>
    public class TaskCalendarTimelinePresentationTests
    {
        private static string Json(object o) => JsonSerializer.Serialize(o, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        private static TimelineEventPresenter.Presentation Strict(string eventType, object? payload = null, int version = 1)
        {
            bool ok = TimelineEventPresenter.TryPresent(
                eventType, version, payload == null ? null : Json(payload), out var presentation, out var error);

            Assert.True(ok, $"strict presentation refused '{eventType}': {error}");
            Assert.Null(error);
            Assert.NotNull(presentation);
            return presentation!;
        }

        // =========================================================================================
        // Every declared event type renders — the regression itself
        // =========================================================================================

        public static IEnumerable<object[]> TaskEventTypes() => new[]
        {
            new object[] { TaskEvents.Created }, new object[] { TaskEvents.Assigned },
            new object[] { TaskEvents.Reassigned }, new object[] { TaskEvents.StatusChanged },
            new object[] { TaskEvents.DueDateChanged }, new object[] { TaskEvents.BecameOverdue },
            new object[] { TaskEvents.Completed }, new object[] { TaskEvents.Reopened },
            new object[] { TaskEvents.Cancelled },
        };

        public static IEnumerable<object[]> CalendarEventTypes() => new[]
        {
            new object[] { CalendarEventEvents.Created }, new object[] { CalendarEventEvents.Updated },
            new object[] { CalendarEventEvents.Rescheduled }, new object[] { CalendarEventEvents.Cancelled },
            new object[] { CalendarEventEvents.AttendeeAdded }, new object[] { CalendarEventEvents.AttendeeRemoved },
            new object[] { CalendarEventEvents.ReminderTriggered }, new object[] { CalendarEventEvents.Started },
            new object[] { CalendarEventEvents.Completed },
        };

        [Theory]
        [MemberData(nameof(TaskEventTypes))]
        public void Every_declared_task_event_has_a_strict_presentation(string eventType)
        {
            var p = Strict(eventType, new { title = "ZZ task", newStatus = "InProgress", previousStatus = "New" });

            Assert.False(string.IsNullOrWhiteSpace(p.TitleAr));
            Assert.False(string.IsNullOrWhiteSpace(p.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(p.Icon));
            Assert.False(string.IsNullOrWhiteSpace(p.Color));
        }

        [Theory]
        [MemberData(nameof(CalendarEventTypes))]
        public void Every_declared_calendar_event_has_a_strict_presentation(string eventType)
        {
            var p = Strict(eventType, new { title = "ZZ meeting", organizerId = 7 });

            Assert.False(string.IsNullOrWhiteSpace(p.TitleAr));
            Assert.False(string.IsNullOrWhiteSpace(p.TitleEn));
            Assert.False(string.IsNullOrWhiteSpace(p.Icon));
            Assert.False(string.IsNullOrWhiteSpace(p.Color));
        }

        // The exact five Task types and six Calendar types that were failing on CrossBuyDev.
        [Theory]
        [InlineData("Task.Created")]
        [InlineData("Task.Assigned")]
        [InlineData("Task.StatusChanged")]
        [InlineData("Task.BecameOverdue")]
        [InlineData("Task.Completed")]
        [InlineData("CalendarEvent.Created")]
        [InlineData("CalendarEvent.Updated")]
        [InlineData("CalendarEvent.Rescheduled")]
        [InlineData("CalendarEvent.Cancelled")]
        [InlineData("CalendarEvent.AttendeeAdded")]
        [InlineData("CalendarEvent.AttendeeRemoved")]
        public void The_event_types_that_were_failing_in_CrossBuyDev_now_present(string eventType)
        {
            TimelineEventPresenter.TryPresent(eventType, 1, null, out var presentation, out var error);
            Assert.Null(error);
            Assert.NotNull(presentation);
        }

        // =========================================================================================
        // Content — the row says what actually happened
        // =========================================================================================

        [Fact]
        public void A_task_status_change_names_both_sides_of_the_transition()
        {
            var p = Strict(TaskEvents.StatusChanged, new { previousStatus = "New", newStatus = "InProgress" });

            Assert.Contains("New", p.DescriptionEn);
            Assert.Contains("InProgress", p.DescriptionEn);
        }

        [Fact]
        public void A_calendar_update_lists_changed_field_names()
        {
            var p = Strict(CalendarEventEvents.Updated, new { organizerId = 7, changedFields = new[] { "Location", "Title" } });

            Assert.Contains("Location", p.DescriptionEn);
            Assert.Contains("Title", p.DescriptionEn);
        }

        [Fact]
        public void A_missing_payload_still_presents_rather_than_refusing()
        {
            // Strict is about the CONTRACT, not about completeness: an event whose payload carries only some
            // fields must still render. Refusing here would fail dispatch rows for perfectly valid events.
            var p = Strict(TaskEvents.Created, payload: null);
            Assert.False(string.IsNullOrWhiteSpace(p.TitleEn));
        }

        // =========================================================================================
        // PAYLOAD SENSITIVITY
        // =========================================================================================

        [Fact]
        public void A_cancelled_calendar_event_never_renders_the_free_text_reason()
        {
            // The reason is free text a user typed about why a meeting was called off; it can name people or
            // state something that is nobody else's business, and a timeline row is read by everyone who can
            // see the entity. The FACT of cancellation is the event.
            const string secret = "CONFIDENTIAL-REASON-ZZ";
            var p = Strict(CalendarEventEvents.Cancelled, new { organizerId = 7, title = "ZZ meeting", reason = secret });

            Assert.DoesNotContain(secret, p.DescriptionEn ?? "");
            Assert.DoesNotContain(secret, p.DescriptionAr ?? "");
            Assert.DoesNotContain(secret, p.TitleEn);
            Assert.DoesNotContain(secret, p.TitleAr);
        }

        [Fact]
        public void Attendee_and_assignee_identifiers_are_not_printed_into_the_row()
        {
            var added = Strict(CalendarEventEvents.AttendeeAdded, new { organizerId = 7, attendeeEmployeeId = 987654 });
            Assert.DoesNotContain("987654", added.DescriptionEn ?? "");
            Assert.DoesNotContain("987654", added.DescriptionAr ?? "");

            var assigned = Strict(TaskEvents.Assigned, new { newAssigneeId = 987654 });
            Assert.DoesNotContain("987654", assigned.DescriptionEn ?? "");
            Assert.DoesNotContain("987654", assigned.DescriptionAr ?? "");
        }

        [Fact]
        public void An_attendee_count_is_shown_but_the_attendee_list_is_not_in_the_payload_contract()
        {
            var p = Strict(CalendarEventEvents.Created, new { organizerId = 7, title = "ZZ", attendeeCount = 4 });
            Assert.Contains("4", p.DescriptionEn ?? "");
        }

        // =========================================================================================
        // STRICT CONTRACT — unknown events and future payloads are still refused
        // =========================================================================================

        [Fact]
        public void An_unregistered_event_type_is_still_refused_by_the_strict_path()
        {
            // The fix must widen the vocabulary, NOT turn the strict consumer into a catch-all. An event type
            // nothing declares must still fail its dispatch row and become visible to an operator.
            bool ok = TimelineEventPresenter.TryPresent("Task.NotARealTransition", 1, null, out var presentation, out var error);

            Assert.False(ok);
            Assert.Null(presentation);
            Assert.Contains("no timeline presentation registered", error!);
        }

        [Fact]
        public void An_unrelated_unknown_family_is_still_refused()
        {
            Assert.False(TimelineEventPresenter.TryPresent("Unicorn.Invented", 1, null, out _, out var error));
            Assert.Contains("no timeline presentation registered", error!);
        }

        [Theory]
        [InlineData("Task.Created")]
        [InlineData("CalendarEvent.Created")]
        public void A_payload_version_from_the_future_is_refused_rather_than_guessed(string eventType)
        {
            bool ok = TimelineEventPresenter.TryPresent(eventType, 99, "{}", out var presentation, out var error);

            Assert.False(ok);
            Assert.Null(presentation);
            Assert.Contains("payload version 99", error!);
        }

        [Fact]
        public void A_payload_that_is_not_valid_json_for_its_version_is_refused()
        {
            bool ok = TimelineEventPresenter.TryPresent(TaskEvents.Created, 1, "{ this is not json", out _, out var error);

            Assert.False(ok);
            Assert.NotNull(error);
        }

        // =========================================================================================
        // REPLAY / IDEMPOTENCY — presentation is a pure function
        // =========================================================================================

        [Fact]
        public void Presenting_the_same_event_twice_yields_the_same_row()
        {
            // A replayed dispatch row must project the same text. The presenter reads only its arguments — no
            // clock, no culture, no database — so a replay cannot drift.
            var payload = new { title = "ZZ task", previousStatus = "New", newStatus = "Done" };

            var first = Strict(TaskEvents.StatusChanged, payload);
            var second = Strict(TaskEvents.StatusChanged, payload);

            Assert.Equal(first.TitleAr, second.TitleAr);
            Assert.Equal(first.TitleEn, second.TitleEn);
            Assert.Equal(first.DescriptionAr, second.DescriptionAr);
            Assert.Equal(first.DescriptionEn, second.DescriptionEn);
            Assert.Equal(first.Icon, second.Icon);
            Assert.Equal(first.Color, second.Color);
        }

        [Fact]
        public void The_lenient_read_path_and_the_strict_path_now_agree_for_these_families()
        {
            // Before the fix these disagreed: strict refused, lenient fell back to the bare action name. That
            // divergence is exactly what let a broken projection look healthy on screen.
            foreach (var eventType in new[] { TaskEvents.Created, CalendarEventEvents.Created })
            {
                var payload = Json(new { title = "ZZ" });
                Assert.True(TimelineEventPresenter.TryPresent(eventType, 1, payload, out var strict, out _));
                var lenient = TimelineEventPresenter.Present(eventType, 1, payload);

                Assert.Equal(strict!.TitleEn, lenient.TitleEn);
                Assert.Equal(strict.DescriptionEn, lenient.DescriptionEn);
            }
        }
    }
}
