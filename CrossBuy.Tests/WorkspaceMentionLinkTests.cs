using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Platform;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// A MENTION LINKS TO THE RECORD IT NAMES.
	//
	// THE DEFECT: every mention row carried Url = null, and the view then substituted the Mentions list
	// (`mention.Url ?? Url.Action("Mentions", "Workspace")`). A reader standing ON the Mentions page
	// clicked "@you on Invoice #4120" and arrived back at the Mentions page. That is worse than a dead
	// link, because a dead link is visibly dead: this one looked like it worked.
	//
	// THE RULE THESE TESTS PIN: resolve to the intended record, or to NOTHING. Never to a generic list.
	// A null Url is an honest "there is no detail screen for this yet" and the view renders the row
	// unlinked; a list Url is a wrong answer wearing the costume of a right one.
	//
	// WHY THE GUARD IS TryGetDefinition AND NOT A NULL CHECK: IEntityRegistry.BuildUrl calls
	// GetDefinition, which THROWS EntityCodeNotRegisteredException on a code it does not know. A mention
	// is HISTORY - it can outlive the vocabulary it was written against - so asking BuildUrl blind turns
	// one retired entity code into a dead Mentions panel for everybody. That is what the third test
	// below exists to stop, and it is the reason this is not a one-line change.
	//
	// DATABASE-FREE BY CONSTRUCTION, not by mocking: WorkspaceService has no CrossDbContext, and
	// EntityRegistry's definitions/TryGetDefinition/BuildUrl never dereference its context. So the whole
	// suite runs with `new EntityRegistry(null!)` and no database of any kind.
	// =================================================================================================
	public class WorkspaceMentionLinkTests
	{
		private const int Company = 1;
		private const int Employee = 11;

		[Fact]
		public async Task A_mention_links_to_the_record_it_names()
		{
			var dash = await DashboardAsync(Mention(1, "SalesInvoice", 4120));

			var mention = Assert.Single(dash.Mentions.Items);
			Assert.Equal("/Accounting/SalesInvoiceDetail?id=4120", mention.Url);
		}

		[Fact]
		public async Task A_mention_on_a_routeless_entity_has_no_link_rather_than_a_list_link()
		{
			// ProjectBilling publishes no RouteTemplate ON PURPOSE: its screen needs BOTH a project id
			// and a billing id, and this resolver has only the billing id. The right answer is no link.
			var dash = await DashboardAsync(Mention(1, EntityRegistry.ProjectBilling, 77));

			var mention = Assert.Single(dash.Mentions.Items);
			Assert.Null(mention.Url);
		}

		[Fact]
		public async Task A_mention_on_an_unregistered_code_does_not_take_the_panel_down()
		{
			// The regression guard. BuildUrl THROWS on this code; an unguarded call would surface as a
			// TemporaryFailure panel - every mention lost because one row named a retired entity.
			var dash = await DashboardAsync(
				Mention(1, "RetiredThingNobodyRegistered", 5),
				Mention(2, "SalesInvoice", 4120));

			Assert.Equal(WorkspacePanelState.Ready, dash.Mentions.State);
			Assert.Equal(2, dash.Mentions.Items.Count);
			Assert.Null(dash.Mentions.Items[0].Url);
			Assert.Equal("/Accounting/SalesInvoiceDetail?id=4120", dash.Mentions.Items[1].Url);
		}

		[Fact]
		public async Task No_mention_ever_links_to_a_workspace_list()
		{
			var dash = await DashboardAsync(
				Mention(1, "SalesInvoice", 4120),
				Mention(2, EntityRegistry.CalendarEvent, 9),
				Mention(3, "RetiredThingNobodyRegistered", 5));

			// The count and state are asserted FIRST so this cannot pass vacuously. Mutation analysis
			// caught exactly that: with the TryGetDefinition guard removed, BuildUrl throws, the panel
			// degrades to TemporaryFailure with zero rows, and an Assert.All over an empty list is
			// green. An "every row is fine" test must first prove there are rows.
			Assert.Equal(WorkspacePanelState.Ready, dash.Mentions.State);
			Assert.Equal(3, dash.Mentions.Items.Count);

			Assert.All(dash.Mentions.Items, m =>
				Assert.True(m.Url == null || !m.Url.StartsWith("/Workspace/", System.StringComparison.Ordinal),
					"A mention resolved to a Workspace list instead of its record: " + m.Url));
		}

		[Fact]
		public async Task Without_the_registry_mentions_still_list_unlinked()
		{
			// A deployment without the kernel registry keeps its Mentions panel; it loses the links only.
			var dash = await DashboardAsync(false, Mention(1, "SalesInvoice", 4120));

			var mention = Assert.Single(dash.Mentions.Items);
			Assert.Equal(WorkspacePanelState.Ready, dash.Mentions.State);
			Assert.Null(mention.Url);
		}

		[Fact]
		public async Task The_attention_row_for_a_mention_carries_the_same_record_link()
		{
			// The Attention card re-presents mentions, so a null there was the SAME defect rendered in a
			// second place. ComposeAttention carries the row's Url rather than re-deriving one.
			var dash = await DashboardAsync(Mention(1, "SalesInvoice", 4120));

			var row = Assert.Single(dash.Attention.Items,
				i => i.Reason == WorkspaceAttentionReason.Mention);
			Assert.Equal("/Accounting/SalesInvoiceDetail?id=4120", row.Url);
		}

		// ==========================================================================================
		// harness
		// ==========================================================================================

		private static CommMentionHistoryItemDto Mention(long id, string entityCode, int entityId) => new()
		{
			MentionId = id,
			CommentId = id,
			ThreadId = id,
			Entity = new CommEntityRef(entityCode, entityId),
			TargetKind = "Employee",
			ViaKind = "Direct",
			MentionedBy = new CommActorDto { EmployeeId = 42, NameEn = "Reem" },
			Excerpt = "look at this",
			CreatedAt = System.DateTime.Now,
		};

		private static Task<WorkspaceDashboard> DashboardAsync(params CommMentionHistoryItemDto[] mentions)
			=> DashboardAsync(true, mentions);

		private static async Task<WorkspaceDashboard> DashboardAsync(
			bool withRegistry, params CommMentionHistoryItemDto[] mentions)
		{
			var services = new ServiceCollection();
			services.AddSingleton<ICommMentionService>(new StubMentions(mentions));
			if (withRegistry)
				services.AddSingleton<IEntityRegistry>(new EntityRegistry(null!));

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

			return await workspace.GetDashboardAsync();
		}

		private sealed class StubMentions : ICommMentionService
		{
			private readonly CommMentionHistoryItemDto[] _items;
			public StubMentions(CommMentionHistoryItemDto[] items) { _items = items; }

			public Task<CommPage<CommMentionHistoryItemDto>> GetHistoryAsync(
				BusinessContext context, CommPageRequest? page = null, CancellationToken cancellationToken = default)
				=> Task.FromResult(new CommPage<CommMentionHistoryItemDto> { Items = _items });

			public Task<int> GetUnreadCountAsync(BusinessContext context, CancellationToken cancellationToken = default)
				=> Task.FromResult(_items.Length);

			public Task<CommMentionOutcome> RecordAsync(
				BusinessContext context, CommThread thread, CommComment comment,
				IReadOnlyList<CommMentionToken> tokens, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException("read-only stub");

			public Task<int> MarkReadAsync(
				BusinessContext context, IReadOnlyList<long>? mentionIds = null, CancellationToken cancellationToken = default)
				=> throw new NotSupportedException("read-only stub");

			public Task<IReadOnlyList<CommMentionDto>> ListForCommentAsync(
				BusinessContext context, long commentId, CancellationToken cancellationToken = default)
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
