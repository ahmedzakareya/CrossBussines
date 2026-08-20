using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// Pins the Workspace Approvals PANEL MARKUP.
	//
	// The model side is covered by WorkspaceApprovalsPanelTests; this class covers the thing a model test
	// cannot see — what the Razor actually emits. Three properties matter enough to be asserted rather
	// than reviewed once:
	//
	//   1. READ ONLY. No form, no POST, no approve/reject control. The panel's only affordance is a link
	//      into the owning module. A button added here would be a second approval surface with none of
	//      the module's checks behind it.
	//   2. NO MODULE ROUTES. Links use the navigation each MODULE supplied. The three silo pairs
	//      (People/Leaves, People/Requests, Inventory/Approvals) must not appear as literals — that is the
	//      duplication the read platform exists to prevent.
	//   3. HONEST STATES. The five panel states go through the shared PanelNotice with the panel's OWN
	//      state, so a failure cannot render as an empty inbox.
	//
	// The sweep is over the real file, and it fails loudly if the file cannot be found: a structural
	// assertion over an empty string passes vacuously, which is worse than no test.
	// =================================================================================================
	public class WorkspaceApprovalsViewTests
	{
		// ---------------------------------------------------------------------------------------------
		// The panel exists and is fed from the model
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_dashboard_renders_an_approvals_panel()
		{
			var view = View();

			Assert.Contains("<!--begin::Approvals-->", view);
			Assert.Contains("<!--end::Approvals-->", view);
			Assert.Contains("id=\"approvals\"", view);
			// The same card vocabulary as every other panel — no new design language.
			Assert.Contains("card card-flush mb-5 mb-xl-10\" id=\"approvals\"", view);
		}

		[Fact]
		public void The_pending_total_comes_from_the_model_and_is_not_recomputed()
		{
			var view = View();

			Assert.Contains("Model.PendingApprovals", view);
			// No Razor-side arithmetic over the rows: a count computed here could disagree with the
			// service's, and the service's is the one that counted the whole inbox rather than the page.
			Assert.DoesNotContain("Model.Approvals.Items.Count()", view);
			Assert.DoesNotContain("Model.Approvals.Items.Sum", view);
		}

		[Fact]
		public void The_rows_come_from_the_model_panel()
		{
			var view = View();

			Assert.Contains("foreach (var approval in Model.Approvals.Items)", view);
			Assert.Contains("Model.Approvals.HasItems", view);
		}

		[Fact]
		public void Each_row_shows_its_silo_and_its_module_type()
		{
			var view = View();

			Assert.Contains("SiloLabel(approval.Silo)", view);
			Assert.Contains("ApprovalTypeLabel(approval)", view);
			// The silo is named in TEXT. A colour-only indicator would be unreadable to anyone who cannot
			// distinguish the badge colours.
			Assert.Contains("badge badge-light fs-8 text-gray-800\">@SiloLabel(approval.Silo)", view);
		}

		// ---------------------------------------------------------------------------------------------
		// Navigation
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void Row_links_use_the_module_supplied_navigation()
		{
			var view = View();

			Assert.Contains("href=\"@approval.Url\"", view);
		}

		[Fact]
		public void The_view_hardcodes_no_module_route()
		{
			var view = View();

			// The three silo pairs, in either argument order Url.Action accepts.
			foreach (var pair in new[]
			{
				"\"Leaves\", \"People\"", "\"People\", \"Leaves\"",
				"\"Requests\", \"People\"", "\"People\", \"Requests\"",
				"\"Approvals\", \"Inventory\"", "\"Inventory\", \"Approvals\"",
			})
				Assert.DoesNotContain(pair, view);

			// Nor as raw paths.
			foreach (var path in new[] { "/People/Leaves", "/People/Requests", "/Inventory/Approvals" })
				Assert.DoesNotContain(path, view);
		}

		// ---------------------------------------------------------------------------------------------
		// States — all five, honestly
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void Every_non_data_state_goes_through_the_shared_panel_notice_with_this_panels_state()
		{
			var panel = Panel(View());

			// PanelNotice already renders Empty, Unavailable, AccessDenied, TemporaryFailure and
			// PartiallyAvailable distinctly. Passing the panel's OWN state is what stops a failure being
			// drawn as "no approvals".
			Assert.Contains("PanelNotice(Model.Approvals.State", panel);

			// Scoped to THIS panel: sibling panels legitimately pass a literal state for their
			// partially-available notice, and a page-wide scan would report that as a violation here.
			// Inside the approvals panel a literal would collapse every failure into an empty inbox.
			Assert.DoesNotContain("PanelNotice(WorkspacePanelState.", panel);
		}

		[Fact]
		public void No_placeholder_or_fabricated_approval_row_is_rendered()
		{
			var view = View();
			var panel = Panel(view);

			// Rows exist only inside the model loop. Any row markup outside it would be invented data.
			foreach (var fake in new[] { "Lorem", "placeholder", "Sample", "dummy", "TODO" })
				Assert.DoesNotContain(fake, panel, StringComparison.OrdinalIgnoreCase);
		}

		// ---------------------------------------------------------------------------------------------
		// Read-only
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_panel_contains_no_mutation_control()
		{
			var panel = Panel(View());

			foreach (var control in new[] { "<form", "<button", "type=\"submit\"", "method=\"post\"", "asp-action", "onclick" })
				Assert.DoesNotContain(control, panel, StringComparison.OrdinalIgnoreCase);

			foreach (var verb in new[] { "Approve", "Reject", "Release", "Confirm", "Escalate", "Quick" })
				Assert.DoesNotContain(verb, panel, StringComparison.Ordinal);
		}

		[Fact]
		public void The_view_queries_no_approval_table_and_resolves_no_service()
		{
			var view = View();

			foreach (var table in new[] { "LeaveRequests", "EmployeeRequests", "InventoryApprovals", "InventoryUserRoles" })
				Assert.DoesNotContain(table, view);

			// The data path is module readers -> ApprovalInboxService -> WorkspaceService -> model. The view
			// is the last hop and must not open a second one.
			Assert.DoesNotContain("IApprovalInboxService", view);
			Assert.DoesNotContain("GetPendingForCurrentApproverAsync", view);
			Assert.DoesNotContain("GetService", view);
			Assert.DoesNotContain("CrossDbContext", view);
		}

		// ---------------------------------------------------------------------------------------------
		// Accessibility and bidirectionality
		// ---------------------------------------------------------------------------------------------

		[Fact]
		public void The_panel_is_reachable_and_labelled()
		{
			var panel = Panel(View());

			// A semantic heading, like every sibling panel, so the section is navigable by landmark.
			Assert.Contains("<h3 class=\"card-title", panel);

			// The link carries an accessible name that includes the silo, so "Letter" alone is not the
			// entire announcement.
			Assert.Contains("aria-label=", panel);

			// Decorative icons are hidden from assistive technology rather than announced as "check square".
			Assert.Contains("aria-hidden=\"true\"", panel);

			// The only interactive element is an anchor with visible text — no icon-only control.
			Assert.DoesNotContain("<i class=\"ki-outline\"></i>", panel);
		}

		[Fact]
		public void The_panel_uses_direction_neutral_spacing_so_rtl_is_not_mirrored_wrongly()
		{
			var panel = Panel(View());

			// Metronic logical properties: ms-/me-/text-end flip with direction; ml-/mr-/text-left do not.
			foreach (var physical in new[] { "text-left", "text-right", "ml-", "mr-", "pl-", "pr-" })
				Assert.DoesNotContain(physical, panel, StringComparison.Ordinal);
		}

		[Fact]
		public void Rtl_content_is_localized_rather_than_dropped()
		{
			// Every user-visible string in the panel comes from a localizer, so the Arabic render carries
			// the same content rather than falling back to a key or an empty cell. The silo and type labels
			// reuse SharedResources — the same translations the Approvals screen shows.
			var panel = Panel(View());

			Assert.Contains("@Shared[\"Approvals\"]", panel);
			Assert.Contains("@Shared[\"days\"]", panel);
			Assert.Contains("Shared[\"No pending approvals\"]", panel);
			Assert.Contains("@Localizer[\"Type\"]", panel);
			Assert.Contains("@Localizer[\"Details\"]", panel);
			Assert.Contains("@Localizer[\"When\"]", panel);

			// And the strings actually exist in the Arabic resource, so the fallback-to-key path is never
			// taken. A missing key renders English inside an RTL page, which is the defect this catches.
			var arabic = Resource("SharedResources.ar.resx");
			foreach (var key in new[]
			{
				"Approvals", "days", "No pending approvals", "Leave", "Employee request", "Inventory",
				"Permission", "Letter", "Purchase order", "Transfer", "Stock count", "Write-off",
			})
				Assert.Contains($"name=\"{key}\"", arabic);

			var viewArabic = Resource("Views/Workspace/Index.ar.resx");
			foreach (var key in new[] { "Type", "Details", "When", "View all", "Now" })
				Assert.Contains($"name=\"{key}\"", viewArabic);
		}

		// ---------------------------------------------------------------------------------------------
		// Helpers
		// ---------------------------------------------------------------------------------------------

		/// <summary>Just the Approvals panel, so a match elsewhere on the page cannot satisfy an assertion
		/// about this one — or hide a violation inside it.</summary>
		private static string Panel(string view)
		{
			const string start = "<!--begin::Approvals-->";
			const string end = "<!--end::Approvals-->";
			var from = view.IndexOf(start, StringComparison.Ordinal);
			var to = view.IndexOf(end, StringComparison.Ordinal);
			Assert.True(from >= 0 && to > from, "the Approvals panel markers were not found");
			return view[from..to];
		}

		private static string View() => ReadRepoFile("CrossBuy", "Views", "Workspace", "Index.cshtml");

		private static string Resource(string relative) =>
			ReadRepoFile(new[] { "CrossBuy", "Resources" }.Concat(relative.Split('/')).ToArray());

		private static string ReadRepoFile(params string[] parts)
		{
			for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
			{
				var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
				if (File.Exists(candidate)) return File.ReadAllText(candidate);
			}

			Assert.Fail($"{string.Join("/", parts)} could not be located from the test assembly directory. " +
						"The markup guards cannot run, and a sweep over an empty string would pass vacuously.");
			return string.Empty;
		}
	}
}
