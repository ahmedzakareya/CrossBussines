using CrossBuy.BL;
using CrossBuy.BL.Approvals;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers.Api;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

namespace CrossBuy.Tests.Hr
{
    // ============================================================================================
    // LeaveApiController — the injected dependencies are actually ASSIGNED, proved by RUNNING the code.
    //
    // WHY THIS FILE EXISTS AND WHY IT IS NOT A SHAPE CHECK. An earlier candidate declared _hrAccess and
    // _businessContexts, took them as constructor parameters, and never assigned the fields. That
    // compiles. Worse, the authorization scanner CREDITS the endpoint, because the CanAsync call really is
    // in the method body — so a clean build and a clean scan both report a protected endpoint while every
    // request through it dies with a NullReferenceException on the first gate. An endpoint that throws is
    // not an endpoint that is protected; it is an outage that measures as a success.
    //
    // Reading the constructor text would not catch the next version of this either: the assignment can be
    // deleted from a line the assertion is not looking at. So these tests construct the real controller
    // and EXECUTE Create along the path that must dereference each dependency before it can return.
    // ============================================================================================
    public class LeaveApiControllerWiringTests
    {
        private const int CompanyA = 41;
        private const int Alice = 5;

        [Fact]
        public void Every_injected_dependency_reaches_a_field()
        {
            using var host = SeededHost();
            var contexts = new StubContexts(null);
            var workflow = new RecordingWorkflow();
            var controller = Build(host, contexts, workflow);

            // Object STATE after the real constructor ran, not the constructor's source. A parameter that
            // never reaches a field shows up here as null whichever line was deleted, and this covers all
            // six dependencies rather than only the two this batch added.
            var nulls = typeof(LeaveApiController)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(f => f.GetValue(controller) is null)
                .Select(f => f.Name)
                .ToList();

            Assert.Empty(nulls);
        }

        [Fact]
        public async Task The_business_context_accessor_is_assigned_and_an_unresolved_context_refuses()
        {
            using var host = SeededHost();
            var workflow = new RecordingWorkflow();
            var controller = Build(host, new StubContexts(null), workflow);

            // _businessContexts is dereferenced first in the gate. Unassigned, this line throws instead of
            // returning, so reaching the assertion at all is the proof.
            var result = await controller.Create(new LeaveApiController.CreateLeaveDto { LeaveTypeID = 1 });

            Assert.IsType<NotFoundObjectResult>(result);
            Assert.False(workflow.WasCalled);   // and the refusal is real - nothing was written
        }

