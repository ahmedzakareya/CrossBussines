using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — H8: the laboratory. The worklist (orders born from signed visits), the specimen steps,
	// result entry, verification (the second step), critical values, and the test/analyte setup.
	// Every rule is HospitalLabService's.
	// ==========================================================================================
	public partial class HospitalController
	{
		public sealed class HospitalLabResultsInput { public Dictionary<string, string?> Values { get; set; } = new(); public string? Comment { get; set; } }
		public sealed class HospitalLabNoteInput { public string? Note { get; set; } }
		public sealed class HospitalLabPriorityInput { public string Priority { get; set; } = ""; }

		[HttpGet("Lab")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Lab(string? status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			await _lab.SyncOrdersAsync(ctx, ct);
			Chrome();
			ViewBag.Status = HospitalLabStatus.All.Contains(status ?? "") ? status : null;
			ViewBag.Stats = await _lab.StatsAsync(ctx, DateTime.Today.ToUniversalTime(), ct);
			ViewBag.CanLab = await _access.CanAsync(ctx, HospitalActions.Lab, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Critical = (await _lab.WorklistAsync(ctx, null, null, ct)).Where(r => r.CriticalOpen).ToList();
			return View(await _lab.WorklistAsync(ctx, ViewBag.Status as string, null, ct));
		}

		[HttpGet("Lab/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabOrder(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var v = await _lab.OrderAsync(ctx, id, ct);
			if (v == null) return NotFound();
			Chrome();
			ViewBag.CanLab = await _access.CanAsync(ctx, HospitalActions.Lab, null, ct);
			ViewBag.CanVerify = await _access.CanAsync(ctx, HospitalActions.LabVerify, null, ct);
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Me = ctx.EmployeeId;
			// H27: whether the patient may see it on the portal, and who may let them
			ViewBag.CanConsult = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
			ViewBag.PortalRelease = await PortalRecordsSvc.ReleaseOfAsync(ctx, HospitalPortalResultSource.Lab, id, ct);
			return View(v);
		}

		public sealed class HospitalCollectScanInput { public string? Wristband { get; set; } public string? Tube { get; set; } public string? ScanOverride { get; set; } }

		[HttpPost("Lab/{id:int}/Collect"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabCollect(int id, [FromBody] HospitalCollectScanInput? input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			// H206: the bedside scan (wristband + tube label) or the reason it cannot be made
			return Result(await _lab.CollectAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct, input?.Wristband, input?.Tube, input?.ScanOverride));
		}

		[HttpPost("Lab/{id:int}/Receive"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabReceive(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.ReceiveAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Lab/{id:int}/Priority"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabPriority(int id, [FromBody] HospitalLabPriorityInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.SetPriorityAsync(ctx, id, input?.Priority ?? "", ct));
		}

		[HttpPost("Lab/{id:int}/Results"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabResults(int id, [FromBody] HospitalLabResultsInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var values = new Dictionary<int, string?>();
			foreach (var kv in input?.Values ?? new()) if (int.TryParse(kv.Key, out var aid)) values[aid] = kv.Value;
			return Result(await _lab.SaveResultsAsync(ctx, id, values, input?.Comment, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpPost("Lab/{id:int}/Verify"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.LabVerify)]
		public async Task<IActionResult> LabVerify(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await _lab.VerifyAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct);
			if (r.Ok) await _labAlerts.NotifyAbnormalAsync(ctx, HospitalAlertSource.Lab, id, ctx.EmployeeId, DateTime.UtcNow, ct);   // an urgent order out of range tells its doctor
			return Result(r);
		}

		[HttpPost("Lab/{id:int}/Critical"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabCritical(int id, [FromBody] HospitalLabNoteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.AcknowledgeCriticalAsync(ctx, id, input?.Note, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		// ---------------- critical values: the lab tells a doctor, the doctor acknowledges anywhere ----------------

		public sealed class HospitalLabSendInput { public int PractitionerId { get; set; } public bool ViaSms { get; set; } public string? PhoneNote { get; set; } }
		public sealed class HospitalLabReadBackInput { public string? Value { get; set; } }

		[HttpGet("Lab/{id:int}/Critical")]
		[HospitalPerm(HospitalActions.Read)]
		public Task<IActionResult> LabCritical(int id, CancellationToken ct = default) => CriticalPage(HospitalAlertSource.Lab, id, ct);

		/// The critical-value page of a lab order or an imaging order: whom to tell, how, and following it up.
		private async Task<IActionResult> CriticalPage(string source, int id, CancellationToken ct)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var s = await _labAlerts.SubjectAsync(ctx, source, id, ct);
			if (s == null) return NotFound();
			bool rad = source == HospitalAlertSource.Radiology;
			if (!s.IsCritical) return RedirectToAction(rad ? "RadOrder" : "LabOrder", new { id });
			await _labAlerts.MaintainAsync(ctx, DateTime.UtcNow, DateTime.Now, ct);
			Chrome();
			var recipients = await _labAlerts.RecipientsAsync(ctx, source, id, DateTime.Now, ct);
			ViewBag.Recipients = recipients;
			ViewBag.Photos = await EmployeePhotos.ResolveAsync(_db, ctx, recipients.Where(r => r.Practitioner.EmployeeId != null).Select(r => r.Practitioner.EmployeeId!.Value), ct);
			ViewBag.Trail = await _labAlerts.TrailAsync(ctx, source, id, DateTime.Now, ct);
			var preview = await _labAlerts.PreviewAsync(ctx, source, id, DateTime.UtcNow, ct);
			ViewBag.PreviewView = preview;                                   // a record, read by the view
			ViewBag.Preview = preview == null ? null : AlertJson(preview);   // the JSON the alert partial renders
			ViewBag.CanLab = await _access.CanAsync(ctx, rad ? HospitalActions.Rad : HospitalActions.Lab, null, ct);
			ViewBag.Departments = (await _outpatient.DepartmentsAsync(ctx, ct)).ToDictionary(d => d.ID, d => DisplayName.Of(d.Name, d.NameEn));
			return View("LabCritical", s);
		}

		[HttpPost("Lab/{id:int}/Critical/Send"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Lab)]
		public async Task<IActionResult> LabCriticalSend(int id, [FromBody] HospitalLabSendInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _labAlerts.SendAsync(ctx, HospitalAlertSource.Lab, id, input?.PractitionerId ?? 0, input?.ViaSms == true, input?.PhoneNote, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		/// The signed-in doctor's open alerts — polled by every layout of the system (_LabCriticalAlerts).
		[HttpGet("Alerts/Mine")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabAlertsMine(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return Json(new { ok = true, alerts = Array.Empty<object>() });
			var list = await _labAlerts.MineAsync(ctx, DateTime.UtcNow, DateTime.Now, ct);
			return Json(new { ok = true, alerts = list.Select(AlertJson) });
		}

		/// The shape the alert partial renders — the same for a real alert and the lab's preview.
		private object AlertJson(HospitalLabAlertView a) => new
		{
			id = a.Id, level = a.Level, reason = a.Reason, orderId = a.OrderId, accession = a.AccessionNo, test = DisplayName.Of(a.Test, a.TestEn), priority = a.Priority,
			patientId = a.PatientId, patient = DisplayName.Of(a.PatientName, a.PatientNameEn), initials = HospitalUi.Initials(a.PatientNameEn), mrn = a.Mrn, photo = a.Photo, room = a.Room, bed = a.Bed,
			value = a.Value, sentAt = a.SentAt.ToString("o"), sentBy = a.SentBy, escalatedFrom = a.EscalatedFrom, reminded = a.RemindedAt != null,
			lines = a.Lines.Select(l => new { name = DisplayName.Of(l.Name, l.NameEn), unit = l.Unit, value = l.Shown, flag = l.Flag, critical = l.Critical, range = l.Range, previous = l.Previous }),
			fileUrl = Url.Action("Patient", new { id = a.PatientId }), orderUrl = Url.Action(a.Source == HospitalAlertSource.Radiology ? "RadOrder" : "LabOrder", new { id = a.OrderId }),
			source = a.Source, impression = a.Impression, impressionCategory = a.ImpressionCategory,
		};

		[HttpPost("Alerts/{id:int}/Seen"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabAlertSeen(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _labAlerts.SeenAsync(ctx, id, DateTime.UtcNow, ct));
		}

		[HttpPost("Alerts/{id:int}/Acknowledge"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabAlertAcknowledge(int id, [FromBody] HospitalLabReadBackInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _labAlerts.AcknowledgeAsync(ctx, id, input?.Value, DateTime.UtcNow, ct));
		}

		// ---------------- setup ----------------

		[HttpGet("LabTests")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> LabTests(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			return View(await _lab.SetupAsync(ctx, ct));
		}

		[HttpPost("LabTests/{serviceId:int}"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveLabTest(int serviceId, [FromBody] HospitalLabTestInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.SaveTestAsync(ctx, serviceId, input ?? new(), ct));
		}

		[HttpPost("LabTests/Analyte"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveLabAnalyte([FromBody] HospitalLabAnalyteInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.SaveAnalyteAsync(ctx, input ?? new(), ct));
		}

		[HttpPost("LabTests/SeedStandard"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedLabStandard(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _lab.SeedStandardAsync(ctx, ct));
		}
	}
}
