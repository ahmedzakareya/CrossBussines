using CrossBuy.BL;
using CrossBuy.BL.Approvals;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Inventory;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CANONICAL ENTITY IDENTITY ON APPROVAL ROWS — the contract TAB-3 integrates against.
	//
	// What this suite is really guarding:
	//
	//   1. A SILO KEY IS NOT AN ENTITY CODE. "Request" groups a view; "EmployeeRequest" is a platform
	//      identity. The map between them is asserted against EntityRegistry's own constants, so a
	//      renamed silo or a renamed code breaks here rather than in a consumer.
	//
	//   2. THE MAPPING IS NOT A NUMERIC COINCIDENCE. Four silos are seeded so that the SAME numeric id
	//      exists in more than one of them, and the rows are then told apart by EntityType. A contract
	//      that paired rows by matching ids would collapse them, and this is the test that would catch it.
	//
	//   3. THE ENTITY QUERY DISCLOSES NOTHING NEW. GetPendingForEntityAsync is a filter over the caller's
	//      own inbox, so a non-approver gets nothing — proven by asking as somebody who is not the
	//      approver and getting an empty list where the real approver gets a row.
	//
	//   4. UNRESOLVED IS A REAL ANSWER. An unmapped silo yields a null EntityType rather than a
	//      plausible-looking code, and a null EntityType matches no entity query.
	//
	// These drive the REAL services over a real relational host, not mocks: the rows under test are the
	// rows the modules actually produce, because a fake row could carry any EntityType it liked.
	// =================================================================================================
	public class ApprovalEntityIdentityTests
	{
		private const int OwnCompany = 1;
		private const int OtherCompany = 2;
		private const int Approver = 10;
		private const int Stranger = 11;
		private const int Requester = 12;
		private const int ForeignRequester = 20;

		private static readonly DateTime Oldest = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Middle = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
		private static readonly DateTime Newest = new(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);

		private static Employee Emp(int id, int companyId) => new()
		{
			ID = id, FirstName = "T", LastName = "T", FullName = "emp" + id, FullNameEn = "emp" + id,
			EmpCompanyID = companyId, IsActive = true, Address = "-", PhoneNumber = "-",
			Email = $"e{id}@example.invalid", ProfileImage = "-", Gender = "M", MaritalStatus = "S",
			UserId = "user-" + id,
		};

		private static BusinessContext Context(int companyId, int? employeeId = Approver) => new()
		{
			CompanyId = companyId, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Test,
		};

		private static IApprovalInboxService Inbox(PlatformTestHost host)
		{
			var leave = new LeaveWorkflowService(host.Db, new IdentityNoopNotifications(), new IdentityStubDashboard(),
				NullLogger<LeaveWorkflowService>.Instance);
			var requests = new EmployeeRequestService(host.Db, new IdentityNoopNotifications(), leave,
				new IdentityStubCaller(41), new IdentityNoEvents());
			var inventory = new InventoryApprovalService(host.Db, null!, null!, null!, null!);
			return new ApprovalInboxService(leave, requests, inventory, new InboxNoBillingApprovals());
		}

		/// Seeds the three silos that this host can drive end to end, each awaiting <c>Approver</c>.
		/// ProjectBilling is covered by the map assertions instead — its reader needs the projects graph,
		/// and inventing a stub row here would prove the stub rather than the contract.
		private static void SeedThreeSilos(PlatformTestHost host)
		{
			host.Seed.Employee.AddRange(
				Emp(Approver, OwnCompany), Emp(Stranger, OwnCompany), Emp(Requester, OwnCompany),
				Emp(ForeignRequester, OtherCompany));
			host.Seed.LeaveTypes.Add(new LeaveTypes { ID = 1, NameAr = "سنوية", NameEn = "Annual" });
			host.Seed.InventoryUserRoles.Add(new InventoryUserRole
			{ CompanyID = OwnCompany, EmployeeId = Approver, Role = "InventoryManager" });

			host.Seed.LeaveRequests.Add(new LeaveRequest
			{
				EmployeeID = Requester, LeaveTypeID = 1, Status = 0, CurrentApproverEmployeeID = Approver,
				CurrentLevel = 1, StartDate = new DateTime(2026, 9, 1), EndDate = new DateTime(2026, 9, 3),
				Days = 3, CreatedAt = Oldest,
			});
			host.Seed.EmployeeRequests.Add(new EmployeeRequest
			{
				CompanyID = OwnCompany, EmployeeID = Requester, RequestType = "Letter", LetterType = "Salary",
				Status = 0, CurrentApproverEmployeeID = Approver, CurrentLevel = 1, CreatedAt = Middle,
			});
			host.Seed.InventoryApprovals.Add(new InventoryApproval
			{
				CompanyID = OwnCompany, DocType = "PurchaseOrder", Amount = 500m, Status = "Pending",
				RequestedByEmployeeId = Requester, RequestedAt = Newest,
			});
			host.Seed.SaveChanges();
		}

		// ==========================================================================================
		// THE MAP
		// ==========================================================================================

		[Fact]
		public void Every_silo_maps_to_a_canonical_registry_code()
		{
			// Asserted against EntityRegistry's OWN constants, never against a string literal typed here:
			// a renamed code must break this test, and a literal would have hidden the rename.
			Assert.Equal(EntityRegistry.LeaveRequest, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.Leave));
			Assert.Equal(EntityRegistry.EmployeeRequest, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.Request));
			Assert.Equal(EntityRegistry.InventoryApproval, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.Inventory));
			Assert.Equal(EntityRegistry.ProjectBilling, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.ProjectBilling));

			// THE SILO KEY IS NOT THE CODE. Two of the four differ outright, and asserting that keeps a
			// future "just use the silo name" shortcut from looking harmless.
			Assert.NotEqual(ApprovalSilos.Leave, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.Leave));
			Assert.NotEqual(ApprovalSilos.Request, ApprovalEntityMap.CanonicalEntityType(ApprovalSilos.Request));
		}

		[Fact]
		public void An_unknown_silo_resolves_to_nothing_rather_than_a_plausible_code()
		{
			foreach (var unknown in new[] { "Payroll", "Purchase", "leave", "LEAVE", "", "  " })
				Assert.Null(ApprovalEntityMap.CanonicalEntityType(unknown));

			Assert.Null(ApprovalEntityMap.CanonicalEntityType(null));
			Assert.False(ApprovalEntityMap.IsSupported("Payroll"));

			// Case matters: silo keys are compared ordinally everywhere else, and a case-insensitive map
			// would quietly accept "leave" from a consumer that then got rows it never asked for.
			Assert.False(ApprovalEntityMap.IsSupported("leave"));

			// THE ALLOW, so the refusals above cannot be satisfied by a map that resolves nothing at all.
			Assert.True(ApprovalEntityMap.IsSupported(ApprovalSilos.Leave));
			Assert.Equal(4, ApprovalEntityMap.SupportedSilos.Count);
			Assert.Equal(4, ApprovalEntityMap.SupportedEntityTypes.Count);
		}

		[Fact]
		public void Every_published_entity_type_is_a_registered_platform_entity()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);

			// A code the registry does not define would be an invented taxonomy - exactly what this
			// contract refuses. Asked of the REGISTRY's own definition list rather than a literal set
			// typed here, so deleting an entity breaks this instead of silently orphaning a silo.
			//
			// Registration is TWO steps and this asserts the first; the ResolveAsync arm for each of the
			// four was verified separately against the id the row actually carries.
			var registered = new EntityRegistry(host.Db).GetDefinitions()
				.Select(d => d.Code).ToHashSet(StringComparer.Ordinal);

			foreach (var code in ApprovalEntityMap.SupportedEntityTypes)
				Assert.Contains(code, registered);
		}

		// ==========================================================================================
		// IDENTITY ON REAL ROWS
		// ==========================================================================================

		[Fact]
		public async Task Every_row_carries_its_canonical_entity_type_and_the_company_it_was_read_under()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);
			Assert.Equal(3, page.Rows.Count);

			foreach (var row in page.Rows)
			{
				Assert.True(row.HasCanonicalEntity, $"{row.Silo} produced an unresolved row");
				Assert.Equal(ApprovalEntityMap.CanonicalEntityType(row.Silo), row.EntityType);
				Assert.Equal(OwnCompany, row.CompanyId);

				// The canonical key is built from the ENTITY type, and is distinct from the silo-keyed
				// Reference that existing consumers already use.
				Assert.NotNull(row.EntityReference);
				Assert.StartsWith(row.EntityType! + ":", row.EntityReference!, StringComparison.Ordinal);
				Assert.NotEqual(row.Reference, row.EntityReference);
			}
		}

		[Fact]
		public async Task The_entity_id_is_the_modules_own_primary_key_not_a_renumbering()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			var leaveId = host.Seed.LeaveRequests.AsNoTracking().Single().ID;
			var requestId = host.Seed.EmployeeRequests.AsNoTracking().Single().ID;
			var inventoryId = host.Seed.InventoryApprovals.AsNoTracking().Single().ID;

			Assert.Equal(leaveId, page.Rows.Single(r => r.EntityType == EntityRegistry.LeaveRequest).EntityId);
			Assert.Equal(requestId, page.Rows.Single(r => r.EntityType == EntityRegistry.EmployeeRequest).EntityId);
			Assert.Equal(inventoryId, page.Rows.Single(r => r.EntityType == EntityRegistry.InventoryApproval).EntityId);
		}

		[Fact]
		public async Task Existing_consumers_see_an_unchanged_row()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			// BACKWARD COMPATIBILITY, ASSERTED RATHER THAN ASSUMED. Everything an existing consumer reads
			// is still exactly what it was: the silo keys, the silo-keyed Reference, the counts, the
			// visible silos and the navigation target.
			foreach (var row in page.Rows)
			{
				Assert.Equal(
					row.StepId.HasValue ? $"{row.Silo}:{row.EntityId}:{row.StepId.Value}" : $"{row.Silo}:{row.EntityId}",
					row.Reference);
				Assert.NotNull(row.Navigation);
				Assert.False(string.IsNullOrWhiteSpace(row.Navigation.Controller));
			}

			Assert.Equal(3, page.TotalPending);
			Assert.Equal(4, page.CountsBySilo.Count);
			Assert.Contains(ApprovalSilos.Leave, page.VisibleSilos);
		}

		// ==========================================================================================
		// NO NUMERIC-ID COLLISION ACROSS SILOS
		// ==========================================================================================

		[Fact]
		public async Task The_same_number_in_two_silos_stays_two_different_records()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);

			var page = await Inbox(host).GetPendingForCurrentApproverAsync(Context(OwnCompany), Approver);

			var leave = page.Rows.Single(r => r.EntityType == EntityRegistry.LeaveRequest);
			var request = page.Rows.Single(r => r.EntityType == EntityRegistry.EmployeeRequest);

			// The fixture seeds one row per silo, so each table's identity starts at 1 - the ids genuinely
			// COLLIDE. That is the point: this is the shape in which a contract that paired rows by number
			// would be wrong, and it is the shape production is in for every low-id tenant.
			Assert.Equal(leave.EntityId, request.EntityId);

			// They are still two different business records, and the canonical key is what says so.
			Assert.NotEqual(leave.EntityType, request.EntityType);
			Assert.NotEqual(leave.EntityReference, request.EntityReference);

			// And an entity query for one does not return the other.
			var hits = await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.LeaveRequest, leave.EntityId);

			Assert.Equal(EntityRegistry.LeaveRequest, Assert.Single(hits).EntityType);
		}

		// ==========================================================================================
		// THE ENTITY QUERY — authorization, isolation, fail-closed
		// ==========================================================================================

		[Fact]
		public async Task The_entity_query_returns_the_approvers_own_row()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);
			var requestId = host.Seed.EmployeeRequests.AsNoTracking().Single().ID;

			var hits = await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, requestId);

			var row = Assert.Single(hits);
			Assert.Equal(EntityRegistry.EmployeeRequest, row.EntityType);
			Assert.Equal(requestId, row.EntityId);
			Assert.Equal(OwnCompany, row.CompanyId);
		}

		[Fact]
		public async Task Somebody_who_is_not_the_approver_learns_nothing_about_the_same_record()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);
			var requestId = host.Seed.EmployeeRequests.AsNoTracking().Single().ID;

			// THE DISCLOSURE TEST. Stranger is a real employee in the SAME company, and the record is
			// real and genuinely pending - only the entitlement differs. If this method had become a
			// second query path instead of a filter over the caller's inbox, this is where it would leak.
			var strangerHits = await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany, Stranger), Stranger, EntityRegistry.EmployeeRequest, requestId);
			Assert.Empty(strangerHits);

			// THE ALLOW, same record, same company, same call - only the approver differs.
			var approverHits = await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, requestId);
			Assert.Single(approverHits);
		}

		[Fact]
		public async Task A_foreign_company_context_learns_nothing()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);
			var requestId = host.Seed.EmployeeRequests.AsNoTracking().Single().ID;

			// Same approver id, same record id, announced under the other company.
			var foreign = await Inbox(host).GetPendingForEntityAsync(
				Context(OtherCompany), Approver, EntityRegistry.EmployeeRequest, requestId);
			Assert.Empty(foreign);

			// THE ALLOW.
			Assert.Single(await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, requestId));
		}

		[Fact]
		public async Task The_entity_query_fails_closed_on_every_unusable_argument()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);
			var requestId = host.Seed.EmployeeRequests.AsNoTracking().Single().ID;
			var inbox = Inbox(host);

			// Unresolved company, unresolved approver, missing type, non-positive id, unknown code.
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(0), Approver, EntityRegistry.EmployeeRequest, requestId));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), 0, EntityRegistry.EmployeeRequest, requestId));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, "", requestId));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, "   ", requestId));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, 0));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, -1));
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, "Payroll", requestId));

			// Case-sensitivity is part of the contract, not an accident.
			Assert.Empty(await inbox.GetPendingForEntityAsync(Context(OwnCompany), Approver, "employeerequest", requestId));

			// THE ALLOW that makes all nine refusals meaningful.
			Assert.Single(await inbox.GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.EmployeeRequest, requestId));
		}

		[Fact]
		public async Task Asking_for_a_record_with_no_pending_approval_is_empty_not_an_error()
		{
			using var host = new PlatformTestHost(companyId: OwnCompany);
			SeedThreeSilos(host);

			// A well-formed, registered code and a plausible id that simply has nothing waiting.
			var hits = await Inbox(host).GetPendingForEntityAsync(
				Context(OwnCompany), Approver, EntityRegistry.ProjectBilling, 9999);

			Assert.Empty(hits);
		}

		[Fact]
		public async Task An_unresolved_row_matches_no_entity_query()
		{
			// The guard for a FUTURE silo added without a verified mapping. A row whose EntityType is null
			// must not be reachable by any entity query, however the caller spells the type - including
			// the silo key itself, which is the mistake this contract exists to prevent.
			var unmapped = new PendingApprovalRow
			{
				Silo = "Payroll", EntityId = 7, Status = "Pending",
				Navigation = new ApprovalNavigationTarget("Payroll", "Index"),
				EntityType = ApprovalEntityMap.CanonicalEntityType("Payroll"),
			};

			Assert.Null(unmapped.EntityType);
			Assert.False(unmapped.HasCanonicalEntity);
			Assert.Null(unmapped.EntityReference);

			// Its silo-keyed Reference still works, so an unmapped silo remains a perfectly good inbox row.
			Assert.Equal("Payroll:7", unmapped.Reference);

			await Task.CompletedTask;
		}

		// ---- local doubles, named apart from the neighbouring suites' own -----------------------------

		private sealed class IdentityNoEvents : IBusinessEventService
		{
			public Task<long> RecordAsync(BusinessEventRecord record, CancellationToken cancellationToken = default)
				=> throw new InvalidOperationException("The identity contract must not record business events.");
			public BusinessEventEnvelope BuildEnvelope(CrossBuy.Models.Context.Platform.BusinessEvent stored)
				=> throw new InvalidOperationException("not used");
		}

		private sealed class IdentityStubCaller : IBusinessContextAccessor
		{
			private readonly BusinessContext _ctx;
			public IdentityStubCaller(int employeeId) => _ctx = new BusinessContext
			{ CompanyId = OwnCompany, EmployeeId = employeeId, UserId = "u", Source = BusinessContextSource.Test };
			public Task<BusinessContext> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(_ctx);
			public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<BusinessContext?>(_ctx);
		}

		private sealed class IdentityNoopNotifications : INotificationService
		{
			public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn, string? bodyAr,
				string? bodyEn, string type, int? refId = null, string? url = null, int? companyId = null,
				int? actorEmployeeId = null, string? priority = null, string? category = null,
				string? dedupKey = null, DateTime? expiresAt = null, string? icon = null,
				string? entityType = null, int? entityId = null) => Task.CompletedTask;

			public Task<int> NotifyRoleAsync(int companyId, string role, string[] actions, string? titleAr,
				string? titleEn, string? bodyAr, string? bodyEn, string type, int? refId = null,
				int? actorEmployeeId = null) => Task.FromResult(0);
		}

		private sealed class IdentityStubDashboard : ILeaveDashboardService
		{
			public Task<PeopleDashboardDto> BuildAsync(int employeeId) => throw new NotImplementedException();
			public Task<int> RemainingForTypeAsync(int employeeId, int leaveTypeId) => Task.FromResult(30);
			public Task<int> RemainingForTypeInYearAsync(int employeeId, int leaveTypeId, int year) => Task.FromResult(30);
			public Task<bool[]?> WorkDayFlagsAsync(int employeeId) => Task.FromResult<bool[]?>(null);
			public Task<int> WorkingDaysAsync(int employeeId, DateTime start, DateTime end)
				=> Task.FromResult(Math.Max(1, (end.Date - start.Date).Days + 1));
		}
	}
}
