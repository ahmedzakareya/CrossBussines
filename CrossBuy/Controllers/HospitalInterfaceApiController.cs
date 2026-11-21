using System.Text;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// HOSPITAL H215 — the machine side of the interface channel (see HospitalInterfaceService). No session and no
	// cookie: a machine presents its key in the X-Interface-Key header; the key opens one interface of one kind, and
	// the work is done as the employee that interface acts for, with that person's rights at that moment.
	[Route("api/hospital/interfaces")]
	[IgnoreAntiforgeryToken]
	public class HospitalInterfaceApiController : Controller
	{
		private readonly CrossDbContext _db;
		private readonly IBusinessContextFactory _contexts;
		public HospitalInterfaceApiController(CrossDbContext db, IBusinessContextFactory contexts) { _db = db; _contexts = contexts; }

		private HospitalInterfaceService Interfaces => ActivatorUtilities.CreateInstance<HospitalInterfaceService>(HttpContext.RequestServices);
		private const string KeyHeader = "X-Interface-Key";

		private async Task<(HospitalInterface I, BusinessContext Ctx)?> OpenAsync(string code, string kind, CancellationToken ct)
		{
			var i = await Interfaces.ByKeyAsync(code, Request.Headers[KeyHeader].FirstOrDefault(), kind, DateTime.UtcNow, ct);
			if (i == null) return null;
			var ctx = await _contexts.ForEmployeeAsync(i.ActsAsEmployeeId, null, ct);
			return ctx == null || ctx.CompanyId != i.CompanyId ? null : (i, ctx);
		}

		[AllowAnonymous]
		[AnonymousByDesign(
			"An analyser or interface engine has no login: it presents its interface key (X-Interface-Key; random, stored only as a " +
			"SHA-256 hash, shown once). The key opens one LabResults interface of one company; the message is filed through the " +
			"laboratory's own receive path AS the employee the interface acts for, with that person's rights checked on every message. " +
			"A wrong, inactive or other-kind key gets a bare 401 and nothing is read.")]
		[HttpPost("{code}/hl7")]
		[RequestSizeLimit(1_000_000)]
		public async Task<IActionResult> Hl7(string code, CancellationToken ct = default)
		{
			var open = await OpenAsync(code, HospitalInterfaceKind.LabResults, ct);
			if (open == null) return Unauthorized();
			string raw;
			using (var reader = new StreamReader(Request.Body, Encoding.UTF8)) raw = await reader.ReadToEndAsync(ct);
			raw = raw.Trim('\v', '\x1c', '\r', '\n', ' ');   // an MLLP frame passed on as it came: its start and end bytes dropped
			var svc = ActivatorUtilities.CreateInstance<HospitalLabDeviceService>(HttpContext.RequestServices);
			var r = await svc.ReceiveAsync(open.Value.Ctx, raw, open.Value.I.ActsAsEmployeeId, DateTime.UtcNow, ct);
			string control = HospitalInterfaceRules.ControlId(raw) ?? "";
			string ack;
			if (!r.Ok) ack = HospitalInterfaceRules.Ack(control, "AR", r.Error, DateTime.UtcNow);
			else
			{
				var m = await _db.Set<HospitalDeviceMessage>().AsNoTracking().Where(x => x.ID == r.Id && x.CompanyId == open.Value.I.CompanyId).Select(x => new { x.Status, x.Detail }).FirstOrDefaultAsync(ct);
				ack = HospitalInterfaceRules.Ack(control, HospitalInterfaceRules.AckCode(m?.Status ?? ""), m?.Detail, DateTime.UtcNow);
			}
			return Content(ack, "application/hl7-v2", Encoding.UTF8);
		}

		[AllowAnonymous]
		[AnonymousByDesign(
			"An ECG machine or its management system has no login: it presents its interface key (X-Interface-Key; hashed, shown once). " +
			"The key opens one CardioResults interface of one company; the measurements are filed on the requested ECG study the message " +
			"names, as the device reading, AS the employee the interface acts for (consult rights checked on every message); the cardiologist " +
			"still confirms and signs. A wrong, inactive or other-kind key gets a bare 401 and nothing is read.")]
		[HttpPost("{code}/ecg")]
		[RequestSizeLimit(1_000_000)]
		public async Task<IActionResult> Ecg(string code, CancellationToken ct = default)
		{
			var open = await OpenAsync(code, HospitalInterfaceKind.CardioResults, ct);
			if (open == null) return Unauthorized();
			string raw;
			using (var reader = new StreamReader(Request.Body, Encoding.UTF8)) raw = await reader.ReadToEndAsync(ct);
			raw = raw.Trim('\v', '\x1c', '\r', '\n', ' ');
			var svc = ActivatorUtilities.CreateInstance<HospitalCardiologyService>(HttpContext.RequestServices);
			var r = await svc.ImportAsync(open.Value.Ctx, raw, DateTime.UtcNow, ct);
			return Content(HospitalInterfaceRules.Ack(HospitalInterfaceRules.ControlId(raw) ?? "", r.Ack, r.Detail, DateTime.UtcNow), "application/hl7-v2", Encoding.UTF8);
		}

		[AllowAnonymous]
		[AnonymousByDesign(
			"A modality worklist broker has no login: it presents its interface key (X-Interface-Key; hashed, shown once). The key opens " +
			"one ImagingWorklist interface of one company, and the scheduled imaging orders are read AS the employee the interface acts " +
			"for (radiology rights checked each time). It reads; it writes nothing but the interface's last-used time.")]
		[HttpGet("{code}/worklist")]
		public async Task<IActionResult> Worklist(string code, string? modality, string? format, CancellationToken ct = default)
		{
			var open = await OpenAsync(code, HospitalInterfaceKind.ImagingWorklist, ct);
			if (open == null) return Unauthorized();
			var items = await Interfaces.WorklistAsync(open.Value.Ctx, modality, DateTime.UtcNow, ct);
			if (string.Equals(format, "hl7", StringComparison.OrdinalIgnoreCase))
				return Content(string.Concat(items.Select(x => "\v" + x.Hl7 + "\x1c\r")), "application/hl7-v2", Encoding.UTF8);   // MLLP-framed batch
			return Json(items.Select(x => new { accession = x.Accession, mrn = x.Mrn, patient = x.Patient, modality = x.Modality, exam = x.Exam, orderedAt = x.OrderedAt, priority = x.Priority, hl7 = x.Hl7 }));
		}
	}
}
