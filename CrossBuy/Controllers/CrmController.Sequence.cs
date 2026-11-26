using System.Text.Json;
using CrossBuy.BL.CrmIdentity;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// CRM C6 — follow-up sequences (list, editor, a sequence's walks) and the rep's own booking link.
	public partial class CrmController
	{
		private ICrmSequenceService SeqSvc => _seqSvc ??= ActivatorUtilities.CreateInstance<CrmSequenceService>(HttpContext.RequestServices);
		private ICrmSequenceService? _seqSvc;

		[HttpGet] public async Task<IActionResult> Sequences()
		{
			var (ok, _, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View();
		}

		[HttpGet] public async Task<IActionResult> SequencesData(string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await SeqSvc.ListAsync(cid, q, page, pageSize);
			SetPaging(total, page, pageSize);
			return PartialView("_SequenceRows", rows);
		}

		[HttpGet] public async Task<IActionResult> SequenceEditor(int id = 0)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			if (!await _access.CanAsync("manage")) { this.ToastError(CL["You do not have permission to perform this action"].Value); return RedirectToAction(nameof(Sequences)); }
			var file = id > 0 ? await SeqSvc.FileAsync(cid, id) : null;
			if (id > 0 && file == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Sequences)); }
			ViewBag.Templates = (await InboxSvc.ChannelsAsync(cid)).Where(c => c.Channel.Kind == "WhatsApp" && c.Channel.IsActive)
				.SelectMany(c => (c.Templates ?? "").Split(new[] { ',', '،' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct().ToList();
			return View(file);
		}

		sealed record StepIn(int day, string kind, string? subject, string? body, string? template);

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("manage")]
		public async Task<IActionResult> SaveSequence(int id, string name, string? nameEn, string target, bool isActive, bool skipNonWorkDays, string stepsJson)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			List<StepIn>? steps;
			try { steps = JsonSerializer.Deserialize<List<StepIn>>(stepsJson ?? "[]"); }
			catch (JsonException) { return Done(false, "Could not save. Refresh the page and try again."); }
			var (d, e, newId) = await SeqSvc.SaveAsync(cid, _access.CurrentEmployeeId(), id, name, nameEn, target, isActive, skipNonWorkDays,
				(steps ?? new()).Select(s => new CrmSequenceRules.Step(s.day, s.kind, s.subject, s.body, s.template)).ToList());
			return Done(d, e, newId);
		}

		[HttpGet] public async Task<IActionResult> Sequence(int id)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var file = await SeqSvc.FileAsync(cid, id);
			if (file == null) { this.ToastError(CL["Record not found"].Value); return RedirectToAction(nameof(Sequences)); }
			ViewBag.CanEdit = await _access.CanAsync("edit");
			ViewBag.CanManage = await _access.CanAsync("manage");
			return View(file);
		}

		[HttpGet] public async Task<IActionResult> SequenceEnrollmentsData(int id, string? status, string? q, int page = 1, int pageSize = 25)
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			var (rows, total) = await SeqSvc.EnrollmentsAsync(cid, await _access.VisibleOwnerIdsAsync(), id, status, q, page, pageSize);
			SetPaging(total, page, pageSize);
			ViewBag.CanEdit = await _access.CanAsync("edit");
			return PartialView("_EnrollmentRows", rows);
		}

		[HttpGet] public async Task<IActionResult> SequenceCandidates(int id, string? q)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var list = await SeqSvc.CandidatesAsync(cid, await _access.VisibleOwnerIdsAsync(), id, q);
			return Json(list.Select(c => new { id = c.Id, name = c.Name, sub = c.Sub, already = c.Already }));
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> SequenceEnroll(int id, int[] ids)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e, n, skipped) = await SeqSvc.EnrollAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), id, ids ?? Array.Empty<int>());
			return Done(d, e, new { enrolled = n, skipped });
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> SequenceStop(int id)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await SeqSvc.StopAsync(cid, await _access.VisibleOwnerIdsAsync(), _access.CurrentEmployeeId(), id);
			return Done(d, e);
		}

		[HttpGet] public async Task<IActionResult> SequenceEvents(int id)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			return PartialView("_SequenceEvents", await SeqSvc.EventsAsync(cid, id));
		}

		// ---------------- my booking link ----------------
		[HttpGet] public async Task<IActionResult> Booking()
		{
			var (ok, cid, deny) = await ResolveCompanyAsync();
			if (!ok) return deny;
			await SeqSvc.ProcessBookingsAsync(cid);   // anything booked since the worker's last pass lands in the calendar now
			ViewBag.Origin = Request.Scheme + "://" + Request.Host;
			var view = await SeqSvc.MyLinkAsync(cid, _access.CurrentEmployeeId());
			if (view == null) { this.ToastError(CL["Your login is not linked to an employee"].Value); return RedirectToAction(nameof(Index)); }
			return View(view);
		}

		[HttpPost][ValidateAntiForgeryToken][CrmPerm("edit")]
		public async Task<IActionResult> SaveBookingLink(string? title, int duration, int buffer, int daysAhead, int notice, bool isActive)
		{
			var (ok, cid, _) = await ResolveCompanyAsync();
			if (!ok) return Refused();
			var (d, e) = await SeqSvc.SaveLinkAsync(cid, _access.CurrentEmployeeId(), title, duration, buffer, daysAhead, notice, isActive, Request.Scheme + "://" + Request.Host);
			return Done(d, e);
		}
	}
}