        [Fact]
        public async Task The_hr_access_service_is_assigned_and_is_consulted_before_the_workflow_runs()
        {
            using var host = SeededHost();
            var workflow = new RecordingWorkflow();
            var controller = Build(host, new StubContexts(Ctx(CompanyA, Alice)), workflow);

            // A resolved self context passes the first gate, so execution reaches _hrAccess.CanAsync -
            // which is where the broken shape throws. Reaching the workflow proves the second field is
            // wired AND that the gate allowed a genuine self-service submission.
            var result = await controller.Create(new LeaveApiController.CreateLeaveDto { LeaveTypeID = 1 });

            Assert.True(workflow.WasCalled);

            // The subject came from the resolved context, never from the body: CreateLeaveDto carries no
            // employee id at all, and this pins that it is the context's employee that gets the request.
            Assert.Equal(Alice, workflow.SubjectEmployeeId);

            // The stub workflow declines, so a BadRequest here means the GATE passed and the workflow ran.
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task A_context_that_resolves_no_employee_refuses_before_the_workflow()
        {
            using var host = SeededHost();
            var workflow = new RecordingWorkflow();
            var controller = Build(host, new StubContexts(Ctx(CompanyA, null)), workflow);

            Assert.IsType<NotFoundObjectResult>(
                await controller.Create(new LeaveApiController.CreateLeaveDto { LeaveTypeID = 1 }));
            Assert.False(workflow.WasCalled);
        }

        // ---- fixture -------------------------------------------------------------------------------

        private static LeaveApiController Build(
            PlatformTestHost host, IBusinessContextAccessor contexts, ILeaveWorkflowService workflow)
        {
            var db = host.Db;
            var hr = new HrAccessService(
                db,
                new PlatformRoleDirectory(db, NullLogger<PlatformRoleDirectory>.Instance),
                new OrgHierarchy(db, NullLogger<OrgHierarchy>.Instance),
                new BootstrapAccessPolicyReader(db, NullLogger<BootstrapAccessPolicyReader>.Instance),
                NullLogger<HrAccessService>.Instance);

            var controller = new LeaveApiController(
                hr, contexts, new StubEmployees(), db, new StubDashboard(), workflow);

            // Create reads HttpContext.RequestAborted, so the action needs a real HttpContext to run -
            // which is the point: this executes rather than being inspected.
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        private static PlatformTestHost SeededHost()
        {
            var host = new PlatformTestHost();
            host.Db.Employee.Add(new Employee
            {
                ID = Alice, EmpCompanyID = CompanyA, IsActive = true,
                FirstName = "Alice", LastName = "A", FullName = "Alice A",
                Address = "-", PhoneNumber = "-", Email = "-", ProfileImage = "-",
                Gender = "-", MaritalStatus = "-", UserId = "u-alice",
            });
            host.Db.SaveChanges();
            return host;
        }

        private static BusinessContext Ctx(int companyId, int? employeeId) => new()
        {
            CompanyId = companyId,
            EmployeeId = employeeId,
            UserId = "u-alice",
            Source = BusinessContextSource.Http,
        };

        private sealed class StubContexts : IBusinessContextAccessor
        {
            private readonly BusinessContext? _context;
            public StubContexts(BusinessContext? context) { _context = context; }

            public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(_context);

            public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
                => _context is null
                    ? throw new BusinessContextUnresolvedException("no context in this fixture")
                    : Task.FromResult(_context);
        }

        // Records whether the controller got past its gates, and with whose id.
        private sealed class RecordingWorkflow : ILeaveWorkflowService
        {
            public bool WasCalled { get; private set; }
            public int SubjectEmployeeId { get; private set; }

            public Task<(bool ok, string? error, LeaveRequest? req)> CreateAsync(
                int employeeId, int leaveTypeId, DateTime start, DateTime end, string? reason)
            {
                WasCalled = true;
                SubjectEmployeeId = employeeId;
                return Task.FromResult<(bool, string?, LeaveRequest?)>(
                    (false, "the stub declines, so the assertion is about the GATE and not the workflow", null));
            }

            public Task<List<int>> ManagerChainAsync(int employeeId) => Task.FromResult(new List<int>());
            public Task<ApproverChain> ApproverChainAsync(int employeeId) => throw new NotSupportedException();
            public Task<IReadOnlyList<PendingApprovalRow>> PendingForApproverAsync(
                BusinessContext context, int approverEmployeeId, CancellationToken cancellationToken = default)
                => Task.FromResult<IReadOnlyList<PendingApprovalRow>>(Array.Empty<PendingApprovalRow>());
            public Task<(bool ok, string? error)> DecideAsync(
                int requestId, int approverEmployeeId, bool approve, string? note)
                => throw new NotSupportedException();
        }

        private sealed class StubEmployees : IEmployeeService
        {
            // Create looks the caller up by user id before the authorization gate. It must return SOMETHING
            // for execution to reach the gate at all - the identity that actually decides is the
            // BusinessContext's, which is the whole point of the change under test.
            public Task<CrossBuy.ViewModel.EmployeeViewModel> GetByIdAsync(int id)
                => Task.FromResult(new CrossBuy.ViewModel.EmployeeViewModel { ID = id });
            public Task<CrossBuy.ViewModel.EmployeeViewModel> GetEmployeeByUserIdAsync(string userId)
                => Task.FromResult(new CrossBuy.ViewModel.EmployeeViewModel { ID = Alice });
            public Task<List<CrossBuy.ViewModel.EmployeeListItemDto>> GetAllAsync()
                => Task.FromResult(new List<CrossBuy.ViewModel.EmployeeListItemDto>());
            public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeAsync(
                CrossBuy.ViewModel.EmployeeViewModel model, IFormFile profileImage, string webRootPath)
                => throw new NotSupportedException();
            public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeImageAsync(
                int employeeId, IFormFile profileImage, string webRootPath)
                => throw new NotSupportedException();
        }

        private sealed class StubDashboard : ILeaveDashboardService
        {
            public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotSupportedException();
            public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(0);
            public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year) => Task.FromResult(0);
            public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);
            public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end) => Task.FromResult(0);
        }
    }
}
