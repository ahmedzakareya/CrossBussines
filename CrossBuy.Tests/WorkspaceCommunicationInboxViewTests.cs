using System.IO;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// THE WORKSPACE COMMUNICATION INBOX, AND THE PARTIAL THAT DRAWS IT.
	//
	// PROVENANCE, stated here because it is unusual and should not be discovered later from a blame:
	// Views/Workspace/_CommunicationInbox.cshtml and the Index.cshtml block that renders it were
	// ORPHANED SHARED-TREE WIP, uncommitted since 2026-10-14 and present in more than one worktree
	// with no author. TAB-0 ruled on 2026-11-08 to ADOPT rather than drop it, and named TAB-6 its
	// owner. This suite is the test that ruling required, and it is the first test the code has ever
	// had.
	//
	// WHAT IS PROVEN HERE AND WHAT IS NOT - the split is deliberate and worth reading before trusting
	// a green run:
	//
	//   Facts 1-3 are BEHAVIOURAL. They drive the real WorkspaceService against stub contracts and
	//   assert the state the partial branches on. A database is never opened: WorkspaceService holds
	//   no CrossDbContext, and the inbox arrives through ICommNotificationService.
	//
	//   Facts 4-6 are STRUCTURAL - they read the .cshtml as text. A Razor partial cannot be rendered
	//   in this suite: there is no view-rendering precedent in CrossBuy.Tests, and a renderer would
	//   need a web host. A source assertion FAILS OPEN by nature, so each one here pins an exact
	//   shape rather than the presence of a keyword, and each was mutation-tested by breaking the
	//   partial and confirming the test went red. What they CANNOT prove is that the rendered output
	//   is right - that is what TAB-0's /Workspace render probe covers, and neither replaces the other.
	// =================================================================================================
	public class WorkspaceCommunicationInboxViewTests
	{
		private const int Company = 1;
		private const int Employee = 11;

		// ==========================================================================================
		// 1-3: the state the partial branches on (behavioural)
		// ==========================================================================================

		[Fact]
		public async Task An_absent_communication_platform_leaves_the_inbox_unavailable()
		{
			// Unavailable is a FIRST-CLASS state, not an error and not Empty. Collapsing it into Empty
			// is how an undeployed platform reads as "you have no notifications".
			var inbox = await InboxAsync(notifications: null);

			Assert.Equal(WorkspacePanelState.Unavailable, inbox.State);
		}

		[Fact]
		public async Task An_inbox_with_no_rows_is_empty_not_ready()
		{
			// The partial's empty branch. Ready with zero rows would draw a headed, bordered list
			// containing nothing.
			var inbox = await InboxAsync(new StubInbox());

			Assert.Equal(WorkspacePanelState.Empty, inbox.State);
			Assert.Empty(inbox.Page.Items);
		}

		[Fact]
		public async Task An_inbox_with_rows_is_ready_and_carries_the_unread_count()
		{
			var inbox = await InboxAsync(new StubInbox(unread: 2, Row(1, "SalesInvoice", 4120), Row(2, "Task", 9)));

			Assert.Equal(WorkspacePanelState.Ready, inbox.State);
			Assert.Equal(2, inbox.Page.Items.Count);
			Assert.Equal(2, inbox.UnreadTotal);
		}

		// ==========================================================================================
		// 4-6: the partial's link treatment (structural - see the header for the limits)
		// ==========================================================================================

		[Fact]
		public void Every_row_link_in_the_partial_is_guarded_against_a_null_url()
		{
			// c.Url is null BY DESIGN for an entity the registry publishes no route for, so an
			// unguarded href here would emit <a> with no destination on an ordinary row, not a rare one.
			var source = Partial();

			foreach (var row in new[] { "n", "c" })
			{
				var href = "href=\"@" + row + ".Url\"";
				var guard = "IsNullOrWhiteSpace(" + row + ".Url)";

				var at = source.IndexOf(href, System.StringComparison.Ordinal);
				Assert.True(at > 0, "Expected a row link on " + row + ".Url in the partial.");

				var before = source.Substring(0, at);
				var guardAt = before.LastIndexOf(guard, System.StringComparison.Ordinal);
				Assert.True(guardAt > 0, row + ".Url is linked without a null guard in _CommunicationInbox.cshtml.");

				// The guard must be the one enclosing THIS anchor, not a distant earlier one.
				Assert.True(at - guardAt < 400,
					row + ".Url's nearest null guard is " + (at - guardAt) + " chars away - too far to be enclosing it.");
			}
		}

		[Fact]
		public void The_partial_falls_back_to_a_span_not_a_dead_anchor()
		{
			// An <a> with no href is a non-navigable placeholder per the HTML spec, but it still reads
			// as a link to a screen reader and still takes hover styling. A <span> is the honest element.
			var source = Partial();

			var spans = Count(source, "<span class=\"fw-bold text-gray-900\">");
			Assert.True(spans >= 2,
				"Expected an unlinked <span> fallback for both row kinds; found " + spans + ".");
		}

		[Fact]
		public void The_partial_never_substitutes_a_generic_list()
		{
			// The item-7 rule, pinned in the partial too: resolve to the intended record or to nothing.
			// A row that cannot be opened must not quietly navigate to the list it came from.
			var source = Partial();

			Assert.DoesNotContain("?? Url.Action", source, System.StringComparison.Ordinal);
			Assert.DoesNotContain("?? \"#\"", source, System.StringComparison.Ordinal);
		}

		// ==========================================================================================
		// harness
		// ==========================================================================================

		private static string Partial() => File.ReadAllText(Path.Combine(
			RepoRoot(), "CrossBuy", "Views", "Workspace", "_CommunicationInbox.cshtml"));

		private static int Count(string haystack, string needle)
		{
			int n = 0, at = 0;
			while ((at = haystack.IndexOf(needle, at, System.StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
			return n;
		}

		private static string RepoRoot()
		{
			var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
			if (!string.IsNullOrWhiteSpace(fromEnvironment)
				&& File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
			{
				return fromEnvironment;
			}

			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
			{
				dir = dir.Parent;
			}

			Assert.NotNull(dir);
			return dir!.FullName;
		}

		private static CommInboxItemDto Row(long id, string entityCode, int entityId) => new()
		{
			NotificationId = id,
			TemplateKey = "comment.mention",
			Category = "Mention",
			Priority = "Normal",
			Entity = new CommEntityRef(entityCode, entityId),
			TitleAr = "عنوان",
			TitleEn = "A title",
			BodyAr = "نص",
			BodyEn = "A body",
			CreatedAt = System.DateTime.Now,
		};

		private static async Task<WorkspaceCommunicationInbox> InboxAsync(ICommNotificationService? notifications)
		{
			var services = new ServiceCollection();
			if (notifications != null)
				services.AddSingleton(notifications);

			var context = new BusinessContext
			{
				CompanyId = Company, EmployeeId = Employee, UserId = "u" + Employee,
			};

			var workspace = new WorkspaceService(
				new StubContextAccessor(context),
				services.BuildServiceProvider(),
				new NoNotifications(),
				new NoIdentity(),
				Array.Empty<IWorkspaceFavoritesSource>(),
				Array.Empty<IWorkspaceActivitySource>(),
				Array.Empty<IWorkspaceReportSource>(),
				Array.Empty<IWorkspaceKpiSource>(),
				NullLogger<WorkspaceService>.Instance);

			// includeConversations: false keeps this off the Hub, which is a ten-member contract and a
			// different seam. The conversation row's null-Url production is already proven in
			// WorkspaceMentionLinkTests via the same registry guard.
			return await workspace.GetCommunicationInboxAsync(includeConversations: false);
		}

		private sealed class StubInbox : ICommNotificationService
		{
			private readonly CommInboxItemDto[] _items;
			private readonly int _unread;
			public StubInbox(int unread = 0, params CommInboxItemDto[] items) { _unread = unread; _items = items; }

			public Task<CommPage<CommInboxItemDto>> GetInboxAsync(
				BusinessContext context, bool unreadOnly = false, CommPageRequest? page = null,
				CancellationToken cancellationToken = default)
				=> Task.FromResult(new CommPage<CommInboxItemDto> { Items = _items });

			public Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(_unread);

			public Task<CommNotificationResult> QueueAsync(
				BusinessContext context, CommNotificationRequest request, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException("read-only stub");

			public Task<int> MarkReadAsync(
				BusinessContext context, IReadOnlyList<long>? notificationIds = null, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException("read-only stub");
		}

		private sealed class NoNotifications : IWorkspaceNotificationSource
		{
			public bool IsAvailable => true;
			public Task<IReadOnlyList<WorkspaceNotification>> GetAsync(
				BusinessContext context, bool unreadOnly, int take, CancellationToken cancellationToken = default)
				=> Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
			public Task<IReadOnlyList<WorkspaceNotification>> GetPageAsync(
				BusinessContext context, bool unreadOnly, int skip, int take, CancellationToken cancellationToken = default)
				=> Task.FromResult<IReadOnlyList<WorkspaceNotification>>(Array.Empty<WorkspaceNotification>());
			public Task<int> CountAsync(BusinessContext context, bool unreadOnly, CancellationToken cancellationToken = default)
				=> Task.FromResult(0);
			public Task<int> CountUnreadAsync(BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(0);
		}

		private sealed class NoIdentity : IWorkspaceIdentityResolver
		{
			public Task<(string EmployeeName, string? CompanyName)> ResolveAsync(
				BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(("tester", (string?)"Test Co"));
		}
	}
}
