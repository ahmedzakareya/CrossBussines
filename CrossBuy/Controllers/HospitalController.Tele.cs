using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Views.Hospital
{
	/// H214: what the shared video room needs — where to send and read the set-up messages, which side this is, the
	/// STUN servers, and the anti-forgery token.
	public sealed class HospitalTeleRoomModel
	{
		public string SignalUrl { get; init; } = "";
		public string PollUrl { get; init; } = "";
		public string? EndUrl { get; init; }
		public string? DoneUrl { get; init; }
		public string Side { get; init; } = HospitalTeleSide.Doctor;
		public IReadOnlyList<string> Ice { get; init; } = Array.Empty<string>();
		public string Token { get; init; } = "";

		/// The STUN servers from Hospital:TeleIceServers (comma-separated), else a public one: a call across two networks
		/// needs one to find its way; within one network none is used.
		public static IReadOnlyList<string> IceFrom(Microsoft.Extensions.Configuration.IConfiguration? config) =>
			(config?["Hospital:TeleIceServers"] ?? "stun:stun.l.google.com:19302").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}
}

namespace CrossBuy.Controllers
{
	// HOSPITAL H214 — video visits, the doctor's side: make an appointment a video visit (`book` or `consult`), the room
	// (`consult`), its set-up messages, and the end.
	public partial class HospitalController
	{
		public sealed class HospitalTeleSignalInput { public string? Kind { get; set; } public string? Payload { get; set; } }

		private HospitalTeleVisitService? _teleSvc;
		private HospitalTeleVisitService TeleSvc => _teleSvc ??= ActivatorUtilities.CreateInstance<HospitalTeleVisitService>(HttpContext.RequestServices);

		[HttpPost("Appointments/{id:int}/Video"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> AppointmentVideo(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			if (!await _access.CanAsync(ctx, HospitalActions.Book, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return Result(HospitalResult.Fail("forbidden"));
			return Result(await TeleSvc.CreateAsync(ctx, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}

		[HttpGet("Tele/{id:int}")]
		[HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> Tele(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			var room = await TeleSvc.RoomAsync(ctx.CompanyId, id, ct);
			if (room == null) return NotFound();
			var join = await TeleSvc.JoinAsync(ctx.CompanyId, id, HospitalTeleSide.Doctor, DateTime.UtcNow, DateTime.Now, ct);
			ViewBag.Refusal = join.Ok ? null : join.Error;
			ViewBag.Ice = CrossBuy.Views.Hospital.HospitalTeleRoomModel.IceFrom(HttpContext.RequestServices.GetService<Microsoft.Extensions.Configuration.IConfiguration>());
			return View(room);
		}

		[HttpPost("Tele/{id:int}/Signal"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> TeleSignal(int id, [FromBody] HospitalTeleSignalInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null || input == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await TeleSvc.SignalAsync(ctx.CompanyId, id, HospitalTeleSide.Doctor, input.Kind, input.Payload, DateTime.UtcNow, ct));
		}

		[HttpGet("Tele/{id:int}/Poll"), HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> TelePoll(int id, long after = 0, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var room = await TeleSvc.RoomAsync(ctx.CompanyId, id, ct);
			if (room == null) return NotFound(new { ok = false, code = "not_found" });
			var s = await TeleSvc.PollAsync(ctx.CompanyId, id, HospitalTeleSide.Doctor, after, ct);
			return Json(new { ok = true, ended = room.Visit.Status == HospitalTeleStatus.Ended, signals = s.Select(x => new { id = x.ID, kind = x.Kind, payload = x.Payload }) });
		}

		[HttpPost("Tele/{id:int}/End"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Consult)]
		public async Task<IActionResult> TeleEnd(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await TeleSvc.EndAsync(ctx.CompanyId, id, ctx.EmployeeId, DateTime.UtcNow, ct));
		}
	}
}
