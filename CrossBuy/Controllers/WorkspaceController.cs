using CrossBuy.BL.Workspace;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
    // ============================================================================================
    // CrossBusiness Workspace — the first user-facing product.
    //
    // THIN BY CONSTRUCTION. Every action resolves a view model from IWorkspaceService and returns a view.
    // There is no business rule, no query and no permission decision here: the read model consumes services
    // that already own those, and the caller's company/employee come from the resolved BusinessContext rather
    // than from anything on the request.
    //
    // [SessionValidation] matches every other backend screen: the shell requires a signed-in session, and an
    // expired one is bounced by the shared filter rather than by a check repeated in each action.
    //
    // NO MUTATING ENDPOINT. See the mark-as-read handover in
    // docs/platform/Stage-Workspace-Integration-05-Communication-States.md — the endpoint stays withdrawn
    // until TAB 1 approves an authority the Roslyn analyzer recognises.
    // ============================================================================================
    [SessionValidation]
    public class WorkspaceController : Controller
    {
        private readonly IWorkspaceService _workspace;

        public WorkspaceController(IWorkspaceService workspace) { _workspace = workspace; }

        // GET /Workspace  ·  GET /Workspace/Index
        //
        // The dashboard: metrics, My Work, Agenda, Reports, Favourites, Recent Activity, Quick Actions and the
        // attention rail. ONE service call assembles all of it, so a panel that cannot load leaves the others
        // intact.
        public async Task<IActionResult> Index(CancellationToken cancellationToken)
        {
            var model = await _workspace.GetDashboardAsync(cancellationToken);
            return View(model);
        }

        // GET /Workspace/Agenda?days=7
        //
        // Consumes IWorkspaceAgendaService — never a Task or Calendar table. `days` is clamped inside the
        // service, so a crafted value cannot turn this into an unbounded scan.
        public async Task<IActionResult> Agenda(int days = 7, int page = 1, CancellationToken cancellationToken = default)
        {
            var panel = await _workspace.GetAgendaAsync(days, page, cancellationToken);
            ViewBag.Days = days;
            ViewBag.Capabilities = await _workspace.GetCapabilitiesAsync(cancellationToken);
            return View(panel);
        }

        // GET /Workspace/Reports
        //
        // Consumes Reporting EXTENSION SOURCES only. No report is generated, rendered or exported here — this
        // screen lists shortcuts and hands off to the Reporting product.
        public async Task<IActionResult> Reports(CancellationToken cancellationToken)
        {
            var panel = await _workspace.GetReportsAsync(cancellationToken);
            ViewBag.Capabilities = await _workspace.GetCapabilitiesAsync(cancellationToken);
            return View(panel);
        }

        // GET /Workspace/Notifications?unreadOnly=false
        public async Task<IActionResult> Notifications(bool unreadOnly = false,
            CancellationToken cancellationToken = default)
        {
            var panel = await _workspace.GetNotificationsAsync(unreadOnly, take: 50, cancellationToken);
            ViewBag.UnreadOnly = unreadOnly;
            ViewBag.Capabilities = await _workspace.GetCapabilitiesAsync(cancellationToken);
            return View(panel);
        }

        // GET /Workspace/Mentions
        //
        // Reachable even when Communication is NOT activated — deliberately. The nav entry is hidden in that
        // case, but the route still renders the explicit "platform not activated" state, so a reviewer can
        // open the URL and see exactly what a user would see once it is switched on.
        public async Task<IActionResult> Mentions(CancellationToken cancellationToken)
        {
            var panel = await _workspace.GetMentionsAsync(take: 50, cancellationToken);
            ViewBag.Capabilities = await _workspace.GetCapabilitiesAsync(cancellationToken);
            return View(panel);
        }
    }
}
