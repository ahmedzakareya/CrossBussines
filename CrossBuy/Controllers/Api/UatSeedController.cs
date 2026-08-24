using CrossBuy.BL.Platform;
using CrossBuy.BL.Uat;
using CrossBuy.Models.Context;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers.Api
{
	// =============================================================================================
	// UAT DATASET ENDPOINTS — DEVELOPMENT ONLY
	//
	// A SEPARATE controller from DevSeedController, not an addition to it. DevSeedController is the
	// hypermarket track's acceptance harness (~40 document-creating endpoints); UAT dataset seeding has a
	// different owner, a different lifetime and a different set of refusals, and merging them would mean
	// every future change to one is a change to the other's blast radius.
	//
	// FOUR GATES, and they are independent on purpose:
	//
	//   1. [CrossBuy.Models.DevOnly]  — 404 outside Development. Applied at the CLASS, so a new action
	//      cannot be added without it.
	//   2. `key`                      — a shared-secret query parameter, matching the convention the other
	//      dev endpoints already use. It is not authentication and is not claimed to be; it stops a
	//      stray browser tab or a crawler on a developer's machine.
	//   3. THE SEEDER'S OWN GUARDS     — environment, catalogue name, and outbound mail. They live in
	//      UatSeedGuard, not here, so they cannot be forgotten by a future caller. See UatSeedContracts.
	//   4. A RESOLVED BusinessContext  — the company and the acting employee come from the session, never
	//      from the query string. There is no companyId parameter on `seed` at all.
	//
	// AND ONE THING THIS CONTROLLER DELIBERATELY CANNOT DO: authenticate. It holds no SignInManager and
	// issues no auth cookie. `provision` can create a UAT account with a caller-supplied password; signing in
	// is then the ordinary AccountController.Login path, exactly as for a human.
	//
	// NOTHING RUNS AT STARTUP. There is no hosted service, no migration hook and no first-request seeding:
	// the dataset appears only when a human calls `seed`.
	//
	// HOW TO DRIVE IT (and why it is two calls, not one)
	//
	//   The company scope is resolved by CompanyScopeMiddleware BEFORE any action filter runs, and an
	//   unresolved scope reads no pilot rows and may not write them at all (Batch B/B4). A first, cold,
	//   unauthenticated request therefore has nothing to resolve. So:
	//
	//     curl -c cj "http://localhost:5268/api/uat/prime?key=uat123"          # establishes the session
	//     curl -b cj "http://localhost:5268/api/uat/seed?key=uat123"           # seeds THAT session's company
	//
	//   This is the cookie-jar pattern CLAUDE.md already records as mandatory for curl-driven acceptance
	//   (HM-D59). Driving it from a signed-in browser needs only the second call.
	//
	// SCREEN VERIFICATION USES THE ORDINARY LOGIN, NOT THIS HARNESS. `prime` establishes only the session
	// blob, which is enough for the seeder but not for anything that reads CLAIMS (CalendarController reads
	// ClaimTypes.NameIdentifier; the report gate reads ClaimTypes.Role). To drive the screens:
	//
	//     curl "…/api/uat/provision?key=uat123&password=<generated per run>"   # create UAT accounts, once
	//     curl -c cj -d "UserName=uat.admin&Password=…" "…/Account/Login"      # the REAL Identity path
	//
	// THERE IS NO PASSWORDLESS SIGN-IN ON THIS CONTROLLER. One existed and was removed — see the REMOVED
	// note above `provision`. This harness can prepare an account; it cannot authenticate one.
	//
	//   THE SECOND COMPANY IS A SECOND SESSION, not a parameter: sign in (or prime) as an employee of that
	//   company and call `seed` again. See the header comment in UatDatasetSeeder for why a company override
	//   would split a task and its business event across two companies.
	// =============================================================================================
	[ApiController]
	[Route("api/uat")]
	[AllowAnonymous]
	[CrossBuy.Models.DevOnly]
	public sealed class UatSeedController : Controller
	{
		private const string Key = "uat123";

		private readonly IUatDatasetSeeder _seeder;
		private readonly IBusinessContextAccessor _contexts;
		private readonly CrossDbContext _db;

		public UatSeedController(IUatDatasetSeeder seeder, IBusinessContextAccessor contexts, CrossDbContext db)
		{
			_seeder = seeder; _contexts = contexts; _db = db;
		}

		// GET /api/uat/preflight?key=uat123
		//
		// Runs the three refusals and reports what they saw. WRITES NOTHING, so it is also the endpoint that
		// proves the guards without having to break anything: point the app at another catalogue, call this,
		// and read the refusal.
		[HttpGet("preflight")]
		public async Task<IActionResult> Preflight(string key, CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			var preflight = await _seeder.PreflightAsync(ct);
			var context = await _contexts.TryGetCurrentAsync(ct);

			return Ok(new
			{
				preflight,
				session = context == null
					? new { resolved = false, companyId = 0, employeeId = 0, roles = Array.Empty<string>() }
					: new
					{
						resolved = true,
						companyId = context.CompanyId,
						employeeId = context.EmployeeId ?? 0,
						roles = context.Roles.ToArray(),
					},
			});
		}

		// GET /api/uat/prime?key=uat123&employeeId=5
		//
		// Establishes a session for a real ACTIVE employee and returns the cookie, so the NEXT request has a
		// company for CompanyScopeMiddleware to resolve.
		//
		// This is the HM-D58 dev-context pattern, and its limits are the same: it writes the "Employee" blob
		// exactly as AccountController.Login does, from a REAL active employee row, and it invents nothing —
		// no role is granted, no password is set, no authorization is bypassed. A request primed this way is
		// authorized as that employee and no more, which is why the reporting scenarios below still produce
		// genuine refusals.
		//
		// `employeeId` is OPTIONAL and is VALIDATED, never trusted: it must name an active employee, and the
		// company written to the blob comes from that employee's own row.
		[HttpGet("prime")]
		public async Task<IActionResult> Prime(string key, int? employeeId, CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });

			var query = _db.Employee.AsNoTracking().Where(e => e.IsActive && e.UserId != "");
			query = employeeId is > 0
				? query.Where(e => e.ID == employeeId.Value)
				: query.Where(e => e.EmpCompanyID == 1);

			var employee = await query
				.OrderBy(e => e.ID)
				.Select(e => new { e.ID, e.BranchID, e.EmpCompanyID, e.UserId, e.FullName })
				.FirstOrDefaultAsync(ct);

			if (employee == null)
				return NotFound(new
				{
					message = employeeId is > 0
						? $"employee {employeeId} is not an active employee with a linked user"
						: "no active company-1 employee with a linked user",
				});

			HttpContext.Session.SetString("Employee", System.Text.Json.JsonSerializer.Serialize(
				new EmployeeViewModel
				{
					ID = employee.ID,
					BranchID = employee.BranchID,
					EmpCompanyID = employee.EmpCompanyID,
					UserId = employee.UserId,
				}));

			return Ok(new
			{
				primed = true,
				employeeId = employee.ID,
				employeeName = employee.FullName,
				companyId = employee.EmpCompanyID,
				branchId = employee.BranchID,
				next = "re-send the SAME cookie jar to /api/uat/seed — the scope is resolved on the next request",
			});
		}

		// =========================================================================================
		// REMOVED: GET /api/uat/signin  — the passwordless sign-in.
		//
		// It existed because the screen-by-screen verification needs a real ClaimsPrincipal (CalendarController
		// reads ClaimTypes.NameIdentifier; the report gate reads ClaimTypes.Role), and `prime` writes only the
		// session blob. It worked, it was [DevOnly], key-gated and catalogue-guarded — and it was still an
		// authentication bypass one attribute away from being reachable. It is GONE, not disabled: there is no
		// SignInManager on this controller any more, and no code path here issues an auth cookie.
		//
		// WHAT REPLACED IT: `provision` below creates UAT-only Identity accounts with a password the CALLER
		// supplies, and the verification then signs in through the ordinary AccountController.Login /
		// PasswordSignInAsync path — the same path a human uses. Authentication is no longer something this
		// harness can do; it is something it can only prepare an account for.
		//
		// WHY NOT REUSE THE EXISTING dev.* PERSONAS: all of them already carry a PasswordHash, and
		// DevSeedController.identity-roles-seed deliberately never rewrites an existing account's password
		// ("that would make a re-run a silent credential reset for whoever is already using it"). Their
		// passwords are not knowable from the repository, so logging in as them is not possible — and resetting
		// them silently is exactly what the brief forbids. Hence dedicated UAT accounts.
		// =========================================================================================

		// GET /api/uat/provision?key=uat123&password=<supplied at call time>
		//
		// Creates the UAT verification personas, if absent, through RoleManager/UserManager — the project's own
		// Identity APIs, the same ones DevSeedController.identity-roles-seed uses. Modelled on that endpoint
		// deliberately: it is the approved development provisioning mechanism and this is not a second one.
		//
		// THE PASSWORD IS NEVER IN SOURCE. It arrives per invocation and is handed straight to
		// UserManager.CreateAsync, so no hash is written by hand, every account behaves like an ordinary one,
		// and the repository contains no credential. The verification script generates a random one per run.
		//
		// IDEMPOTENT AND NON-DESTRUCTIVE: every step is create-if-absent. It never rewrites an existing
		// account's password, never removes a role, never deletes anything, and re-running it reports the state
		// rather than changing it. An existing account is reported as `already-present` and left alone.
		//
		// FOUR PERSONAS, ONE REFUSING DIMENSION EACH — the same matrix reasoning identity-roles-seed records:
		//   uat.admin     company 1,  Admin + SuperAdmin  — the broad tier; reaches the monitor and all reports
		//   uat.auditor   company 1,  Auditor             — sees Internal event rows, must NOT see run history
		//   uat.clerk     company 1,  no roles            — fail-closed on role alone
		//   uat.otherco   company 65, Auditor             — holds the role, wrong company: isolates COMPANY
		//
		// ROLES ARE ASSIGNED HERE, WHICH IS THE POINT AND NOT A LOOPHOLE. Provisioning a test matrix means
		// deciding what each account may do; the removed endpoint was dangerous because it AUTHENTICATED
		// without a credential, not because accounts have roles. Nothing here grants a role to an account that
		// already exists.
		[HttpGet("provision")]
		public async Task<IActionResult> Provision(
			string key, string password,
			[FromServices] Microsoft.AspNetCore.Identity.UserManager<CrossBuy.Models.Context.Admin.Users> userManager,
			[FromServices] Microsoft.AspNetCore.Identity.RoleManager<Microsoft.AspNetCore.Identity.IdentityRole> roleManager,
			CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			if (string.IsNullOrWhiteSpace(password))
				return BadRequest(new { message = "password is required and is never stored in source" });

			// The same catalogue guard the rest of the harness uses. Creating accounts is not seeding, but it
			// must not be possible against anything other than the development clone either.
			var preflight = await _seeder.PreflightAsync(ct);
			if (!preflight.Allowed)
				return StatusCode(StatusCodes.Status409Conflict, new { message = "refused", preflight.Refusals });

			// Employee.JobTitleID is non-nullable AND a foreign key, so a real job title is required.
			int jobTitleId = await _db.JobTitles.AsNoTracking().OrderBy(j => j.ID).Select(j => j.ID).FirstOrDefaultAsync(ct);
			if (jobTitleId <= 0)
				return StatusCode(500, new { message = "no JobTitles row exists to attach a UAT employee to" });

			var plan = new (string UserName, string NameAr, string NameEn, int CompanyId, string[] Roles)[]
			{
				("uat.admin",   "مدير اختبار القبول",  "UAT Admin",     1,  new[] { "Admin", "SuperAdmin" }),
				("uat.auditor", "مدقّق اختبار القبول", "UAT Auditor",   1,  new[] { "Auditor" }),
				("uat.clerk",   "موظّف اختبار القبول", "UAT Clerk",     1,  System.Array.Empty<string>()),
				("uat.otherco", "مدقّق الشركة الأخرى", "UAT Other Co",  65, new[] { "Auditor" }),

				// ---- Communication UI verification persona (TAB-3) ------------------------------------
				//
				// WHY A FIFTH PERSONA EXISTS. The rendered conformance matrix needs a real ClaimsPrincipal
				// (CommController.MeAsync reads ClaimTypes.NameIdentifier), and the four personas above were
				// all provisioned in an earlier run. This endpoint deliberately NEVER rewrites an existing
				// account's password — "that would make a re-run a silent credential reset for whoever is
				// already using it" — so their passwords are unknowable and they cannot be signed in as.
				// Adding a NEW name is the only way to obtain a usable login without resetting anybody:
				// create-if-absent means this one is created with the caller's password on first call and is
				// then left alone exactly like the others.
				//
				// NO ROLES, BY DESIGN. CommController carries [SessionValidation] and no role or permission
				// attribute; reaching /Comm needs only a Users row plus an Employee row for company scope. So
				// the least-privilege persona that can render the governed Communication screen holds no role
				// at all, and it cannot be repurposed to reach an administrative surface.
				//
				// Company 1 is explicit, matching the governed matrix's own company.
				("uat.commui",  "مدقّق واجهة الاتصالات", "UAT Comm UI",   1,  System.Array.Empty<string>()),
				//
				// Second verification persona, same contract and same reason. uat.commui above was created on a
				// previous machine, and this endpoint deliberately NEVER rewrites an existing password, so its
				// credential is unknowable here. Adding a NAME is the only way to obtain a login without
				// resetting an account somebody else may be using. Least privilege, company 1, no roles.
				("uat.commctx", "مدقّق سياق الاتصالات", "UAT Comm Context", 1, System.Array.Empty<string>()),
			};

			// Roles first: assigning a role that does not exist fails, and the vocabulary is the one Program.cs
			// maps report permissions against.
			var roleReport = new List<object>();
			foreach (var roleName in new[] { "Auditor", "Admin", "SuperAdmin" })
			{
				if (await roleManager.RoleExistsAsync(roleName)) { roleReport.Add(new { role = roleName, action = "already-present" }); continue; }
				var created = await roleManager.CreateAsync(new Microsoft.AspNetCore.Identity.IdentityRole(roleName));
				if (!created.Succeeded)
					return StatusCode(500, new { message = $"could not create role {roleName}", errors = created.Errors.Select(e => e.Description) });
				roleReport.Add(new { role = roleName, action = "created" });
			}

			var report = new List<object>();
			foreach (var spec in plan)
			{
				var actions = new List<string>();
				string email = spec.UserName + "@uat.crossbuy.local";

				var user = await userManager.FindByNameAsync(spec.UserName);
				if (user == null)
				{
					user = new CrossBuy.Models.Context.Admin.Users
					{
						UserName = spec.UserName,
						Email = email,
						EmailConfirmed = true,
						// AccountController.Login refuses a user that is not BOTH active and an end user, so an
						// account without these two would fail sign-in for a reason unrelated to its password.
						IsActive = true,
						IsEndUser = true,
					};
					var createdUser = await userManager.CreateAsync(user, password);
					if (!createdUser.Succeeded)
						return StatusCode(500, new { message = $"could not create user {spec.UserName}", errors = createdUser.Errors.Select(e => e.Description) });
					actions.Add("user-created");
				}
				else actions.Add("user-already-present-password-untouched");

				foreach (var role in spec.Roles)
				{
					if (await userManager.IsInRoleAsync(user, role)) { actions.Add($"role:{role}:already"); continue; }
					var added = await userManager.AddToRoleAsync(user, role);
					actions.Add(added.Succeeded ? $"role:{role}:added" : $"role:{role}:FAILED");
				}

				// The Employee row. BusinessContext resolves company and branch from HERE, not from the user, so
				// without it a successful sign-in still resolves no company.
				var employee = await _db.Employee.FirstOrDefaultAsync(e => e.UserId == user.Id, ct);
				if (employee == null)
				{
					employee = new CrossBuy.Models.Context.Admin.Employee
					{
						FirstName = spec.NameEn, LastName = "(UAT)", FullName = spec.NameAr, FullNameEn = spec.NameEn,
						Address = "-", PhoneNumber = "-", Email = email,
						JobTitleID = jobTitleId, EmpCompanyID = spec.CompanyId,

						// ProfileImage MUST be "" and not a placeholder like "-". _LayoutInventory.cshtml:838
						// passes it to Url.Content(), which throws ArgumentException ("The path in 'value' must
						// start with '/'") on anything that is neither empty nor a rooted path — and because it
						// happens in the SHARED LAYOUT it took EVERY shell-rendered screen to HTTP 500 for the
						// affected employee, while JSON endpoints kept working. Found exactly that way on the
						// first matrix run. The dev personas created by identity-roles-seed use "" for the same
						// reason; this now matches them.
						ProfileImage = "", Gender = "-", MaritalStatus = "-",
						DateOfBirth = new DateTime(1990, 1, 1), DateOfJoining = DateTime.Today,
						IsActive = true, UserId = user.Id, CreatedAt = DateTime.Now,
					};
					_db.Employee.Add(employee);
					await _db.SaveChangesAsync(ct);
					actions.Add($"employee-created:{employee.ID}");
				}
				else actions.Add($"employee-already-present:{employee.ID}");

				report.Add(new
				{
					userName = spec.UserName,
					employeeId = employee.ID,
					companyId = employee.EmpCompanyID,
					roles = spec.Roles,
					actions,
				});
			}

			return Ok(new
			{
				provisioned = true,
				roles = roleReport,
				personas = report,
				next = "sign in through POST /Account/Login (the ordinary Identity path) — this harness cannot authenticate",
			});
		}

		// GET /api/uat/agenda-probe?key=uat123&employeeId=5&days=7
		//
		// A READ-ONLY MEASUREMENT PROBE over IWorkspaceAgendaService, and deliberately nothing more.
		//
		// WHY IT EXISTS. Task 8 requires the agenda to be verified against the DENSE employee, whose account is
		// the repository owner's own (`Admin`) — a credential this harness must not have and no longer can get.
		// The brief allows comparing DB-derived expectations against "rendered/service output", so this returns
		// the SERVICE's answer for a named employee.
		//
		// WHY THIS IS NOT THE ENDPOINT THAT WAS JUST REMOVED. It authenticates nobody, issues no cookie, grants
		// no role and returns NO CONTENT — only counts, band totals, duplicate detection and the id set size.
		// A caller learns "employee 5 has 23 agenda rows, 9 of them overdue", never what any of them say. It
		// cannot be used to read another person's data, which is the property that made `signin` dangerous.
		[HttpGet("agenda-probe")]
		public async Task<IActionResult> AgendaProbe(
			string key, int employeeId, int days,
			[FromServices] CrossBuy.BL.TasksCalendar.IWorkspaceAgendaService agenda,
			CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			var preflight = await _seeder.PreflightAsync(ct);
			if (!preflight.Allowed)
				return StatusCode(StatusCodes.Status409Conflict, new { message = "refused", preflight.Refusals });

			var employee = await _db.Employee.AsNoTracking()
				.Where(e => e.ID == employeeId && e.IsActive)
				.Select(e => new { e.ID, e.EmpCompanyID })
				.FirstOrDefaultAsync(ct);
			if (employee == null) return NotFound(new { message = $"employee {employeeId} is not active" });

			var from = DateOnly.FromDateTime(DateTime.Now.Date);
			var result = await agenda.GetAgendaAsync(new CrossBuy.BL.TasksCalendar.WorkspaceAgendaQuery
			{
				CompanyId = employee.EmpCompanyID,
				EmployeeId = employee.ID,
				FromLocalDate = from,
				ToLocalDate = from.AddDays(Math.Clamp(days, 1, 60)),
				TimeZoneId = TimeZoneInfo.Local.Id,
				IncludeOverdue = true,
				OverdueLookbackDays = CrossBuy.BL.TasksCalendar.WorkspaceAgendaService.DefaultOverdueLookbackDays,
				PageSize = 200,
			}, ct);

			var today = DateTime.Now.Date;
			var tasks = result.Items.Where(i => i.ItemType == CrossBuy.BL.TasksCalendar.AgendaItemType.Task).ToList();
			var events = result.Items.Where(i => i.ItemType == CrossBuy.BL.TasksCalendar.AgendaItemType.CalendarEvent).ToList();

			return Ok(new
			{
				employeeId = employee.ID,
				companyId = employee.EmpCompanyID,
				days,
				totalMatched = result.TotalMatched,
				returned = result.Items.Count,
				taskRows = tasks.Count,
				calendarRows = events.Count,
				// The three bands, computed the same way the Workspace computes them.
				overdue = tasks.Count(i => i.IsOverdue),
				dueToday = tasks.Count(i => !i.IsOverdue && i.Start.SortKeyUtc(TimeZoneInfo.Local).Date == today),
				completedIncluded = tasks.Count(i => i.IsCompleted),
				// Duplicate detection, which is a requirement of its own.
				distinctTaskIds = tasks.Select(i => i.SourceId).Distinct().Count(),
				duplicateTaskRows = tasks.Count - tasks.Select(i => i.SourceId).Distinct().Count(),
				// Any source that could not be read is reported, never silently dropped.
				degraded = result.DegradedSources,
				overdueFromLocalDate = result.OverdueFromLocalDate?.ToString("yyyy-MM-dd"),
			});
		}

		// GET /api/uat/seed?key=uat123
		//
		// Idempotent. Seeds the dataset for THIS SESSION'S company. Call it twice and the second report shows
		// zero created everywhere — which is the idempotency proof, produced by the endpoint itself.
		[HttpGet("seed")]
		public async Task<IActionResult> Seed(string key, CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			var report = await _seeder.SeedAsync(ct);
			// A refused seed is a 409, not a 500: the guards refusing is a correct outcome, and a 500 would
			// send whoever called it looking for a bug.
			return report.Ok ? Ok(report) : StatusCode(StatusCodes.Status409Conflict, report);
		}

		// GET /api/uat/counts?key=uat123
		[HttpGet("counts")]
		public async Task<IActionResult> Counts(string key, CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			return Ok(await _seeder.CountsAsync(ct));
		}

		// GET /api/uat/cleanup?key=uat123&domain=reporting&confirm=DELETE-UAT-20260812
		//
		// Deletes ONLY marked rows. `confirm` must carry the run id verbatim: cleanup is the one destructive
		// operation here, and a mistyped URL must not be able to trigger it. `domain` limits the sweep to one
		// domain (tasks | calendar | platform | reporting) so cleanup can be PROVEN on a disposable subset
		// without destroying the dataset under review.
		[HttpGet("cleanup")]
		public async Task<IActionResult> Cleanup(string key, string confirm, string? domain, CancellationToken ct)
		{
			if (key != Key) return Unauthorized(new { message = "bad key" });
			if (confirm != "DELETE-" + UatMarkers.RunId)
				return BadRequest(new
				{
					message = $"cleanup requires confirm=DELETE-{UatMarkers.RunId}",
					domains = new[] { "tasks", "calendar", "platform", "reporting", "(omit for all)" },
				});

			var report = await _seeder.CleanupAsync(domain, ct);
			return report.Ok ? Ok(report) : StatusCode(StatusCodes.Status409Conflict, report);
		}
	}
}
