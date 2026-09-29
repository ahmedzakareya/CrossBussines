using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Portal;
using CrossBuy.Controllers;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Communication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.Portal
{
    // =============================================================================================
    // WAVE 3B-D — THE PORTAL CONVERSATION UI.
    //
    // These test the CONTROLLER INTEGRATION and nothing else. The authority itself was proven in
    // 3B-C by CommPortalServiceTests over the real CommExternalAccessPolicy, and duplicating that
    // suite here would create a second place for the boundary to be described - and therefore a
    // place it can disagree. What is under test is the thing 3B-D actually added: that each action
    // resolves the portal identity server-side, delegates to that same service, and renders only
    // what came back.
    //
    // THE UI IS NOT AUTHORITY, and these prove it by construction rather than by assertion: the
    // controller contains no company, customer or visibility predicate of its own, so there is
    // nothing here that could disagree with the service.
    // =============================================================================================

    internal sealed class PortalUiHost : IDisposable
    {
        public const int CompanyA = 1, CompanyB = 2;
        public const int CustomerAcme = 100, CustomerBeta = 200;
        public const int PortalAcme = 10, PortalBeta = 20;

        private readonly ExternalActorHost _inner = new();
        private readonly StubPortalContext _contexts = new();

        public CrossBuy.Models.Context.CrossDbContext Db => _inner.Db;

        public void ActAs(int portalUserId = PortalAcme, int customerId = CustomerAcme, int companyId = CompanyA)
            => _contexts.Current = new PortalContext(companyId, customerId, portalUserId, PortalCapabilities.Default);

        public void ActAsNobody() => _contexts.Current = null;

        public ClientPortalController Controller()
        {
            var options = Options.Create(new CommunicationPlatformOptions());
            var conversations = new CommPortalService(
                Db, new CommExternalAccessPolicy(Db), new CommBodyPolicy(options), options);

            // DOC1 added a fourth dependency. The conversation tests do not exercise documents, but
            // the controller is constructed whole - a null here would turn an unrelated future change
            // into a NullReferenceException in this file rather than a compile error.
            var documents = new PortalDocumentService(Db, _contexts, new LocalDocumentStorage(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cb-ui-" + Guid.NewGuid().ToString("n"))));

            // The portal convergence added a fifth dependency, for the same reason DOC1 added the
            // fourth: the controller is constructed WHOLE, so an unrelated future change is a compile
            // error in this file rather than a NullReferenceException inside an assertion.
            var photos = PortalPhotoTest.Service(Db, _contexts, new TempContentRoot());

            var controller = new ClientPortalController(
                new PortalDataService(Db, _contexts), conversations, _contexts, documents, photos)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext(),
                    RouteData = new RouteData(),
                    ActionDescriptor = new ControllerActionDescriptor(),
                },
            };
            controller.ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary());
            controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProviderUi());
            return controller;
        }

        public CommThread SeedThread(int customerId, int companyId, string visibility = CommVisibility.Public)
            => _inner.SeedPublicThread(customerId, companyId, visibility);

        public void AddPortal(CommThread t, int portalUserId) => _inner.AddPortalParticipant(t, portalUserId);

        public CommComment SeedComment(CommThread t, CommActor author, string visibility)
            => _inner.SeedComment(t, author, visibility);

        /// A thread this portal user may fully use. Every refusal test breaks exactly one condition.
        public CommThread SeedUsableThread()
        {
            var t = SeedThread(CustomerAcme, CompanyA);
            AddPortal(t, PortalAcme);
            return t;
        }

        public void Dispose() => _inner.Dispose();
    }

    internal sealed class NullTempDataProviderUi : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    // =============================================================================================
    public class PortalConversationUiTests
    {
        private static T ModelOf<T>(IActionResult result) where T : class
        {
            var view = Assert.IsType<ViewResult>(result);
            return Assert.IsAssignableFrom<T>(view.Model);
        }

        // ---- LIST ----------------------------------------------------------------------------------

        [Fact]
        public async Task A_portal_customer_lists_their_own_conversations()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.ActAs();

            var model = ModelOf<IReadOnlyList<PortalConversationDto>>(await host.Controller().Updates());

            var one = Assert.Single(model);
            Assert.Equal(thread.Id, one.ThreadId);
        }

        [Fact]
        public async Task An_unresolved_portal_identity_gets_the_refusal_screen_not_an_empty_list()
        {
            using var host = new PortalUiHost();
            host.SeedUsableThread();
            host.ActAsNobody();

            // An external caller must never see a working-looking portal with nothing in it: that is
            // indistinguishable from a customer who genuinely has no conversations.
            var view = Assert.IsType<ViewResult>(await host.Controller().Updates());
            Assert.Equal("NoAccess", view.ViewName);
        }

        // ---- OPEN ----------------------------------------------------------------------------------

        [Fact]
        public async Task A_portal_customer_opens_a_conversation_that_is_theirs()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.ActAs();

            var result = await host.Controller().Conversation(thread.Id);
            var model = ModelOf<PortalConversationDto>(result);

            Assert.Equal(thread.Id, model.ThreadId);
            var view = (ViewResult)result;
            var comments = Assert.IsAssignableFrom<IReadOnlyList<PortalCommentDto>>(view.ViewData["Comments"]);
            Assert.Single(comments);
        }

        [Fact]
        public async Task A_foreign_customer_cannot_open_it()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();

            // Same company, valid portal identity, different customer.
            host.ActAs(portalUserId: PortalUiHost.PortalBeta, customerId: PortalUiHost.CustomerBeta);

            Assert.IsType<NotFoundResult>(await host.Controller().Conversation(thread.Id));
        }

        [Fact]
        public async Task Another_company_cannot_open_it()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.ActAs(companyId: PortalUiHost.CompanyB);

            Assert.IsType<NotFoundResult>(await host.Controller().Conversation(thread.Id));
        }

        [Fact]
        public async Task An_internal_thread_stays_inaccessible_through_the_UI()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedThread(PortalUiHost.CustomerAcme, PortalUiHost.CompanyA, CommVisibility.Internal);
            host.AddPortal(thread, PortalUiHost.PortalAcme);
            host.ActAs();

            Assert.IsType<NotFoundResult>(await host.Controller().Conversation(thread.Id));
            Assert.Empty(ModelOf<IReadOnlyList<PortalConversationDto>>(await host.Controller().Updates()));
        }

        // ---- COMMENT SCOPING -----------------------------------------------------------------------

        [Fact]
        public async Task Only_externally_visible_comments_reach_the_view()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Internal);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Confidential);
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Restricted);
            host.ActAs();

            var view = (ViewResult)await host.Controller().Conversation(thread.Id);
            var comments = Assert.IsAssignableFrom<IReadOnlyList<PortalCommentDto>>(view.ViewData["Comments"]);

            Assert.Single(comments);
        }

        // ---- POST ----------------------------------------------------------------------------------

        [Fact]
        public async Task Posting_a_comment_writes_the_current_portal_actor()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.ActAs();

            var redirect = Assert.IsType<RedirectToActionResult>(
                await host.Controller().PostComment(thread.Id, "from the customer"));
            Assert.Equal(nameof(ClientPortalController.Conversation), redirect.ActionName);

            var row = host.Db.Set<CommComment>().AsNoTracking()
                .Single(c => c.ThreadId == thread.Id && c.AuthorPortalUserId != null);
            Assert.Equal(PortalUiHost.PortalAcme, row.AuthorPortalUserId);
            Assert.Null(row.AuthorEmployeeId);                       // never employee 0
            Assert.Equal(CommVisibility.Public, row.Visibility);     // never Internal
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task An_empty_comment_is_rejected_and_writes_nothing(string? body)
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.ActAs();

            // The service validates; the controller does NOT re-validate, so this proves the refusal
            // travels rather than that the view layer grew its own rule.
            var controller = host.Controller();
            var result = await controller.PostComment(thread.Id, body);

            // Nothing written...
            Assert.Empty(host.Db.Set<CommComment>().Where(c => c.ThreadId == thread.Id));

            // ...AND the customer is told. Without this the test would pass just as well if the
            // refusal were swallowed, which is how a form that silently does nothing ships. The
            // body policy THROWS for an empty body; an escaping exception would have been a 500.
            Assert.IsType<RedirectToActionResult>(result);
            Assert.True(controller.TempData.ContainsKey("PortalCommentError"));
        }

        [Fact]
        public async Task A_foreign_customer_cannot_post_into_someone_elses_conversation()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.ActAs(portalUserId: PortalUiHost.PortalBeta, customerId: PortalUiHost.CustomerBeta);

            await host.Controller().PostComment(thread.Id, "let me in");

            Assert.Empty(host.Db.Set<CommComment>().Where(c => c.ThreadId == thread.Id));
        }

        // ---- MARK READ -----------------------------------------------------------------------------

        [Fact]
        public async Task Mark_read_writes_the_portal_actor_server_side()
        {
            using var host = new PortalUiHost();
            var thread = host.SeedUsableThread();
            host.SeedComment(thread, CommActor.Employee(7), CommVisibility.Public);
            host.ActAs();

            Assert.IsType<RedirectToActionResult>(await host.Controller().MarkConversationRead(thread.Id));

            var receipt = host.Db.Set<CommReadReceipt>().AsNoTracking().Single(r => r.ThreadId == thread.Id);
            Assert.Equal(PortalUiHost.PortalAcme, receipt.PortalUserId);
            Assert.Null(receipt.EmployeeId);
        }

        [Fact]
        public async Task Mark_read_on_a_foreign_conversation_writes_nothing()
        {
            using var host = new PortalUiHost();
            var stranger = host.SeedThread(PortalUiHost.CustomerBeta, PortalUiHost.CompanyA);
            host.ActAs();

            await host.Controller().MarkConversationRead(stranger.Id);

            Assert.Empty(host.Db.Set<CommReadReceipt>().Where(r => r.ThreadId == stranger.Id));
        }

        // ---- THE SURFACE ITSELF --------------------------------------------------------------------

        [Fact]
        public void Both_UI_mutations_are_anti_forgery_protected_and_portal_authorized()
        {
            var type = typeof(ClientPortalController);

            // The portal policy, not bare [Authorize] - the 3A-0B defect, which admits staff.
            var authorize = type.GetCustomAttributes(inherit: true).OfType<PortalAuthorizeAttribute>().ToList();
            Assert.NotEmpty(authorize);
            Assert.All(authorize, a => Assert.Equal(PortalAuthenticationDefaults.PolicyName, a.Policy));

            foreach (var name in new[] { nameof(ClientPortalController.PostComment),
                                         nameof(ClientPortalController.MarkConversationRead) })
            {
                var action = type.GetMethod(name)!;
                Assert.NotNull(action.GetCustomAttribute<HttpPostAttribute>());
                Assert.NotNull(action.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
            }
        }

        [Fact]
        public void No_UI_action_accepts_an_identity_parameter()
        {
            var forbidden = new[] { "companyid", "customerid", "portaluserid", "employeeid", "visibility", "authorid" };

            foreach (var action in typeof(ClientPortalController)
                         .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var p in action.GetParameters())
                    Assert.DoesNotContain(forbidden, f => string.Equals(p.Name, f, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void The_UI_exposes_no_operation_the_external_service_does_not_offer()
        {
            var actions = typeof(ClientPortalController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).ToList();

            // Delete, moderation, participant management, thread creation and upload are not hidden
            // controls - they are absent, because the service they would have to call does not exist.
            foreach (var forbidden in new[] { "Delete", "Remove", "Moderate", "Lock", "Upload",
                                              "CreateThread", "AddParticipant", "Assign", "Escalate" })
                Assert.DoesNotContain(actions, a => a.Contains(forbidden, StringComparison.Ordinal));
        }

        [Fact]
        public void The_views_never_render_a_storage_key_path_or_download_link()
        {
            foreach (var name in new[] { "Updates.cshtml", "Conversation.cshtml" })
            {
                // RAZOR COMMENTS ARE STRIPPED FIRST. @* ... *@ never reaches the rendered page, so a
                // comment that NAMES StorageKey in order to say it is deliberately absent is
                // documentation, not a leak - and an assertion that cannot tell the two apart would
                // punish the explanation rather than the defect.
                var text = StripRazorComments(System.IO.File.ReadAllText(Repo("CrossBuy/Views/ClientPortal/" + name)));
                foreach (var forbidden in new[] { "StorageKey", "ThumbnailStorageKey", "/Files/", "FileManager" })
                    Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }

            // Structural, not incidental: the DTO the view is handed carries no such member at all.
            var names = typeof(PortalAttachmentDto).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();
            Assert.DoesNotContain(names, n => n.Contains("storage") || n.Contains("path")
                                              || n.Contains("url") || n.Contains("uri"));
        }

        // REWRITTEN BY THE PORTAL CONVERGENCE, and the two assertions that changed are worth naming
        // because they reversed rather than relaxed.
        //
        //   WAS `Layout = "_Layout"`. That shell was the plain cbp-* one this wave deleted. The five
        //   retained Metronic screens never used it — each is a whole demo30 document — so keeping a
        //   second shell for the three converged screens would have been the "second competing portal
        //   design" the convergence exists to remove. The contract is now the opposite: Layout = null
        //   plus the retained header.
        //
        //   WAS "no <script> anywhere". That was true of the plain shell and cannot be true of the
        //   theme: demo30's own bundles are how its menus, drawers and sticky header work, and the
        //   five retained screens have always loaded them. What the rule was PROTECTING is kept and
        //   made explicit below — the portal still ships no bespoke behaviour of its own.
        [Fact]
        public void The_views_follow_the_portals_own_bilingual_convention_and_carry_only_theme_script()
        {
            foreach (var name in new[] { "Updates.cshtml", "Conversation.cshtml", "Documents.cshtml" })
            {
                var text = System.IO.File.ReadAllText(Repo("CrossBuy/Views/ClientPortal/" + name));

                // The portal localizes with inline ternaries, not resources - measured, not assumed.
                Assert.Contains("isAr", text);

                // The converged contract: a whole document wearing the retained skin.
                Assert.Contains("Layout = null", text);
                Assert.Contains("Html.PartialAsync(\"_PortalHeader\")", text);
                Assert.Contains("/portal-assets/", text);

                // NO BESPOKE PORTAL JAVASCRIPT. Every <script> is either the theme's own bundle or the
                // theme's inline theme-mode bootstrap; nothing on these screens is behaviour this
                // product wrote. That is what the old "no script at all" rule was really protecting.
                var scripts = System.Text.RegularExpressions.Regex.Matches(
                    text, "<script(?<attrs>[^>]*)>(?<body>.*?)</script>",
                    System.Text.RegularExpressions.RegexOptions.Singleline);

                foreach (System.Text.RegularExpressions.Match s in scripts)
                {
                    var src = s.Groups["attrs"].Value;
                    var body = s.Groups["body"].Value;

                    bool themeBundle = src.Contains("/portal-assets/", StringComparison.OrdinalIgnoreCase);
                    bool hostUrl = body.Contains("hostUrl", StringComparison.Ordinal);
                    bool themeMode = body.Contains("defaultThemeMode", StringComparison.Ordinal);

                    Assert.True(themeBundle || hostUrl || themeMode,
                        $"{name} carries a script that is neither a theme bundle nor the theme's own bootstrap");
                }

                // The internal product's shell is still never imported over the portal's own.
                foreach (var forbidden in new[] { "_LayoutBackend", "_LayoutAccounting", "/Backend-assets/css/style.bundle" })
                    Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void The_UI_wires_no_task_notification_or_business_event()
        {
            var controller = System.IO.File.ReadAllText(Repo("CrossBuy/Controllers/ClientPortalController.cs"));

            foreach (var forbidden in new[] { "ITaskService", "TaskOrchestration", "INotificationService",
                                              "NotificationsHub", "IBusinessEventService", "IFileManagerService" })
                Assert.DoesNotContain(forbidden, controller, StringComparison.Ordinal);
        }

        /// Removes @* ... *@ blocks, which the Razor engine never renders.
        private static string StripRazorComments(string razor)
            => System.Text.RegularExpressions.Regex.Replace(
                razor, @"@\*.*?\*@", "", System.Text.RegularExpressions.RegexOptions.Singleline);

        private static string Repo(string relative)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "CrossBuy", "BL")))
                dir = System.IO.Directory.GetParent(dir)?.FullName;
            Assert.NotNull(dir);
            return System.IO.Path.Combine(dir!, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        }
    }
}
