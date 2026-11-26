using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context;
using CrossBuy.Models.Menu;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	/// <summary>
	/// The opening screen after sign-in: every system the user may open, as a launchpad.
	///   · A system shows only when its module answers "read" for this user (SystemCatalog.Scope); a system
	///     with no scope (Back-office, the employee portal, chat) shows to anyone signed in.
	///   · The red counts are what waits for the user — approvals, overdue tasks, unread conversations —
	///     read from the Workspace aggregator, the same numbers the workspace shows; if it cannot answer,
	///     the screen simply shows no counts.
	///   · Search (Ctrl+K) lists every system and every screen the systems publish in MainMenu.
	/// </summary>
	public class PortalController : Controller
	{
		public sealed record LauncherSystem(SystemEntry Entry, string Url, int? Count, string? CountAr, string? CountEn);
		public sealed record LauncherScreen(string System, string Ar, string En, string Url);
		public sealed record LauncherSignal(int Count, string Ar, string En, string Url, bool Urgent);
		public sealed record LauncherModel(string Name, string? Photo, string? CompanyName, IReadOnlyList<LauncherSystem> Systems,
			IReadOnlyList<LauncherScreen> Screens, IReadOnlyList<LauncherSignal> Signals, int Unread);

		private readonly IBusinessContextAccessor _contexts;
		private readonly ISystemVisibility _visibility;
		private readonly IServiceProvider _services;
		private readonly CrossDbContext _db;
		private readonly ILogger<PortalController> _log;

		public PortalController(IBusinessContextAccessor contexts, ISystemVisibility visibility, IServiceProvider services, CrossDbContext db, ILogger<PortalController> log)
		{ _contexts = contexts; _visibility = visibility; _services = services; _db = db; _log = log; }

		public async Task<IActionResult> Choose(CancellationToken ct = default)
		{
			var json = HttpContext.Session.GetString("Employee");
			if (json == null) return RedirectToAction("Login", "Account");
			var emp = JsonSerializer.Deserialize<EmployeeViewModel>(json);
			var ctx = await _contexts.TryGetCurrentAsync(ct);
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";

			// Which systems this user may open. The rule moved to ISystemVisibility when the header
			// search box began offering screens: two copies of a visibility rule drift, and the drift
			// shows up as a system the launcher hides and the search box still offers.
			var visible = await _visibility.VisibleAsync(ctx, ct);

			// What waits for the user, from the Workspace aggregator. A failure only costs the counts.
			int approvals = 0, overdue = 0, unread = 0;
			try
			{
				var ws = _services.GetService<IWorkspaceService>();
				if (ws != null && ctx?.EmployeeId is > 0)
				{
					var d = await ws.GetDashboardAsync(ct);
					approvals = d.Approvals.Total ?? d.Approvals.Items.Count;
					overdue = d.MyWork.Items.Count(i => i.Due is DateTime due && due.Date < DateTime.Today && !string.Equals(i.Status, "Done", StringComparison.OrdinalIgnoreCase));
					unread = d.CommunicationInbox.UnreadTotal ?? 0;
				}
			}
			catch (Exception ex) { _log.LogWarning(ex, "Opening screen: the workspace counts are unavailable"); }

			var systems = visible.Select(s => s.Key switch
			{
				"tasks" when overdue > 0 => new LauncherSystem(s, Url.Action(s.Action, s.Controller) ?? "#", overdue, "مهام متأخرة", "overdue tasks"),
				"chat" when unread > 0 => new LauncherSystem(s, Url.Action(s.Action, s.Controller) ?? "#", unread, "رسائل غير مقروءة", "unread messages"),
				_ => new LauncherSystem(s, Url.Action(s.Action, s.Controller) ?? "#", null, null, null),
			}).ToList();

			var signals = new List<LauncherSignal>();
			if (approvals > 0) signals.Add(new LauncherSignal(approvals, "موافقات بانتظارك", "approvals waiting for you", Url.Action("Index", "Approvals") ?? "#", true));
			if (overdue > 0) signals.Add(new LauncherSignal(overdue, "مهام متأخرة", "overdue tasks", Url.Action("Index", "Tasks") ?? "#", true));
			if (unread > 0) signals.Add(new LauncherSignal(unread, "رسائل غير مقروءة", "unread messages", Url.Action("Index", "Chat") ?? "#", false));

			var screens = visible.SelectMany(s => SystemCatalog.ScreensOf(s).Select(i => new LauncherScreen(s.Key, i.LabelAr, i.LabelEn,
				(i.RouteValues is { Count: > 0 } rv ? Url.Action(i.Action, i.Controller, rv.ToDictionary(k => k.Key, k => (object?)k.Value)) : Url.Action(i.Action, i.Controller)) ?? "#"))).ToList();

			string? photo = null, company = null;
			if (ctx?.EmployeeId is int me)
			{
				photo = (await EmployeePhotos.ResolveAsync(_db, ctx, new[] { me }, ct)).GetValueOrDefault(me);
				company = await _db.Companies.AsNoTracking().Where(c => c.CompanyID == ctx.CompanyId).Select(c => ar ? (c.ComoanyNameAr ?? c.CompanyName) : c.CompanyName).FirstOrDefaultAsync(ct);
			}
			// THE NAME FROM THE RECORD, not from the session blob. The blob is a copy taken at sign-in, and a copy
			// written before FullNameEn was carried has none, so an English screen greeted the user in Arabic.
			var names = ctx?.EmployeeId is int self
				? await _db.Employee.AsNoTracking().Where(e => e.ID == self).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct)
				: null;
			string? ArName() => names?.FullName ?? emp?.FullName;
			string? EnName() => !string.IsNullOrWhiteSpace(names?.FullNameEn) ? names!.FullNameEn : emp?.FullNameEn;
			var name = ar ? (ArName() ?? EnName()) : (EnName() ?? ArName());
			return View(new LauncherModel(name ?? "", photo, company, systems, screens, signals, unread));
		}
	}
}
