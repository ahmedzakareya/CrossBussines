using System.Security.Claims;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HOSPITAL H25 — the PATIENT PORTAL (Metronic demo30, as the client portal).
	//
	//   · Signed in on the client portal's own scheme (CrossBuy.Portal, [PortalAuthorize]); what makes the person a
	//     patient is their HospitalPortalAccount, found from the principal's user id ALONE. The company comes from
	//     that account, never from the request — then IBusinessContextFactory.ForExternalPortal pins the company
	//     scope, as the client portal does.
	//   · No [SessionValidation]: the path is on PortalAuthenticationDefaults.SurfacePathPrefixes, the lane the
	//     employee-session gate leaves to the portal scheme.
	//   · Every booking decision is HospitalPortalBookingService's (H24) through HospitalPortalPolicy (H23).
	// ==========================================================================================
	[PortalAuthorize]
	[Route("PatientPortal")]
	public partial class PatientPortalController : Controller
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IBusinessContextFactory _contexts;
		private readonly IStringLocalizer<HospitalResources> _t;
		private HospitalPortalIdentityService? _identity;
		private HospitalPortalBookingService? _booking;
		private HospitalPortalRulesService? _rules;

		public PatientPortalController(CrossDbContext db, IHospitalAccessService access, IBusinessContextFactory contexts, IStringLocalizer<HospitalResources> t)
		{ _db = db; _access = access; _contexts = contexts; _t = t; }

		private HospitalPortalIdentityService Identity => _identity ??= ActivatorUtilities.CreateInstance<HospitalPortalIdentityService>(HttpContext.RequestServices);
		private HospitalPortalBookingService Booking => _booking ??= new HospitalPortalBookingService(_db, _access);
		private HospitalPortalRulesService Rules => _rules ??= new HospitalPortalRulesService(_db, _access);
		private HospitalDentalService? _dental;
		private HospitalDentalService Dental => _dental ??= ActivatorUtilities.CreateInstance<HospitalDentalService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<HospitalInsuranceService>(HttpContext.RequestServices));
		private HospitalRadImageService? _radImages;
		private HospitalRadImageService RadImages => _radImages ??= ActivatorUtilities.CreateInstance<HospitalRadImageService>(HttpContext.RequestServices);
		private HospitalPortalRecordsService? _records;
		private HospitalPortalRecordsService Records => _records ??= ActivatorUtilities.CreateInstance<HospitalPortalRecordsService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<HospitalBillingService>(HttpContext.RequestServices));

		/// The clinic clock (H4 books in server-local wall time).
		private static DateTime NowLocal => DateTime.Now;

		/// H46: the claim a sign-in carries of the password it was made with (its change time), so a password change
		/// ends every session made before it — renewal keeps the claim, so an old session stays refused.
		private const string PasswordClaim = "hp:pwd";
		private static string PasswordStamp(HospitalPortalAccount a) => (a.PasswordChangedAt?.Ticks ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);

		/// The signed-in patient: their account and the company context it pins. Null = not a patient (refused).
		private async Task<(HospitalPortalAccount Account, BusinessContext Ctx)?> MeAsync(CancellationToken ct)
		{
			var userId = User.FindFirstValue(PortalAuthenticationDefaults.RequiredClaim);
			var acc = await Identity.AccountOfAsync(userId, ct);
			if (acc == null) return null;
			if ((User.FindFirstValue(PasswordClaim) ?? "0") != PasswordStamp(acc))
			{
				await HttpContext.SignOutAsync(PortalAuthenticationDefaults.AuthenticationScheme);
				return null;
			}
			return (acc, _contexts.ForExternalPortal(acc.CompanyId, userId!));
		}

		private IActionResult Json(HospitalResult r) =>
			r.Ok ? base.Json(new { ok = true, id = r.Id })
			: r.Error == "not_found" ? NotFound(new { ok = false, code = "not_found" })
			: base.Json(new { ok = false, error = _t[r.Error!, r.Args ?? Array.Empty<object>()].Value });

		private IActionResult NotAPatient() => RedirectToAction(nameof(Login));

		// ====================================================================================
		// THE DOOR
		// ====================================================================================

		[HttpGet("Login")]
		[AllowAnonymous]
		public IActionResult Login(int? c = null)
		{
			ViewBag.Company = c;
			return View();
		}

		[HttpPost("Login")]
		[AllowAnonymous]
		[ValidateAntiForgeryToken]
		[AnonymousByDesign("This endpoint ESTABLISHES the patient portal credential, so it cannot require one. The credential check stands in " +
			"for authorization and is stricter than one: an active end user, the right password AND a patient portal account must all hold. " +
			"It is anti-forgery protected, takes no company, patient or account id from the request, and every refusal is the same message.")]
		public async Task<IActionResult> Login(string? userName, string? password, bool rememberMe = false, CancellationToken ct = default)
		{
			var hit = await Identity.CheckAsync(userName, password, ct);
			if (hit == null) return base.Json(new { success = false, message = _t["The user name or the password is not right"].Value });
			var identity = new ClaimsIdentity(new[] { new Claim(PortalAuthenticationDefaults.RequiredClaim, hit.Value.User.Id), new Claim(PasswordClaim, PasswordStamp(hit.Value.Account)) }, PortalAuthenticationDefaults.AuthenticationScheme);
			await HttpContext.SignInAsync(PortalAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
				new AuthenticationProperties { IsPersistent = rememberMe, IssuedUtc = DateTimeOffset.UtcNow });
			return base.Json(new { success = true, redirectUrl = Url.Action(nameof(Index)) });
		}

		// ------------------------------------------------------------------------------------
		// H46: forgot password — on the sign-in page, by email only, the email stored nowhere
		// ------------------------------------------------------------------------------------
		private HospitalPortalPasswordService? _password;
		private HospitalPortalPasswordService Password => _password ??= ActivatorUtilities.CreateInstance<HospitalPortalPasswordService>(HttpContext.RequestServices,
			ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));

		/// The reset email and the notice of a change (HospitalPortalMails: the desk sends the same words).
		private (string Subject, string Html) ResetMail(string token, string userName) =>
			HospitalPortalMails.Reset(_t, Url.Action(nameof(Reset), "PatientPortal", new { t = token }, Request.Scheme)!, userName);
		private (string Subject, string Html) ChangedMail(string userName) => HospitalPortalMails.Changed(_t, userName);

		[HttpGet("Forgot")]
		[AllowAnonymous]
		public IActionResult Forgot()
		{
			ViewBag.Mode = "forgot";
			return View(nameof(Login));
		}

		[HttpPost("Forgot")]
		[AllowAnonymous]
		[ValidateAntiForgeryToken]
		[AnonymousByDesign("This endpoint lets a patient who cannot sign in ask for a reset link, so it cannot require a sign-in. It answers " +
			"the same whether or not an account matches, sends the link only to the email already on the account, keeps only the link's hash, " +
			"limits each account to three links an hour, and is anti-forgery protected.")]
		public async Task<IActionResult> Forgot(string? who, CancellationToken ct = default)
		{
			await Password.RequestAsync(who, ResetMail, DateTime.UtcNow, ct);
			return base.Json(new { success = true });
		}

		[HttpGet("Reset")]
		[AllowAnonymous]
		public async Task<IActionResult> Reset(string? t, CancellationToken ct = default)
		{
			ViewBag.Mode = await Password.LinkAsync(t, DateTime.UtcNow, ct) == null ? "expired" : "reset";
			ViewBag.Token = t;
			return View(nameof(Login));
		}

		[HttpPost("Reset")]
		[AllowAnonymous]
		[ValidateAntiForgeryToken]
		[AnonymousByDesign("This endpoint sets a new password through an emailed link, so it cannot require a sign-in. The link's token is the " +
			"credential: only its hash is stored, it works once, for 30 minutes, and Identity's own reset and password rules apply.")]
		public async Task<IActionResult> Reset(string? t, string? password, string? again, CancellationToken ct = default)
		{
			var r = await Password.ResetAsync(t, password, again, ChangedMail, DateTime.UtcNow, ct);
			if (r.Ok) return base.Json(new { success = true });
			if (r.Error == "expired") return base.Json(new { success = false, expired = true });
			return base.Json(new { success = false, message = _t[r.Error!].Value });
		}

		// ------------------------------------------------------------------------------------
		// H48: the earlier-slot offer — from its emailed link, on the sign-in page (owner ruling), like the reset link
		// ------------------------------------------------------------------------------------
		private HospitalPortalWaitlistService? _waitlist;
		private HospitalPortalWaitlistService Waitlist => _waitlist ??= new HospitalPortalWaitlistService(_db, ActivatorUtilities.CreateInstance<SmtpHospitalPortalMailer>(HttpContext.RequestServices));
		private (string Subject, string Html) OfferMail(string token, HospitalWaitlistOfferMail m) =>
			HospitalPortalMails.Offer(_t, Url.Action(nameof(Offer), "PatientPortal", new { t = token }, Request.Scheme)!, m);

		[HttpGet("Offer")]
		[AllowAnonymous]
		public async Task<IActionResult> Offer(string? t, CancellationToken ct = default)
		{
			var view = await Waitlist.LinkAsync(t, DateTime.UtcNow, ct);
			ViewBag.Mode = view == null ? "offer-gone" : "offer";
			ViewBag.Offer = view;
			ViewBag.Token = t;
			return View(nameof(Login));
		}

		[HttpPost("Offer")]
		[AllowAnonymous]
		[ValidateAntiForgeryToken]
		[AnonymousByDesign("This endpoint answers an earlier-slot offer from its emailed link, so it cannot require a sign-in. The link's token is " +
			"the credential: only its hash is stored, it answers one offer of one patient, only while that offer is open (held minutes), and it can " +
			"only move that patient's own booking to the time offered — never choose another time, patient or doctor. Anti-forgery protected.")]
		public async Task<IActionResult> OfferPost(string? t, string? answer, CancellationToken ct = default)
		{
			var r = answer == "take" ? await Waitlist.TakeAsync(t, OfferMail, NowLocal, DateTime.UtcNow, ct)
				: answer == "decline" ? await Waitlist.DeclineAsync(t, OfferMail, NowLocal, DateTime.UtcNow, ct)
				: HospitalResult.Fail("expired");
			if (r.Ok) return base.Json(new { success = true, then = answer == "take" ? "ppTaken" : "ppDeclined" });
			if (r.Error == "expired") return base.Json(new { success = false, expired = true });
			return base.Json(new { success = false, message = _t[r.Error!].Value });
		}

		[HttpPost("Logout")]
		[ValidateAntiForgeryToken]
		public async Task<IActionResult> Logout()
		{
			await HttpContext.SignOutAsync(PortalAuthenticationDefaults.AuthenticationScheme);
			return RedirectToAction(nameof(Login));
		}

		[HttpGet("Register")]
		[AllowAnonymous]
		public async Task<IActionResult> Register(int c, CancellationToken ct = default)
		{
			ViewBag.Company = c;
			ViewBag.Open = await Identity.SelfRegistrationOpenAsync(c, ct);
			return View();
		}

		[HttpPost("Register")]
		[AllowAnonymous]
		[ValidateAntiForgeryToken]
		[AnonymousByDesign("Self-registration creates the credential, so it cannot require one. The company it names is only the hospital the " +
			"person asks to join, checked to be a hospital whose portal rules allow self-registration; the account it makes is UNVERIFIED and can " +
			"do nothing but send requests until the front desk matches it to a patient file. Anti-forgery protected.")]
		public async Task<IActionResult> Register(int c, [FromBody] HospitalPortalRegisterInput input, CancellationToken ct = default)
		{
			if (input == null) return NotFound(new { ok = false, code = "not_found" });
			var r = await Identity.RegisterAsync(c, input, DateTime.UtcNow, ct);
			return Json(r);
		}

		// ====================================================================================
		// THE PAGES
		// ====================================================================================

		private async Task<bool> PrepareAsync((HospitalPortalAccount Account, BusinessContext Ctx) me, string page, CancellationToken ct)
		{
			ViewBag.Account = me.Account;
			ViewBag.Page = page;
			ViewBag.People = await Booking.PeopleAsync(me.Ctx, me.Account.ID, ct);
			ViewBag.Settings = await Rules.SettingsAsync(me.Ctx, ct);
			ViewBag.Photo = me.Account.PatientId is int pid ? await _db.HospitalPatients.Where(p => p.ID == pid && p.CompanyId == me.Ctx.CompanyId).Select(p => p.PhotoPath).FirstOrDefaultAsync(ct) : null;
			return true;
		}

		[HttpGet("")]
		[HttpGet("Index")]
		public async Task<IActionResult> Index(CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "home", ct);
			ViewBag.Requests = await Booking.MyRequestsAsync(me.Ctx, me.Account.ID, ct);
			return View(await Booking.VisitsAsync(me.Ctx, me.Account.ID, NowLocal, ct));
		}

		[HttpGet("Appointments")]
		public async Task<IActionResult> Appointments(CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "appointments", ct);
			await Waitlist.RunAsync(me.Ctx.CompanyId, OfferMail, NowLocal, DateTime.UtcNow, null, ct);   // H48: no worker, so the pages move the line on
			ViewBag.Requests = await Booking.MyRequestsAsync(me.Ctx, me.Account.ID, ct);
			var visits = await Booking.VisitsAsync(me.Ctx, me.Account.ID, NowLocal, ct);
			ViewBag.Tele = await Tele.ForAppointmentsAsync(me.Ctx.CompanyId, visits.Select(v => v.AppointmentId).ToList(), ct);   // H214
			return View(visits);
		}

		// ---- H214: the patient's side of a video visit -----------------------------------------------------------
		private HospitalTeleVisitService? _tele;
		private HospitalTeleVisitService Tele => _tele ??= new HospitalTeleVisitService(_db);
		public sealed class PortalTeleSignalInput { public string? Kind { get; set; } public string? Payload { get; set; } }

		/// The video visit, if it is one of this account's people's (else null: never another family's call).
		private async Task<HospitalTeleRoom?> MyRoomAsync((HospitalPortalAccount Account, BusinessContext Ctx) me, int visitId, CancellationToken ct)
		{
			var room = await Tele.RoomAsync(me.Ctx.CompanyId, visitId, ct);
			if (room == null) return null;
			var mine = (await Booking.PeopleAsync(me.Ctx, me.Account.ID, ct)).Select(p => p.PatientId).ToHashSet();
			return mine.Contains(room.Visit.PatientId) ? room : null;
		}

		[HttpGet("Video/{id:int}")]
		public async Task<IActionResult> Video(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			var room = await MyRoomAsync(me, id, ct);
			if (room == null) return NotFound();
			await PrepareAsync(me, "appointments", ct);
			var join = await Tele.JoinAsync(me.Ctx.CompanyId, id, HospitalTeleSide.Patient, DateTime.UtcNow, NowLocal, ct);
			ViewBag.Refusal = join.Ok ? null : join.Error;
			ViewBag.Ice = CrossBuy.Views.Hospital.HospitalTeleRoomModel.IceFrom(HttpContext.RequestServices.GetService<Microsoft.Extensions.Configuration.IConfiguration>());
			return View(room);
		}

		[HttpPost("Video/{id:int}/Signal"), ValidateAntiForgeryToken]
		public async Task<IActionResult> VideoSignal(int id, [FromBody] PortalTeleSignalInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me || input == null) return NotFound(new { ok = false });
			if (await MyRoomAsync(me, id, ct) == null) return NotFound(new { ok = false });
			var r = await Tele.SignalAsync(me.Ctx.CompanyId, id, HospitalTeleSide.Patient, input.Kind, input.Payload, DateTime.UtcNow, ct);
			return Json(new { ok = r.Ok, error = r.Error == null ? null : _t[r.Error].Value });
		}

		[HttpGet("Video/{id:int}/Poll")]
		public async Task<IActionResult> VideoPoll(int id, long after = 0, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotFound(new { ok = false });
			var room = await MyRoomAsync(me, id, ct);
			if (room == null) return NotFound(new { ok = false });
			var s = await Tele.PollAsync(me.Ctx.CompanyId, id, HospitalTeleSide.Patient, after, ct);
			return Json(new { ok = true, ended = room.Visit.Status == HospitalTeleStatus.Ended, signals = s.Select(x => new { id = x.ID, kind = x.Kind, payload = x.Payload }) });
		}

		[HttpGet("Book")]
		public async Task<IActionResult> Book(CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "book", ct);
			ViewBag.Clinics = await Rules.ClinicsAsync(me.Ctx, ct);
			ViewBag.Doctors = await Rules.DoctorsAsync(me.Ctx, ct);
			return View();
		}

		// ---------------- H27: results, bills, medicines ----------------

		/// Results: lab and imaging, and (H30) the dental record — the dentist's own four tabs, the same drawing. The dental
		/// tab is one of the account's people (their own by default), once they have a FINISHED dental visit: a chart
		/// being charted in the chair is the dentist's working state, not yet the patient's report.
		[HttpGet("Results")]
		public async Task<IActionResult> Results(int? patientId, string? tab, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "results", ct);
			var people = (IReadOnlyList<HospitalPortalPerson>)ViewBag.People;
			var who = people.FirstOrDefault(p => p.PatientId == patientId) ?? people.FirstOrDefault();
			ViewBag.DentalWho = who;
			ViewBag.Tab = tab == "dental" || patientId != null ? "dental" : "lab";
			if (who != null)
			{
				var visits = await (from v in _db.Set<HospitalDentalVisit>().AsNoTracking()
									join a in _db.HospitalAppointments.AsNoTracking() on v.AppointmentId equals a.ID
									where v.CompanyId == me.Ctx.CompanyId && a.CompanyId == me.Ctx.CompanyId && a.PatientId == who.PatientId && a.Status == HospitalAppointmentStatus.Done
									orderby a.AppointmentDate descending, a.StartMinute descending
									select new { a.AppointmentDate, a.StartMinute, a.PractitionerId }).Take(20).ToListAsync(ct);
				ViewBag.DentalVisits = visits.Select(v => (v.AppointmentDate, v.StartMinute, v.PractitionerId)).ToList();
				if (visits.Count > 0)
				{
					ViewBag.Dentist = (await Dental.DentistsAsync(me.Ctx, ct)).FirstOrDefault(d => d.Id == visits[0].PractitionerId);
					var chart = await Dental.ChartAsync(me.Ctx, who.PatientId, DateTime.UtcNow, ct);
					ViewBag.Dental = chart;
					// H31: the imaging orders' pictures — only of the orders the doctor released to the portal
					var released = chart == null ? new HashSet<int>() : await RadImages.ReleasedAsync(me.Ctx, chart.Images.Select(i => i.Id).ToList(), ct);
					ViewBag.DentalReleased = released;
					ViewBag.DentalPictures = await RadImages.OfOrdersAsync(me.Ctx, released.ToList(), ct);
					// the plan's procedures by name, as the dentist's tooth panel shows them (no prices: the plan's quote is the clinic's)
					ViewBag.DentalProcs = (await Dental.ProceduresAsync(me.Ctx, ct)).ToDictionary(x => x.Procedure.ServiceId.ToString(), x => (object)new { code = x.Code, name = DisplayName.Of(x.Name, x.NameEn) });
				}
			}
			var results = await Records.ResultsAsync(me.Ctx, me.Account.ID, ct);
			// H31: a released imaging result carries its pictures
			ViewBag.ResultPictures = await RadImages.OfOrdersAsync(me.Ctx, results.Where(r => r.Source == HospitalPortalResultSource.Radiology && r.Released).Select(r => r.OrderId).ToList(), ct);
			return View(results);
		}

		/// H31: an imaging picture — only of one of this account's people's orders, released by the doctor. A missing,
		/// foreign or unreleased one is the same 404.
		[HttpGet("Images/{id:int}")]
		public async Task<IActionResult> Image(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var people = (await Booking.ClinicalPeopleAsync(me.Ctx, me.Account.ID, ct)).Select(p => p.PatientId).ToList();   // H251
			var img = await RadImages.OpenForPatientAsync(me.Ctx, id, people, ct);
			if (img is not { } x) return NotFound();
			Response.Headers["Cache-Control"] = "private, max-age=600";
			return File(x.Content, x.Type);
		}

		[HttpGet("Bills")]
		public async Task<IActionResult> Bills(CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "bills", ct);
			return View(await Records.BillsAsync(me.Ctx, me.Account.ID, DateTime.UtcNow, ct));
		}

		[HttpGet("Medicines")]
		public async Task<IActionResult> Medicines(CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "meds", ct);
			return View(await Records.MedicinesAsync(me.Ctx, me.Account.ID, NowLocal, ct));
		}

		// ---------------- the wizard's questions ----------------

		[HttpGet("Book/Doctors")]
		public async Task<IActionResult> BookDoctors(int patientId, int departmentId, string visitType, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var (why, mode, docs) = await Booking.AllowedDoctorsAsync(me.Ctx, me.Account.ID, patientId, departmentId, visitType, ct);
			return base.Json(new { ok = why == null, error = why == null ? null : _t[why].Value, mode, doctors = docs });
		}

		[HttpGet("Book/Slots")]
		public async Task<IActionResult> BookSlots(int patientId, int departmentId, string visitType, int? practitionerId, DateTime date, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var (why, slots) = await Booking.SlotsAsync(me.Ctx, me.Account.ID, patientId, departmentId, visitType, practitionerId, date, NowLocal, ct);
			return base.Json(new { ok = why == null, error = why == null ? null : _t[why].Value,
				slots = slots.Select(s => new { doctor = s.PractitionerId, minute = s.StartMinute, state = s.State, label = (s.StartMinute / 60).ToString("00") + ":" + (s.StartMinute % 60).ToString("00") }) });
		}

		[HttpGet("Book/Days")]
		public async Task<IActionResult> BookDays(int patientId, int departmentId, string visitType, int? practitionerId, int count = 21, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var (why, days) = await Booking.DaysAsync(me.Ctx, me.Account.ID, patientId, departmentId, visitType, practitionerId, NowLocal.Date, count, NowLocal, ct);
			return base.Json(new { ok = why == null, error = why == null ? null : _t[why].Value, days = days.Select(d => new { date = d.Date.ToString("yyyy-MM-dd"), works = d.Works, free = d.Free, booked = d.Booked }) });
		}

		// ---------------- photographs (the owner's rule: a person is shown by their photo) ----------------
		//
		// THROUGH HERE, NEVER BY THEIR FILE PATH. A doctor's photo is an employee photograph (/uploads/employees),
		// which PrivateFileGate keeps for the EMPLOYEE sign-in — a patient fetching it by path is refused. So the
		// portal serves it, after its own check: a doctor of the patient's hospital; and a patient's photo only to
		// the account whose people include them. A missing file is a 404 and the page shows the initials.

		[HttpGet("Photo/Doctor/{id:int}")]
		public async Task<IActionResult> DoctorPhoto(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var emp = await _db.HospitalPractitioners.AsNoTracking().Where(p => p.ID == id && p.CompanyId == me.Ctx.CompanyId && p.IsActive).Select(p => p.EmployeeId).FirstOrDefaultAsync(ct);
			if (emp is not int e) return NotFound();
			var path = (await EmployeePhotos.ResolveAsync(_db, me.Ctx, new[] { e }, ct)).GetValueOrDefault(e);
			return Serve(path);
		}

		[HttpGet("Photo/Patient/{id:int}")]
		public async Task<IActionResult> PatientPhoto(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var mine = await Booking.PeopleAsync(me.Ctx, me.Account.ID, ct);
			var p = mine.FirstOrDefault(x => x.PatientId == id);
			if (p == null) return NotFound();
			// the photo is on the document platform now; a file still under /uploads is served as before
			if (p.Photo != null && p.Photo.StartsWith("/Hospital/Patient/", StringComparison.OrdinalIgnoreCase))
			{
				var photos = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<HospitalPatientPhotoService>(HttpContext.RequestServices);
				var f = await photos.OpenForPortalAsync(me.Ctx.CompanyId, id, ct);
				if (f is not { } file) return NotFound();
				Response.Headers["Cache-Control"] = "private, max-age=600";
				return File(file.Content, file.ContentType);
			}
			return Serve(p.Photo);
		}

		/// A web path under wwwroot (/uploads/…) as its bytes, or 404 — never anything outside wwwroot.
		private IActionResult Serve(string? webPath)
		{
			if (string.IsNullOrWhiteSpace(webPath) || !webPath.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase)) return NotFound();
			var root = Path.GetFullPath(HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().WebRootPath);
			var full = Path.GetFullPath(Path.Combine(root, webPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));
			if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full)) return NotFound();
			var type = Path.GetExtension(full).ToLowerInvariant() switch { ".png" => "image/png", ".gif" => "image/gif", ".webp" => "image/webp", _ => "image/jpeg" };
			Response.Headers["Cache-Control"] = "private, max-age=600";
			return PhysicalFile(full, type);
		}

		// ---------------- the writes ----------------

		[HttpPost("Book"), ValidateAntiForgeryToken]
		public async Task<IActionResult> BookPost([FromBody] HospitalPortalBookInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			if (input == null) return NotFound(new { ok = false, code = "not_found" });
			return Json(await Booking.BookAsync(me.Ctx, me.Account.ID, input, NowLocal, DateTime.UtcNow, ct));
		}

		[HttpPost("Appointments/{id:int}/Cancel"), ValidateAntiForgeryToken]
		public async Task<IActionResult> Cancel(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var r = await Booking.CancelAsync(me.Ctx, me.Account.ID, id, NowLocal, DateTime.UtcNow, ct);
			if (r.Ok) await Waitlist.RunAsync(me.Ctx.CompanyId, OfferMail, NowLocal, DateTime.UtcNow, null, ct);   // H48: the freed time, down the line
			return Json(r);
		}

		[HttpPost("Requests"), ValidateAntiForgeryToken]
		public async Task<IActionResult> RequestPost([FromBody] HospitalPortalRequestInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			if (input == null) return NotFound(new { ok = false, code = "not_found" });
			return Json(await Booking.RequestAsync(me.Ctx, me.Account.ID, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Medicines/Refill"), ValidateAntiForgeryToken]
		public async Task<IActionResult> Refill([FromBody] HospitalPortalRefillInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			if (input == null) return NotFound(new { ok = false, code = "not_found" });
			return Json(await Records.RefillAsync(me.Ctx, me.Account.ID, input, DateTime.UtcNow, ct));
		}

		[HttpPost("Medicines/Refill/{id:int}/Withdraw"), ValidateAntiForgeryToken]
		public async Task<IActionResult> WithdrawRefill(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			return Json(await Records.WithdrawRefillAsync(me.Ctx, me.Account.ID, id, DateTime.UtcNow, ct));
		}

		[HttpPost("Requests/{id:int}/Withdraw"), ValidateAntiForgeryToken]
		public async Task<IActionResult> Withdraw(int id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			return Json(await Booking.WithdrawAsync(me.Ctx, me.Account.ID, id, DateTime.UtcNow, ct));
		}
	}
}
