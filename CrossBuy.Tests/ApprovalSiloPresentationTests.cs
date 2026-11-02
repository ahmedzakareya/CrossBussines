using System.Globalization;
using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Approvals;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// HOW /Approvals PRESENTS EACH SILO.
	//
	// THE DEFECT THIS SUITE EXISTS FOR. ApprovalInboxService emits four silos; the screen mapped three
	// and its `_ =>` default arm WAS the Inventory arm. A pending مستخلص therefore reached the approver
	// labelled "Inventory", with the bilingual title the reader had already composed thrown away in
	// favour of the raw ApprovalType discriminator ("ProgressBilling.Approve"), and with the tile strip
	// summing three silos while the header badge counted all four — so the screen contradicted itself.
	//
	// WHY THESE ARE PRESENTATION TESTS AND NOT INTEGRATION TESTS. The rows are crafted here rather than
	// produced by the real module readers, because what is under test is the MAPPING from a row to what
	// the approver sees. Company boundaries, entitlement and ordering are proven against the real
	// readers in ApprovalCompanyIsolationTests and ApprovalEntityIdentityTests; re-proving them through
	// a fourth seeded fixture would test those services again and this mapping not at all.
	//
	// THE REGISTRY IS REAL. Silo chrome is read from the registered EntityDefinition, so these assert
	// the ACTUAL registered Arabic and English names. A stubbed registry would let the screen and the
	// platform vocabulary drift apart silently, which is the drift the dependency exists to prevent.
	// =================================================================================================
	public class ApprovalSiloPresentationTests
	{
		private const int Company = 1;
		private const int Approver = 10;
		private const int ProjectId = 77;
		private const int BillingId = 12;

		// ==========================================================================================
		// THE REGRESSION
		// ==========================================================================================

		[Fact]
		public async Task A_project_billing_row_is_not_presented_as_an_inventory_document()
		{
			var row = await SingleRowAsync(BillingRow());

			Assert.Equal(ApprovalSilos.ProjectBilling, row.Silo);
			Assert.NotEqual("Inventory", row.SiloLabel);
			// The registered English name of the thing being approved - asserted against the registry
			// rather than a literal typed twice.
			Assert.Equal("Progress billing", row.SiloLabel);
		}

		[Fact]
		public async Task A_project_billing_row_keeps_the_bilingual_title_the_reader_composed()
		{
			var row = await SingleRowAsync(BillingRow());

			// NOT "ProgressBilling.Approve" - that is the stage discriminator, not a title.
			Assert.Equal("Approve progress billing #12 - Harbour works", row.Title);
			Assert.DoesNotContain("ProgressBilling.", row.Title, StringComparison.Ordinal);
		}

		[Fact]
		public async Task The_arabic_title_and_the_arabic_silo_name_are_used_under_an_arabic_culture()
		{
			var row = await SingleRowAsync(BillingRow(), culture: "ar");

			Assert.Equal("اعتماد مستخلص #12 - أعمال الميناء", row.Title);
			Assert.Equal("مستخلص", row.SiloLabel);
		}

		// ==========================================================================================
		// THE LINK — the defect a route-value-dropping stub could never have caught
		// ==========================================================================================

		[Fact]
		public async Task A_project_billing_link_carries_both_the_project_and_the_billing_id()
		{
			var row = await SingleRowAsync(BillingRow());

			// /Project/Billing is addressed by project AND billing. Without both, the project lookup
			// fails and the approver lands on the Projects LIST instead of the document.
			Assert.Contains("/Project/Billing", row.Url, StringComparison.Ordinal);
			Assert.Contains("id=" + ProjectId, row.Url, StringComparison.Ordinal);
			Assert.Contains("b=" + BillingId, row.Url, StringComparison.Ordinal);
		}

		[Fact]
		public async Task A_silo_that_supplies_no_route_values_still_links_to_its_bare_action()
		{
			var row = await SingleRowAsync(LeaveRow());

			Assert.Equal("/People/Leaves", row.Url);
		}

		// ==========================================================================================
		// THE STRUCTURAL HALF — an unmapped silo must not borrow another silo's identity
		// ==========================================================================================

		[Fact]
		public async Task An_unknown_silo_is_not_presented_as_an_inventory_document()
		{
			// A silo the screen has never met. Before the fix this fell into the default arm and was
			// rendered as Inventory; the point of the fix is that the NEXT silo cannot repeat the bug.
			var row = await SingleRowAsync(UnknownRow("Procurement"));

			Assert.NotEqual("Inventory", row.SiloLabel);
			Assert.Equal("Procurement", row.SiloLabel);
			Assert.NotEqual("ki-check-square", row.Icon);
		}

		[Fact]
		public async Task An_unknown_silo_falls_back_to_its_approval_type_rather_than_an_empty_title()
		{
			var row = await SingleRowAsync(UnknownRow("Procurement") with { TitleAr = null, TitleEn = null });

			Assert.Equal("Procurement.Approve", row.Title);
		}

		// ==========================================================================================
		// NO REGRESSION ON THE THREE ORIGINAL SILOS
		// ==========================================================================================

		[Theory]
		[InlineData(ApprovalSilos.Leave, "Leave", "ki-calendar-tick")]
		[InlineData(ApprovalSilos.Inventory, "Inventory", "ki-check-square")]
		public async Task The_original_silos_keep_the_label_and_icon_they_have_always_had(
			string silo, string expectedLabel, string expectedIcon)
		{
			var source = silo == ApprovalSilos.Leave ? LeaveRow() : InventoryRow();

			var row = await SingleRowAsync(source);

			Assert.Equal(expectedLabel, row.SiloLabel);
			Assert.Equal(expectedIcon, row.Icon);
		}

		[Fact]
		public async Task The_icon_is_not_prefixed_twice()
		{
			// The registry stores "ki-outline ki-bill"; the view renders class="ki-outline @r.Icon".
			// Carrying the prefix through would emit "ki-outline ki-outline ki-bill" and render nothing.
			var row = await SingleRowAsync(BillingRow());

			Assert.DoesNotContain("ki-outline", row.Icon, StringComparison.Ordinal);
			Assert.Equal("ki-bill", row.Icon);
		}

		// ==========================================================================================
		// THE TILE STRIP — the counts the screen contradicted itself with
		// ==========================================================================================

		[Fact]
		public async Task The_billing_tile_count_is_published_and_the_tiles_sum_to_the_row_total()
		{
			// Deliberately ASYMMETRIC: 2 billing, 1 inventory, 1 leave. A symmetric fixture passes with
			// the silo keys transposed and so proves nothing about which rows were counted where.
			var page = Page(
				new[] { BillingRow(), BillingRow() with { EntityId = 13 }, InventoryRow(), LeaveRow() },
				counts: new Dictionary<string, int>
				{
					[ApprovalSilos.Leave] = 1,
					[ApprovalSilos.Request] = 0,
					[ApprovalSilos.Inventory] = 1,
					[ApprovalSilos.ProjectBilling] = 2,
				},
				visible: new[] { ApprovalSilos.Leave, ApprovalSilos.Request, ApprovalSilos.Inventory, ApprovalSilos.ProjectBilling });

			var (rows, bag) = await RenderAsync(page);

			Assert.Equal(2, (int)bag["ProjectBillingCount"]!);
			Assert.True((bool)bag["IsBillingApprover"]!);

			var tileSum = (int)bag["LeaveCount"]! + (int)bag["RequestCount"]!
				+ (int)bag["InventoryCount"]! + (int)bag["ProjectBillingCount"]!;
			Assert.Equal(rows.Count, tileSum);
		}

		[Fact]
		public async Task A_caller_who_cannot_approve_a_billing_is_not_offered_a_billing_tile()
		{
			// Billing entitlement is per project, so the silo is visible exactly when the module
			// returned something actionable. A permanent zero tile would tell this user nothing.
			var page = Page(
				new[] { LeaveRow() },
				counts: new Dictionary<string, int>
				{
					[ApprovalSilos.Leave] = 1,
					[ApprovalSilos.Request] = 0,
					[ApprovalSilos.Inventory] = 0,
					[ApprovalSilos.ProjectBilling] = 0,
				},
				visible: new[] { ApprovalSilos.Leave, ApprovalSilos.Request });

			var (_, bag) = await RenderAsync(page);

			Assert.Equal(0, (int)bag["ProjectBillingCount"]!);
			Assert.False((bool)bag["IsBillingApprover"]!);
		}

		[Fact]
		public async Task The_billing_tile_label_comes_from_the_registry_in_both_languages()
		{
			var (_, en) = await RenderAsync(Page(new[] { BillingRow() }), culture: "en");
			var (_, ar) = await RenderAsync(Page(new[] { BillingRow() }), culture: "ar");

			Assert.Equal("Progress billing", (string)en["ProjectBillingLabel"]!);
			Assert.Equal("مستخلص", (string)ar["ProjectBillingLabel"]!);
		}

		// ==========================================================================================
		// READ-ONLY — the appointment's standing constraint
		// ==========================================================================================

		[Fact]
		public void The_approvals_screen_exposes_no_mutating_endpoint()
		{
			// Approval WRITES stay with the module services. A POST appearing here would be the
			// boundary breaking, and it should break this test first.
			var posts = typeof(ApprovalsController).GetMethods()
				.Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Length > 0)
				.ToList();

			Assert.Empty(posts);
		}

		// ==========================================================================================
		// harness
		// ==========================================================================================

		private static PendingApprovalRow BillingRow() => new()
		{
			Silo = ApprovalSilos.ProjectBilling,
			EntityId = BillingId,
			Status = "Submitted",
			ApprovalType = "ProgressBilling.Approve",
			TitleAr = "اعتماد مستخلص #12 - أعمال الميناء",
			TitleEn = "Approve progress billing #12 - Harbour works",
			RequesterEmployeeId = 41,
			RequesterNameEn = "Prepared By",
			SubmittedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
			Amount = 125000m,
			Navigation = new ApprovalNavigationTarget("Project", "Billing",
				new Dictionary<string, string?> { ["id"] = ProjectId.ToString(), ["b"] = BillingId.ToString() }),
		};

		private static PendingApprovalRow LeaveRow() => new()
		{
			Silo = ApprovalSilos.Leave,
			EntityId = 5,
			Status = "Pending",
			TitleEn = "Annual leave",
			TitleAr = "إجازة سنوية",
			RequesterEmployeeId = 42,
			RequesterNameEn = "Asker",
			SubmittedAt = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
			PeriodStart = new DateTime(2026, 11, 1),
			PeriodEnd = new DateTime(2026, 11, 3),
			Days = 3,
			Navigation = new ApprovalNavigationTarget("People", "Leaves"),
		};

		private static PendingApprovalRow InventoryRow() => new()
		{
			Silo = ApprovalSilos.Inventory,
			EntityId = 9,
			Status = "Pending",
			ApprovalType = "PurchaseOrder",
			RequesterEmployeeId = 43,
			RequesterNameEn = "Buyer",
			SubmittedAt = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc),
			Amount = 400m,
			Navigation = new ApprovalNavigationTarget("Inventory", "Approvals"),
		};

		private static PendingApprovalRow UnknownRow(string silo) => new()
		{
			Silo = silo,
			EntityId = 3,
			Status = "Pending",
			ApprovalType = silo + ".Approve",
			TitleEn = silo + " approval",
			RequesterEmployeeId = 44,
			RequesterNameEn = "Somebody",
			SubmittedAt = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc),
			Navigation = new ApprovalNavigationTarget("Procurement", "Approvals"),
		};

		private static ApprovalInboxPage Page(
			IReadOnlyList<PendingApprovalRow> rows,
			IReadOnlyDictionary<string, int>? counts = null,
			IEnumerable<string>? visible = null) => new()
			{
				Rows = rows,
				TotalPending = rows.Count,
				CountsBySilo = counts ?? rows.GroupBy(r => r.Silo)
					.ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
				VisibleSilos = new HashSet<string>(
					visible ?? rows.Select(r => r.Silo), StringComparer.Ordinal),
			};

		private static async Task<CrossBuy.BL.ApprovalInboxRow> SingleRowAsync(
			PendingApprovalRow source, string culture = "en")
		{
			var (rows, _) = await RenderAsync(Page(new[] { source }), culture);
			return Assert.Single(rows);
		}

		/// Runs the real action and hands back what the view would bind to.
		private static async Task<(List<CrossBuy.BL.ApprovalInboxRow> Rows, IDictionary<string, object?> ViewBag)>
			RenderAsync(ApprovalInboxPage page, string culture = "en")
		{
			var previous = CultureInfo.CurrentUICulture;
			CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
			try
			{
				using var host = new PlatformTestHost(companyId: Company);
				var context = new BusinessContext { CompanyId = Company, EmployeeId = Approver, UserId = "u" + Approver };

				var controller = new ApprovalsController(
					new StubEmployees(Approver), host.Db, new StubLocalizer(),
					new StubInbox(page), new StubContextAccessor(context),
					new EntityRegistry(host.Db));

				var http = new DefaultHttpContext
				{
					User = new ClaimsPrincipal(new ClaimsIdentity(
						new[] { new Claim(ClaimTypes.NameIdentifier, "u" + Approver) }, "test")),
				};
				controller.ControllerContext = new ControllerContext { HttpContext = http };
				controller.Url = new RouteCarryingUrlHelper(controller.ControllerContext);
				controller.TempData = new TempDataDictionary(http, new StubTempDataProvider());

				var result = Assert.IsType<ViewResult>(await controller.Index());
				var rows = Assert.IsType<List<CrossBuy.BL.ApprovalInboxRow>>(result.Model);
				return (rows, controller.ViewData);
			}
			finally { CultureInfo.CurrentUICulture = previous; }
		}

		private sealed class StubInbox : IApprovalInboxService
		{
			private readonly ApprovalInboxPage _page;
			public StubInbox(ApprovalInboxPage page) => _page = page;

			public Task<ApprovalInboxPage> GetPendingForCurrentApproverAsync(
				BusinessContext context, int employeeId, int take = 0,
				CancellationToken cancellationToken = default) => Task.FromResult(_page);

			// Not the path under test. Throwing beats returning empty: if this screen ever starts
			// asking the entity-scoped question, the suite should say so rather than quietly agree.
			public Task<IReadOnlyList<PendingApprovalRow>> GetPendingForEntityAsync(
				BusinessContext context, int employeeId, string entityType, int entityId,
				CancellationToken cancellationToken = default)
				=> throw new InvalidOperationException("the approvals screen does not ask the entity question");
		}

		private sealed class StubEmployees : IEmployeeService
		{
			private readonly int _employeeId;
			public StubEmployees(int employeeId) => _employeeId = employeeId;

			public Task<CrossBuy.ViewModel.EmployeeViewModel> GetEmployeeByUserIdAsync(string userId)
				=> Task.FromResult(new CrossBuy.ViewModel.EmployeeViewModel { ID = _employeeId });

			public Task<CrossBuy.ViewModel.EmployeeViewModel> GetByIdAsync(int id) => throw new NotSupportedException();
			public Task<List<CrossBuy.ViewModel.EmployeeListItemDto>> GetAllAsync() => throw new NotSupportedException();
			public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeAsync(
				CrossBuy.ViewModel.EmployeeViewModel model, IFormFile profileImage, string webRootPath)
				=> throw new NotSupportedException();
			public Task<CrossBuy.ViewModel.EmployeeViewModel> SaveEmployeeImageAsync(
				int employeeId, IFormFile profileImage, string webRootPath) => throw new NotSupportedException();
		}

		private sealed class StubLocalizer : IStringLocalizer<CrossBuy.SharedResources>
		{
			public LocalizedString this[string name] => new(name, name, resourceNotFound: false);
			public LocalizedString this[string name, params object[] arguments]
				=> new(name, string.Format(CultureInfo.InvariantCulture, name, arguments), resourceNotFound: false);
			public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
				=> Array.Empty<LocalizedString>();
		}

		/// Unlike the older stub this one KEEPS the route values, because dropping them is precisely
		/// the defect under test and a stub that discards an argument cannot fail when the code does.
		private sealed class RouteCarryingUrlHelper : IUrlHelper
		{
			public RouteCarryingUrlHelper(ActionContext actionContext) => ActionContext = actionContext;
			public ActionContext ActionContext { get; }

			public string? Action(UrlActionContext actionContext)
			{
				var path = "/" + actionContext.Controller + "/" + actionContext.Action;
				var values = actionContext.Values == null
					? null
					: new Microsoft.AspNetCore.Routing.RouteValueDictionary(actionContext.Values);
				if (values == null || values.Count == 0) { return path; }
				var query = string.Join("&", values
					.Where(kv => kv.Value != null && kv.Value.ToString()!.Length > 0)
					.Select(kv => kv.Key + "=" + kv.Value));
				return query.Length == 0 ? path : path + "?" + query;
			}

			public string? Content(string? contentPath) => contentPath;
			public bool IsLocalUrl(string? url) => true;
			public string? Link(string? routeName, object? values) => "/";
			public string? RouteUrl(UrlRouteContext routeContext) => "/";
		}

		private sealed class StubTempDataProvider : ITempDataProvider
		{
			public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
			public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
		}
	}
}
