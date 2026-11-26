using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Menu;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL — industry pack H1: the facility; H2: the patient registry (HospitalController.Patients.cs).
	//
	// Screens: the bed board (every floor, every room drawn with its beds), the room-type catalogue,
	// a room's own page (its drawing, its beds and its conversation) and the floor & room designer.
	// Every rule lives in HospitalFacilityService; this controller resolves the caller, asks the
	// module policy at the door ([HospitalPerm]) and renders.
	//
	// A ROOM IS A COMMUNICATION SUBJECT. Its page carries the platform's entity conversation —
	// comments, replies, @mentions, attachments and reactions — through the same panel invoices
	// use. The Communication Platform stores and notifies; nothing here re-implements it.
	// ==========================================================================================
	[SessionValidation]
	[HospitalClinicalGate]   // H156: every endpoint needs clinical-read unless marked [HospitalReceptionAllowed]
	[Route("Hospital")]
	public partial class HospitalController : Controller
	{
		private readonly HospitalFacilityService _facility;
		private readonly IHospitalAccessService _access;
		private readonly IBusinessContextAccessor _contexts;
		private readonly CrossDbContext _db;
		private readonly IStringLocalizer<SharedResources> _sr;
		private readonly IStringLocalizer<HospitalResources> _t;

		private readonly HospitalPatientService _patients;
		private readonly HospitalAdmissionService _admissions;
		private readonly HospitalOutpatientService _outpatient;
		private readonly HospitalConsultationService _consult;
		private readonly HospitalBillingService _billing;
		private readonly HospitalPricingService _pricing;
		private readonly HospitalLabService _lab;
		private readonly HospitalLabAlertService _labAlerts;
		private readonly HospitalRadiologyService _rad;
		private readonly HospitalPharmacyService _pharmacy;

		public HospitalController(HospitalFacilityService facility, HospitalPatientService patients, HospitalAdmissionService admissions,
			HospitalOutpatientService outpatient, HospitalConsultationService consult, HospitalBillingService billing, HospitalPricingService pricing, HospitalLabService lab, HospitalLabAlertService labAlerts, HospitalRadiologyService rad, HospitalPharmacyService pharmacy, IHospitalAccessService access,
			IBusinessContextAccessor contexts, CrossDbContext db, IStringLocalizer<SharedResources> sr, IStringLocalizer<HospitalResources> t)
		{ _facility = facility; _patients = patients; _admissions = admissions; _outpatient = outpatient; _consult = consult; _billing = billing; _pricing = pricing; _lab = lab; _labAlerts = labAlerts; _rad = rad; _pharmacy = pharmacy; _access = access; _contexts = contexts; _db = db; _sr = sr; _t = t; }

		private Task<BusinessContext?> ContextAsync(CancellationToken ct) => _contexts.TryGetCurrentAsync(ct);

		private IActionResult Result(HospitalResult r) => r.Ok
			? Json(new { ok = true, id = r.Id })
			: r.Error == "forbidden" ? StatusCode(StatusCodes.Status403Forbidden, new { ok = false, error = _sr["You do not have permission to perform this action"].Value })
			: r.Error == "not_found" ? NotFound(new { ok = false, code = "not_found" })
			: Json(new { ok = false, error = _t[r.Error!, r.Args ?? Array.Empty<object>()].Value, key = r.Error });   // service refusals translate from the module's own resx; H271: the untranslated key lets a form put the message under the field it is about

		private void Chrome() => ViewBag.SidebarMenu = MainMenu.Hospital();

		// ====================================================================================
		// SCREENS
		// ====================================================================================

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("")]
		[HttpGet("Index")]   // /Hospital/Index answers too: the conventional URL people type
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Index(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Occupants = await _admissions.OccupantsAsync(ctx, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(await _facility.BuildingAsync(ctx, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Room/{id:int}")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Room(int id, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			var room = await _facility.RoomAsync(ctx, id, ct);
			if (room == null) return NotFound();
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Floor = await _db.HospitalFloors.AsNoTracking().Where(f => f.ID == room.FloorId && f.CompanyId == ctx.CompanyId).FirstOrDefaultAsync(ct);
			ViewBag.Ward = room.WardId is int w ? await _db.HospitalWards.AsNoTracking().Where(x => x.ID == w && x.CompanyId == ctx.CompanyId).FirstOrDefaultAsync(ct) : null;
			ViewBag.Occupants = await _admissions.OccupantsAsync(ctx, ct);
			ViewBag.CanAdmit = await _access.CanAsync(ctx, HospitalActions.Admit, null, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(room);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("RoomTypes")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RoomTypes(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return RedirectToAction("Index", "Home");
			Chrome();
			ViewBag.CanManage = await _access.CanAsync(ctx, HospitalActions.Manage, null, ct);
			ViewBag.Building = await _facility.BuildingAsync(ctx, ct);
			ViewBag.Currency = await _facility.CurrencyAsync(ctx, ct);
			return View(await _facility.RoomTypesAsync(ctx, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Designer")]
		[HospitalPerm(HospitalActions.Manage)]
		public IActionResult Designer()
		{
			Chrome();
			return View();
		}

		/// The building as JSON, for the designer and the board's live refresh.
		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Building")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> Building(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			var floors = await _facility.BuildingAsync(ctx, ct);
			var types = await _facility.RoomTypesAsync(ctx, ct);
			var occupants = await _admissions.OccupantsAsync(ctx, ct);
			var currency = await _facility.CurrencyAsync(ctx, ct);
			return Json(new
			{
				ok = true,
				currency = currency == null ? null : new { label = currency.Label(DisplayName.IsArabic), decimals = currency.Decimals },
				types = types.Select(t => new { id = t.ID, code = t.Code, name = t.Name, nameEn = t.NameEn, beds = t.BedCount, rate = t.DailyRate, features = (t.Features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), sameSex = t.SameSexOnly, active = t.IsActive }),
				floors = floors.Select(f => new
				{
					id = f.Id, code = f.Code, name = f.Name, nameEn = f.NameEn, sort = f.SortOrder,
					wards = f.Wards.Select(w => new { id = w.Id, code = w.Code, name = w.Name, nameEn = w.NameEn, sex = w.SexPolicy }),
					rooms = f.Rooms.Select(r => new
					{
						id = r.Id, wardId = r.WardId, number = r.Number, name = r.Name, nameEn = r.NameEn, kind = r.Kind,
						col = r.Column, row = r.Row, colSpan = r.ColumnSpan, rowSpan = r.RowSpan, layout = r.LayoutJson,
						typeId = r.Type?.ID, beds = r.Beds.Select(b => new { id = b.Id, code = b.Code, status = b.Status, patient = HospitalPlanPatient.Of(occupants, b.Id) }),
					}),
				}),
			});
		}

		// ====================================================================================
		// WRITES — all [HospitalPerm(manage)] at the door; the service asks again.
		// ====================================================================================

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("RoomTypes/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveRoomType([FromForm] HospitalRoomTypeInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SaveRoomTypeAsync(ctx, input, ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("RoomTypes/SeedDefaults"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SeedDefaultRoomTypes(CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SeedDefaultRoomTypesAsync(ctx, ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Floors/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveFloor([FromBody] HospitalFloorInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SaveFloorAsync(ctx, input, ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Wards/Save"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveWard([FromBody] HospitalWardInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SaveWardAsync(ctx, input, ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Rooms/Create"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> CreateRoom([FromBody] HospitalRoomInput input, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.CreateRoomAsync(ctx, input, ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Floors/{id:int}/Plan"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveFloorPlan(int id, [FromBody] List<HospitalRoomPlacement> placements, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SaveFloorPlanAsync(ctx, id, placements ?? new(), ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Rooms/{id:int}/Layout"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SaveRoomLayout(int id, [FromBody] List<HospitalLayoutItem> items, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SaveRoomLayoutAsync(ctx, id, items ?? new(), ctx.EmployeeId, ct));
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Beds/{id:int}/Status"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Manage)]
		public async Task<IActionResult> SetBedStatus(int id, [FromForm] string status, CancellationToken ct = default)
		{
			var ctx = await ContextAsync(ct);
			if (ctx == null) return NotFound(new { ok = false, code = "not_found" });
			return Result(await _facility.SetBedStatusAsync(ctx, id, status, ctx.EmployeeId, ct));
		}

		// ====================================================================================
		// ROOM CONVERSATION — the Communication Platform's entity panel, gated by hospital read.
		// ====================================================================================

		private (CrossBuy.BL.Communication.ICommThreadService Threads,
		         CrossBuy.BL.Communication.ICommCommentService Comments,
		         CrossBuy.BL.Communication.ICommEntitySurface Surface,
		         CrossBuy.BL.Communication.ICommReactionService? Reactions)? TryConversation()
		{
			var sp = HttpContext.RequestServices;
			var threads = sp.GetService(typeof(CrossBuy.BL.Communication.ICommThreadService)) as CrossBuy.BL.Communication.ICommThreadService;
			var comments = sp.GetService(typeof(CrossBuy.BL.Communication.ICommCommentService)) as CrossBuy.BL.Communication.ICommCommentService;
			var surface = sp.GetService(typeof(CrossBuy.BL.Communication.ICommEntitySurface)) as CrossBuy.BL.Communication.ICommEntitySurface;
			var reactions = sp.GetService(typeof(CrossBuy.BL.Communication.ICommReactionService)) as CrossBuy.BL.Communication.ICommReactionService;
			// A half-present pair is treated as absent: a conversation that can list but not add is worse
			// than an honest 503.
			return threads is null || comments is null || surface is null ? null : (threads, comments, surface, reactions);
		}

		private IActionResult ConversationUnavailable(string code = "communication_unavailable") =>
			StatusCode(StatusCodes.Status503ServiceUnavailable, new
			{
				ok = false, unavailable = true, code,
				error = _sr["Conversations are unavailable in this environment"].Value,
			});

		/// One gate for read, add and react: a resolved caller, hospital `read`, and the room in the
		/// caller's own company. Absent and other-company answer identically — no existence oracle.
		private async Task<BusinessContext?> RoomGateAsync(int id, CancellationToken ct)
		{
			if (id <= 0) return null;
			var ctx = await ContextAsync(ct);
			if (ctx == null || ctx.CompanyId <= 0) return null;
			if (!await _access.CanAsync(ctx, HospitalActions.Read,
					CrossBuy.Models.Platform.PermissionTarget.ForEntity(EntityRegistry.HospitalRoom, id), ct)) return null;
			bool exists = await _db.HospitalRooms.AsNoTracking().AnyAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId && r.IsActive, ct);
			return exists ? ctx : null;
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpGet("Room/{id:int}/Conversation")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RoomConversation(int id, CancellationToken ct = default)
		{
			var ctx = await RoomGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationListAsync(ctx, EntityRegistry.HospitalRoom, id, ct);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Room/{id:int}/ConversationAdd"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		[RequestSizeLimit(21_000_000)]
		public async Task<IActionResult> RoomConversationAdd(int id, string? body, long? parentCommentId, IFormFile? file, CancellationToken ct = default)
		{
			var ctx = await RoomGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationAddAsync(ctx, EntityRegistry.HospitalRoom, id, body, parentCommentId, file, ct);
		}

		[HospitalReceptionAllowed]   // H156: the reception desk works here (administrative)
		[HttpPost("Room/{id:int}/ConversationReact"), ValidateAntiForgeryToken, HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RoomConversationReact(int id, long commentId, string? key, bool on, CancellationToken ct = default)
		{
			var ctx = await RoomGateAsync(id, ct);
			return ctx == null ? NotFound(new { ok = false, code = "not_found" }) : await ConversationReactAsync(ctx, commentId, key, on, ct);
		}

		// ---- the conversation itself, shared by every hospital record (room, patient) ----------
		// Each caller has already passed its own gate (a resolved caller, hospital access, the record
		// in the caller's company); these only talk to the Communication Platform.

		private async Task<IActionResult> ConversationListAsync(BusinessContext ctx, string entityCode, int id, CancellationToken ct)
		{
			var comm = TryConversation();
			if (comm == null) return ConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(entityCode, id);
			var allowed = await comm.Value.Surface.EvaluateAsync(reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed) return ConversationUnavailable("capability_disabled");

			var thread = await comm.Value.Threads.GetOrCreateAsync(ctx, new CrossBuy.Models.Communication.CommThreadRequest { Entity = reference }, ct);
			var page = await comm.Value.Comments.ListAsync(ctx, thread.Id, null, ct);
			bool isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var avatars = await EmployeePhotos.ResolveAsync(_db, ctx, page.Items.Select(c => c.Author.EmployeeId), ct);
			return Json(new
			{
				ok = true,
				threadId = thread.Id,
				entity = new { code = entityCode, id },
				canReact = comm.Value.Reactions is not null,
				me = await CrossBuy.BL.Communication.CommPanel.MeAsync(_db, ctx, isAr, ct),
				comments = CrossBuy.BL.Communication.CommPanel.Project(page.Items, isAr, avatars),
			});
		}

		private async Task<IActionResult> ConversationAddAsync(BusinessContext ctx, string entityCode, int id, string? body, long? parentCommentId, IFormFile? file, CancellationToken ct, int[]? mentionIds = null)
		{
			if (string.IsNullOrWhiteSpace(body) && (file is null || file.Length == 0))
				return Json(new { ok = false, error = _sr["Write a comment"].Value });
			var comm = TryConversation();
			if (comm == null) return ConversationUnavailable();

			var reference = new CrossBuy.Models.Communication.CommEntityRef(entityCode, id);
			var allowed = await comm.Value.Surface.EvaluateAsync(reference, CrossBuy.BL.Communication.CommCapabilities.Comments);
			if (!allowed.Allowed) return ConversationUnavailable("capability_disabled");

			// Nothing reaches disk before the gate: a refused caller never leaves an orphan file.
			CrossBuy.Models.Communication.CommAttachmentRequest? attachment = null;
			if (file is { Length: > 0 })
			{
				var env = HttpContext.RequestServices.GetService(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment)) as Microsoft.AspNetCore.Hosting.IWebHostEnvironment;
				if (env is null) return Json(new { ok = false, error = _sr["The file could not be attached"].Value });
				var (staged, refusal) = await CrossBuy.BL.Communication.CommPanel.StageAsync(file, env.WebRootPath, ct);
				if (staged is null) return Json(new { ok = false, code = refusal, error = _sr["The file could not be attached"].Value });
				attachment = staged;
			}

			// The platform owns body policy, mention parsing, the audit row and the notification fan-out.
			var added = await comm.Value.Comments.AddAsync(ctx, new CrossBuy.Models.Communication.CommCommentRequest
			{
				Entity = reference,
				Body = body ?? "",
				ParentCommentId = parentCommentId is > 0 ? parentCommentId : null,
				Attachments = attachment is null ? null : new[] { attachment },
				// the @-picker's ids (2026-11-24): a request to mention, never a grant - the platform drops whoever may not read the record
				Mentions = (mentionIds ?? Array.Empty<int>()).Where(m => m > 0).Distinct()
					.Select(m => new CrossBuy.Models.Communication.CommMentionRequest { TargetKind = CrossBuy.Models.Communication.CommMentionTargetKind.Employee, TargetId = m }).ToList(),
			}, ct);
			return Json(new { ok = true, id = added.CommentId, threadId = added.ThreadId });
		}

		private async Task<IActionResult> ConversationReactAsync(BusinessContext ctx, long commentId, string? key, bool on, CancellationToken ct)
		{
			var comm = TryConversation();
			if (comm?.Reactions is null) return ConversationUnavailable();
			if (commentId <= 0 || string.IsNullOrWhiteSpace(key))
				return Json(new { ok = false, error = _sr["The reaction could not be saved"].Value });
			try
			{
				var summary = on
					? await comm.Value.Reactions.AddAsync(ctx, commentId, key, ct)
					: await comm.Value.Reactions.RemoveAsync(ctx, commentId, key, ct);
				return Json(new { ok = true, commentId, reactions = summary.Where(r => r.Count > 0).Select(r => new { key = r.ReactionKey, count = r.Count, mine = r.Mine }) });
			}
			catch (CrossBuy.Models.Communication.CommAccessDeniedException) { return NotFound(new { ok = false, code = "not_found" }); }
			catch (CrossBuy.Models.Communication.CommValidationException) { return Json(new { ok = false, error = _sr["The reaction could not be saved"].Value }); }
		}
	}
}
