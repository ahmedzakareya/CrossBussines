using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Calendar;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
    // ==========================================================================================
    // CALENDAR AUTHORIZATION.
    //
    // This service exists because CalendarEvent had PermissionScope = ScopeNone, so the platform
    // denied every write — correctly, since no policy existed. These tests are what make the new
    // policy a policy rather than a claim, and the ones that matter most are the DENIALS.
    //
    // The sharpest rule: an ATTENDEE may READ a meeting but may NOT EDIT it. Being invited is not
    // permission to move it — rescheduling silently changes the event for everyone else invited.
    // ==========================================================================================
    public class CalendarAccessServiceTests
    {
        private const int CompanyOne = 1, CompanyTwo = 2;

        private static BusinessContext Ctx(int employeeId, int companyId) => new()
        {
            CompanyId = companyId, EmployeeId = employeeId, UserId = "user-" + employeeId,
            Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid(),
        };

        private static CalendarAccessService Service(PlatformTestHost host) => new(
            host.Db,
            new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance),
            new OrgHierarchy(host.Db, NullLogger<OrgHierarchy>.Instance),
            NullLogger<CalendarAccessService>.Instance);

        private static async Task<int> EventAsync(PlatformTestHost host, int ownerId,
            string scope = "Personal", int companyId = CompanyOne)
        {
            var ev = new CalendarEvent
            {
                CompanyID = companyId, Title = "Meeting", StartAt = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc),
                EndAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), Scope = scope, OwnerEmpId = ownerId
            };
            host.Db.CalendarEvents.Add(ev);
            await host.Db.SaveChangesAsync();
            return ev.Id;
        }

        private static async Task EmployeeAsync(PlatformTestHost host, int id, int companyId = CompanyOne)
        {
            host.Db.Employee.Add(new Employee
            {
                ID = id, EmpCompanyID = companyId, IsActive = true,
                FirstName = $"Emp{id}", LastName = "T", FullName = $"Emp {id}",
                Address = "-", PhoneNumber = "-", Email = $"e{id}@t.local",
                ProfileImage = "-", Gender = "M", MaritalStatus = "S", UserId = "user-" + id,
            });
            await host.Db.SaveChangesAsync();
        }

        /// The org tree lives in Hierarchicals (H_Type 5 = employee node), NOT in a column on Employee —
        /// which is why the manager rule has to be seeded rather than assumed.
        private static async Task ReportsToAsync(PlatformTestHost host, int managerId, int reportId)
        {
            host.Db.Hierarchicals.Add(new Hierarchical { H_ID = 1000 + managerId, H_Type = 5, H_ObjectID = managerId, H_Parent = null });
            host.Db.Hierarchicals.Add(new Hierarchical { H_ID = 1000 + reportId, H_Type = 5, H_ObjectID = reportId, H_Parent = 1000 + managerId });
            await host.Db.SaveChangesAsync();
        }

        // ---- the scope is real, which is what the startup validator checks --------------------
        [Fact]
        public void The_calendar_scope_is_registered_so_a_role_assignment_can_match_it()
        {
            // A module whose scope is unknown to the registry can never match a role grant, so it would
            // stay bootstrap-open silently. PermissionScopeStartupValidator refuses to boot on that.
            Assert.Contains(EntityRegistry.ScopeCalendar, EntityRegistry.PermissionScopes);
            Assert.True(EntityRegistry.IsKnownScope(EntityRegistry.ScopeCalendar));
        }

        [Fact]
        public void The_service_publishes_the_calendar_scope_and_its_action_vocabulary()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            var svc = Service(host);

            Assert.Equal(EntityRegistry.ScopeCalendar, svc.Scope);
            Assert.Equal(CalendarActions.All.OrderBy(x => x), svc.Actions.OrderBy(x => x));
        }

        // ---- deny by default -------------------------------------------------------------------
        [Fact]
        public async Task An_unknown_action_is_refused()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            int id = await EventAsync(host, ownerId: 10);

            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), "teleport", id));
            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), "", id));
        }

        [Fact]
        public async Task An_event_in_another_company_is_refused_even_to_its_owner()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            int theirs = await EventAsync(host, ownerId: 10, companyId: CompanyTwo);

            // The company comes from the ROW. A caller in company 1 cannot reach a company-2 event
            // even though the owner id matches.
            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Edit, theirs));
            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Read, theirs));
        }

        [Fact]
        public async Task An_absent_event_and_a_foreign_event_are_indistinguishable()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            int foreignId = await EventAsync(host, ownerId: 99, companyId: CompanyTwo);

            // Both false, by the same path — otherwise ids could be probed for existence.
            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Read, foreignId));
            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Read, 987654));
        }

        // ---- the owner rule --------------------------------------------------------------------
        [Fact]
        public async Task The_owner_may_read_and_edit_their_own_event()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            int id = await EventAsync(host, ownerId: 10);

            var svc = Service(host);
            Assert.True(await svc.CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Read, id));
            Assert.True(await svc.CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Edit, id));
        }

        [Fact]
        public async Task A_stranger_may_not_read_someone_elses_personal_event()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            await EmployeeAsync(host, 11);
            int id = await EventAsync(host, ownerId: 10, scope: "Personal");

            Assert.False(await Service(host).CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Read, id));
        }

        [Fact]
        public async Task A_company_scope_event_is_readable_by_the_company_but_still_not_editable()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            await EmployeeAsync(host, 11);
            int id = await EventAsync(host, ownerId: 10, scope: "Company");

            var svc = Service(host);
            // "Company" scope is what the calendar screen has always meant by visible-to-everyone…
            Assert.True(await svc.CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Read, id));
            // …but visibility is not authorship. Anyone being able to move a company event would be worse
            // than nobody being able to see it.
            Assert.False(await svc.CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Edit, id));
        }

        // ---- the rule this service exists to get right ------------------------------------------
        [Fact]
        public async Task An_attendee_may_READ_the_meeting_but_may_NOT_EDIT_it()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);   // owner
            await EmployeeAsync(host, 11);   // invitee
            int id = await EventAsync(host, ownerId: 10, scope: "Personal");
            host.Db.CalendarEventAttendees.Add(new CalendarEventAttendee { EventId = id, EmployeeId = 11 });
            await host.Db.SaveChangesAsync();

            var svc = Service(host);

            // Reading is required: an invitee who is notified about a meeting they may not see would be
            // told something and then refused sight of it.
            Assert.True(await svc.CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Read, id));

            // Editing is NOT. Rescheduling would silently change the event for everyone else invited.
            Assert.False(await svc.CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Edit, id));
        }

        // ---- the hierarchy ----------------------------------------------------------------------
        [Fact]
        public async Task A_manager_may_read_and_edit_a_direct_reports_event()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);                       // the manager
            await EmployeeAsync(host, 11);                       // the report
            await ReportsToAsync(host, managerId: 10, reportId: 11);
            int id = await EventAsync(host, ownerId: 11, scope: "Personal");

            var svc = Service(host);
            Assert.True(await svc.CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Read, id));
            Assert.True(await svc.CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Edit, id));
        }

        [Fact]
        public async Task A_report_may_not_edit_their_managers_event()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            await EmployeeAsync(host, 11);
            await ReportsToAsync(host, managerId: 10, reportId: 11);
            int id = await EventAsync(host, ownerId: 10, scope: "Personal");

            // The hierarchy runs one way. Upwards is not access.
            Assert.False(await Service(host).CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Edit, id));
        }

        // ---- create and manage ------------------------------------------------------------------
        [Fact]
        public async Task Any_employee_may_create_a_calendar_entry()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 11);

            // A personal calendar nobody may write to is not a calendar.
            Assert.True(await Service(host).CanAsync(Ctx(11, CompanyOne), CalendarActions.Create));
        }

        [Fact]
        public async Task Manage_is_refused_to_an_employee_holding_no_administrator_role()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 11);
            int id = await EventAsync(host, ownerId: 11);

            // Even the OWNER does not get company-wide administration by owning one event.
            Assert.False(await Service(host).CanEventAsync(Ctx(11, CompanyOne), CalendarActions.Manage, id));
        }

        [Fact]
        public async Task A_soft_deleted_event_is_not_editable()
        {
            using var host = new PlatformTestHost(companyId: CompanyOne);
            await EmployeeAsync(host, 10);
            int id = await EventAsync(host, ownerId: 10);

            var ev = await host.Db.CalendarEvents.FirstAsync(e => e.Id == id);
            ev.DeletedAt = DateTime.UtcNow;
            await host.Db.SaveChangesAsync();

            Assert.False(await Service(host).CanEventAsync(Ctx(10, CompanyOne), CalendarActions.Edit, id));
        }
    }
}
