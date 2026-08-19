using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL.Reporting;
using CrossBuy.BL.TasksCalendar;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Reporting;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Uat
{
	// ==========================================================================================
	// UAT DATASET SEEDER — NOTIFICATIONS, REPORTING, COUNTS, CLEANUP
	// ==========================================================================================
	public sealed partial class UatDatasetSeeder
	{
		/// A UAT persona: an employee, the identity they sign in as, and the roles that identity holds.
		/// Roles are READ from Identity, never asserted — the whole point of the role-difference scenarios is
		/// that the dataset agrees with the database's own authorization state.
		private sealed class UatPersona
		{
			public required int EmployeeId { get; init; }
			public required string UserId { get; init; }
			public required string UserName { get; init; }
			public required string DisplayName { get; init; }
			public required IReadOnlyCollection<string> Roles { get; init; }
			public required BusinessContext Context { get; init; }
		}

		// ==========================================================================================
		// PERSONAS
		//
		// Resolved from the database so the report/permission scenarios describe what is actually true. A
		// hard-coded "2045 is the auditor" would keep passing after somebody changed the role assignment,
		// which is the one thing these scenarios exist to detect.
		// ==========================================================================================
		private async Task<List<UatPersona>> PersonasAsync(int companyId, CancellationToken ct)
		{
			var empty = await EmptyStateEmployeeIdsAsync(companyId, ct);

			var employees = await _db.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId && e.IsActive && e.UserId != "")
				.Select(e => new { e.ID, e.UserId, e.FullName, e.BranchID })
				.ToListAsync(ct);

			var userIds = employees.Select(e => e.UserId).ToList();

			var roleRows = await (from ur in _db.UserRoles
								 join r in _db.Roles on ur.RoleId equals r.Id
								 where userIds.Contains(ur.UserId)
								 select new { ur.UserId, RoleName = r.Name })
				.ToListAsync(ct);

			var names = await _db.Users.AsNoTracking()
				.Where(u => userIds.Contains(u.Id))
				.Select(u => new { u.Id, u.UserName })
				.ToListAsync(ct);

			var result = new List<UatPersona>();
			foreach (var e in employees)
			{
				if (empty.Contains(e.ID)) continue;      // the deliberately empty context takes part in nothing

				var roles = roleRows.Where(r => r.UserId == e.UserId && r.RoleName != null)
					.Select(r => r.RoleName!).Distinct().ToArray();

				result.Add(new UatPersona
				{
					EmployeeId = e.ID,
					UserId = e.UserId,
					UserName = names.FirstOrDefault(n => n.Id == e.UserId)?.UserName ?? "(no user)",
					DisplayName = e.FullName,
					Roles = roles,
					// Source stays Http: these contexts stand in for a signed-in person and must be authorized
					// exactly as that person would be. A System context would quietly widen what they may do.
					Context = new BusinessContext
					{
						CompanyId = companyId,
						BranchId = e.BranchID,
						EmployeeId = e.ID,
						UserId = e.UserId,
						Roles = roles,
						Source = BusinessContextSource.Http,
					},
				});
			}

			// Role-holders first: they are the personas the reporting scenarios need, and putting them at the
			// front means a plan that only takes the first few still covers the interesting cases.
			return result
				.OrderByDescending(p => p.Roles.Contains("SuperAdmin"))
				.ThenByDescending(p => p.Roles.Contains("Admin"))
				.ThenByDescending(p => p.Roles.Contains("Auditor"))
				.ThenBy(p => p.EmployeeId)
				.ToList();
		}

		// ==========================================================================================
		// NOTIFICATIONS
		//
		// WRITTEN THROUGH THE DbContext, DELIBERATELY, AND THIS IS THE ONE PLACE THAT NEEDS EXPLAINING.
		//
		// INotificationService.NotifyAsync is the product's writer, and it takes a dedupKey — but its own
		// comment states the key is "an unread-noise guard, not true idempotency": it matches UNREAD rows
		// only. A seeded dataset must contain READ notifications (an all-unread bell is not a tested bell),
		// and on a second run every read row would be re-created. So the seeder owns the idempotency check
		// itself, against BOTH read and unread rows, and inserts the row directly.
		//
		// Nothing is lost by doing so: NotifyAsync's other job is the SignalR push, which is a live-delivery
		// concern with no meaning for a row dated three weeks ago.
		//
		// COMPANY: stamped explicitly. Notification is one of the twelve pilot entities, so
		// CompanyWriteGuardInterceptor would refuse a row for any company but this scope's — which is the
		// isolation proof running on the seeder itself.
		// ==========================================================================================
		private async Task SeedNotificationsAsync(
			int companyId, int actor, List<int> employees, List<int> taskIds, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			var personas = await PersonasAsync(companyId, ct);

			// The acting employee gets the dense set; two other role-holders get a small one, so the bell is
			// not a single-person feature and a second sign-in shows a different count.
			var recipients = new List<(int EmployeeId, int Count)> { (actor, plan.NotificationsForActor) };
			foreach (var p in personas.Where(p => p.EmployeeId != actor).Take(2))
				if (plan.NotificationsPerSecondaryRecipient > 0)
					recipients.Add((p.EmployeeId, plan.NotificationsPerSecondaryRecipient));

			var existing = await _db.Notifications.AsNoTracking()
				.Where(n => n.CompanyID == companyId && n.DedupKey != null
							&& n.DedupKey.StartsWith(UatMarkers.NotificationDedupPrefix))
				.Select(n => n.DedupKey!)
				.ToListAsync(ct);
			var have = existing.ToHashSet(StringComparer.Ordinal);

			var now = DateTime.Now;
			var pool = UatContent.Notifications;
			int made = 0, unread = 0;

			foreach (var (recipient, count) in recipients)
			{
				for (int i = 0; i < count; i++)
				{
					string dedupKey = $"{UatMarkers.NotificationDedupPrefix}{recipient}:{i:000}";
					if (have.Contains(dedupKey)) continue;

					var def = pool[i % pool.Length];
					bool isRead = i % 3 == 0;                      // ~1/3 read, so both states are on screen
					// Spread across the previous 30 days AND today. A notification list where every row says
					// "a few seconds ago" cannot be used to test grouping or ordering.
					var at = i < 4 ? now.AddHours(-i) : now.AddDays(-((i * 7) % 30)).AddHours(-(i % 9));

					// Every fourth row is ABOUT a seeded task, so the click-through resolves to a record the
					// owner can actually open — which is what makes the notification list part of the story
					// rather than a wall of text.
					bool linkTask = taskIds.Count > 0 && i % 4 == 1;
					int? entityId = linkTask ? taskIds[i % taskIds.Count] : null;

					var row = new Notification
					{
						CompanyID = companyId,
						RecipientEmployeeID = recipient,
						TitleAr = def.TAr,
						TitleEn = def.TEn,
						// One deliberately long body, for the notification panel's own wrapping.
						BodyAr = i == 5 ? UatContent.LongNotificationBody : def.BAr,
						BodyEn = def.BEn,
						Type = linkTask ? "task_assigned" : def.Type,
						RefId = entityId,
						IsRead = isRead,
						ReadAt = isRead ? at.AddMinutes(20) : null,
						Priority = i % 9 == 0 ? "Critical" : i % 4 == 0 ? "High" : "Normal",
						Category = linkTask ? "Tasks" : def.Category,
						ActorEmployeeID = employees.Count > 0 ? employees[i % employees.Count] : actor,
						DedupKey = dedupKey,
						// Two rows expire in the future, so the read-time expiry predicate has something to
						// keep; nothing is seeded already-expired, because an invisible row is not a test.
						ExpiresAt = i is 2 or 12 ? now.AddDays(14) : null,
						Url = linkTask ? $"/Tasks/Detail?id={entityId}" : null,
						EntityType = linkTask ? TaskCalendarEntityCodes.Task : null,
						EntityId = entityId,
						CreatedAt = at,
						CreatedBy = actor,
					};

					_db.Notifications.Add(row);
					have.Add(dedupKey);
					made++;
					if (!isRead) unread++;
				}
			}

			if (made > 0) await _db.SaveChangesAsync(ct);

			created["notifications"] = made;
			notes.Add($"notifications: {made} created for {recipients.Count} recipient(s) ({unread} unread), " +
					  "dates spread over the previous 30 days");
			notes.Add("notifications: the empty-state employee (dev.clerk) is excluded on purpose — the brief " +
					  "requires a context that still shows the empty state on a dense database.");
		}

		// ==========================================================================================
		// REPORTING
		//
		// AUTHORIZATION IS NOT WEAKENED TO POPULATE A TABLE. Every favourite, layout and run is created for a
		// persona through the REAL service, which authorizes first — so:
		//
		//   * a SuperAdmin gets rows for all three platform reports;
		//   * an Auditor gets rows for Platform.BusinessEventLog (reporting.businessevents.view is mapped to
		//     Admin/SuperAdmin/Auditor in Program.cs) and Platform.ReportCatalog (Public), and NOTHING for
		//     Platform.ReportRunHistory, whose key is ReportPermissions.Administer;
		//   * the no-role employee gets nothing at all, and that refusal is RECORDED as the fail-closed proof
		//     rather than worked around.
		//
		// So the row counts per persona ARE the role-difference evidence the brief asks for. A favourite that
		// exists for a report the persona may not open would be evidence of the opposite.
		// ==========================================================================================
		private static readonly string[] AllPlatformReports =
		{
			"Platform.ReportCatalog", "Platform.BusinessEventLog", "Platform.ReportRunHistory",
		};

		private async Task SeedReportingAsync(
			int companyId, BusinessContext callerContext, UatPlan plan,
			Dictionary<string, int> created, List<string> notes, CancellationToken ct)
		{
			var personas = await PersonasAsync(companyId, ct);

			// THE CALLER'S ROLES COME FROM IDENTITY'S TABLES, NOT FROM THEIR CLAIMS.
			//
			// BusinessContext.Roles is populated from ClaimTypes.Role, which exists only on a request carrying a
			// signed-in ClaimsPrincipal. A session established through /api/uat/prime deliberately does NOT sign
			// anyone in — it writes the "Employee" blob and nothing else — so callerContext.Roles is EMPTY even
			// for a user who holds Admin and SuperAdmin. The first seeding run showed exactly that: employee 5
			// (user "Admin", Admin + SuperAdmin in AspNetUserRoles) was treated as role-less, and every report
			// beyond the Public one was correctly refused, leaving the main UAT employee with a single favourite.
			//
			// The refusal was RIGHT — the gate did its job — but the dataset it produced was wrong, because it
			// described a permission state that does not exist. So the persona rebuilt from AspNetUserRoles wins
			// when the request carries no claims. That is the same source Identity itself reads at sign-in, so
			// nothing is being granted here that a real login would not grant.
			//
			// When the caller IS properly signed in (a browser session), their own claims are used unchanged.
			var acting = personas.FirstOrDefault(p => p.EmployeeId == callerContext.EmployeeId);
			var actingContext = callerContext.Roles.Count == 0 && acting != null ? acting.Context : callerContext;
			var actingRoles = actingContext.Roles;

			var contexts = new List<(string Who, BusinessContext Ctx, IReadOnlyCollection<string> Roles)>
			{
				(acting?.UserName ?? "caller", actingContext, actingRoles),
			};
			foreach (var p in personas.Where(p => p.EmployeeId != callerContext.EmployeeId && p.Roles.Count > 0).Take(3))
				contexts.Add((p.UserName, p.Context, p.Roles));

			// ONE ROLE-LESS PERSONA, INCLUDED DELIBERATELY.
			//
			// PersonasAsync sorts role-holders first, so a plain Take() never reaches an employee with no role —
			// and "no-role user fails closed" is one of the four role scenarios the brief asks the dataset to
			// prove. So a role-less employee is added explicitly. It is NOT the empty-state employee
			// (dev.clerk), which stays untouched: this one exists to make the REFUSALS visible in the seed
			// report, the other exists to keep the empty states testable.
			var roleless = personas.FirstOrDefault(p => p.Roles.Count == 0 && p.EmployeeId != callerContext.EmployeeId);
			if (roleless != null) contexts.Add((roleless.UserName, roleless.Context, roleless.Roles));

			if (callerContext.Roles.Count == 0 && acting != null && acting.Roles.Count > 0)
				notes.Add($"caller roles taken from AspNetUserRoles ({string.Join("/", acting.Roles)}): the primed " +
						  "dev session carries no ClaimsPrincipal, so BusinessContext.Roles arrived empty.");

			int favourites = 0, layouts = 0, runs = 0, denied = 0;
			var layoutIdsByPersona = new Dictionary<int, List<int>>();

			// ---- saved layouts first: a favourite may point at one -------------------------------
			//
			// THE LAYOUT INDEX IS PER-PERSONA, NOT A RUNNING COUNTER.
			//
			// It used to be a single `layoutSeq` advanced across every persona, which made each layout's NAME —
			// and the name is the idempotency key — depend on how many personas came before it. Adding one
			// persona therefore renamed every subsequent layout and a re-run created a second copy of each. The
			// index is now derived from the persona's own position, so a persona's layouts are the same layouts
			// whoever else is in the list.
			for (int p = 0; p < contexts.Count; p++)
			{
				var (who, ctx, roles) = contexts[p];
				var mine = new List<int>();
				int wanted = Math.Max(2, plan.Layouts / Math.Max(1, contexts.Count));

				for (int i = 0; i < wanted; i++)
				{
					// STRIDE 3, NOT 2. The layout catalogue is grouped by report — four BusinessEventLog entries,
					// then two ReportCatalog, then two ReportRunHistory — so a stride of 2 handed the LAST
					// persona (the role-less one) two BusinessEventLog layouts, both of which the gate correctly
					// refused, leaving that persona with nothing at all. A stride of 3 spreads each persona
					// across report codes, so a persona with narrow rights still gets the layouts it MAY have and
					// the refusals that remain are the ones worth recording.
					var def = UatContent.Layouts[(p * 3 + i) % UatContent.Layouts.Length];
					// `(who)` keeps the name unique even when two personas draw the same definition, and the
					// unique index on (CompanyID, Name) is the database's own backstop.
					string name = UatMarkers.NamePrefix + def.Ar + $" ({who})";

					// An EXISTING layout is collected, not skipped: `mine` feeds the template-pinned favourite
					// below, and skipping would make that favourite exist only on the first run.
					int existingId = await _db.ReportTemplates.AsNoTracking()
						.Where(t => t.CompanyID == companyId && t.Name == name && t.DeletedAt == null)
						.Select(t => t.Id).FirstOrDefaultAsync(ct);
					if (existingId != 0) { mine.Add(existingId); continue; }

					var result = await _reportTemplates.SaveAsync(new ReportTemplateInput
					{
						ReportCode = def.Report,
						Name = name,
						NameEn = $"{def.En} [{UatMarkers.RunId}]",
						// Personal scope for everyone. A Company-scope layout needs report-manage rights, and
						// asking for it on behalf of a persona that does not hold them would be a refusal the
						// seeder then had to hide.
						Scope = ReportTemplateScope.Personal,
						IsDefault = false,
						Layout = new ReportLayout { ShowGrandTotals = true },
						ChangeNote = $"Seeded by the UAT dataset ({UatMarkers.RunId}).",
					}, ctx, ct);

					if (result.Success) { mine.Add(result.TemplateId); layouts++; }
					else
					{
						denied++;
						notes.Add($"layout refused for {who} on {def.Report}: " +
								  string.Join("; ", result.Diagnostics.Select(d => d.Code)) +
								  " — recorded as the fail-closed proof, not worked around.");
					}
				}

				// One deliberately long layout name, on the report the caller is most likely authorized for.
				if (ctx.EmployeeId == callerContext.EmployeeId)
				{
					string longName = UatMarkers.NamePrefix + UatContent.LongLayoutName;
					int existingLong = await _db.ReportTemplates.AsNoTracking()
						.Where(t => t.CompanyID == companyId && t.Name == longName && t.DeletedAt == null)
						.Select(t => t.Id).FirstOrDefaultAsync(ct);
					if (existingLong != 0) mine.Add(existingLong);
					else
					{
						var result = await _reportTemplates.SaveAsync(new ReportTemplateInput
						{
							ReportCode = "Platform.BusinessEventLog",
							Name = longName,
							NameEn = $"Event log — very long layout name for overflow inspection [{UatMarkers.RunId}]",
							Scope = ReportTemplateScope.Personal,
							Layout = new ReportLayout { ShowGrandTotals = false },
							ChangeNote = $"Seeded by the UAT dataset ({UatMarkers.RunId}).",
						}, ctx, ct);
						if (result.Success) { mine.Add(result.TemplateId); layouts++; }
					}
				}

				if (ctx.EmployeeId is > 0) layoutIdsByPersona[ctx.EmployeeId.Value] = mine;
			}

			// ---- favourites ------------------------------------------------------------------------
			//
			// AddFavoriteAsync is IDEMPOTENT BY DESIGN — pinning twice is one row and it returns the existing
			// id. That is exactly right for the product and exactly wrong for a "created" counter: counting
			// every successful call made a second run report 15 favourites created when it created none. So the
			// row is looked up first, and only a genuinely new one is counted.
			foreach (var (who, ctx, roles) in contexts)
			{
				int employeeId = ctx.EmployeeId ?? 0;
				if (employeeId <= 0) continue;

				foreach (var code in AllPlatformReports)
				{
					bool already = await _db.ReportFavorites.AsNoTracking().AnyAsync(
						f => f.CompanyID == companyId && f.EmployeeId == employeeId
							 && f.ReportCode == code && f.TemplateId == null, ct);
					try
					{
						await _reportLibrary.AddFavoriteAsync(code, null, ctx, ct);
						if (!already) favourites++;
					}
					catch (ReportingException)
					{
						// EXPECTED for a persona without the report's permission. This is the fail-closed
						// behaviour the brief asks to be visible, so it is counted, not silenced.
						denied++;
					}
				}

				// A second favourite pinned to a SPECIFIC layout, so the favourites bar exercises both shapes
				// (report-with-default-template and report-with-this-template).
				if (layoutIdsByPersona.TryGetValue(employeeId, out var mine) && mine.Count > 0)
				{
					int templateId = mine[0];
					var template = await _db.ReportTemplates.AsNoTracking()
						.Where(t => t.Id == templateId)
						.Select(t => t.ReportCode)
						.FirstOrDefaultAsync(ct);
					if (template != null)
					{
						bool already = await _db.ReportFavorites.AsNoTracking().AnyAsync(
							f => f.CompanyID == companyId && f.EmployeeId == employeeId
								 && f.ReportCode == template && f.TemplateId == templateId, ct);
						try
						{
							await _reportLibrary.AddFavoriteAsync(template, templateId, ctx, ct);
							if (!already) favourites++;
						}
						catch (ReportingException) { denied++; }
					}
				}
			}

			// ---- run history -----------------------------------------------------------------------
			//
			// Through IReportHistoryService.RecordAsync — the product's own history writer — so the rows carry
			// the same shape the engine writes and nothing has to be kept in step by hand.
			var today = DateTime.Today;
			var formats = new[] { ReportOutputFormat.Html, ReportOutputFormat.Pdf, ReportOutputFormat.Xlsx, ReportOutputFormat.Csv };

			foreach (var (who, ctx, roles) in contexts)
			{
				// SAME FIX AS THE LAYOUTS, for the same reason. StartedAt is this row's idempotency key (there
				// is no marker column on a run and no natural id), and it used to be derived from a counter
				// running across every persona — so adding a persona shifted every later timestamp and a re-run
				// inserted 25 duplicate runs. The seed is now the persona's own employee id, which is stable.
				int runSeed = (ctx.EmployeeId ?? 0) % 97;
				int runSeq = 0;
				// Only the reports this persona may actually open. Authorization is decided by the same role map
				// the screens use, resolved here from the roles the database holds.
				var codes = AllPlatformReports.Where(c => MayRun(c, roles)).ToArray();
				if (codes.Length == 0)
				{
					notes.Add($"report runs: none for {who} (roles: {(roles.Count == 0 ? "none" : string.Join("/", roles))}) " +
							  "— fail-closed, and left that way.");
					continue;
				}

				int wanted = Math.Max(2, plan.ReportRuns / Math.Max(1, contexts.Count));
				for (int i = 0; i < wanted; i++, runSeq++)
				{
					string code = codes[runSeq % codes.Length];

					// Deterministic instant, to the second: it is the idempotency key, so it must be reproducible
					// on a second run and unique within one. Seeded from the persona's employee id, so it does
					// not move when the persona LIST changes.
					var startedAt = today
						.AddDays(-((runSeed + runSeq * 3) % 30))
						.AddHours(8 + runSeq % 10)
						.AddMinutes((runSeed + runSeq * 7) % 60)
						.AddSeconds(runSeed % 60);

					bool exists = await _db.ReportRuns.AsNoTracking().AnyAsync(
						r => r.CompanyID == companyId && r.ReportCode == code
							 && r.EmployeeId == ctx.EmployeeId && r.StartedAt == startedAt, ct);
					if (exists) continue;

					// A realistic spread of outcomes. A Denied row is included ON PURPOSE: the history screen
					// exists so an auditor can see refusals, and a history containing only successes would be a
					// success log. It is a RECORD of a refusal, not a granted access.
					var (kind, status, errorCode) = (runSeq % 8) switch
					{
						0 => (ReportRunKind.Preview, ReportRunStatus.Succeeded, (string?)null),
						1 => (ReportRunKind.Full, ReportRunStatus.Succeeded, null),
						2 => (ReportRunKind.Full, ReportRunStatus.Succeeded, null),
						3 => (ReportRunKind.Preview, ReportRunStatus.Succeeded, null),
						4 => (ReportRunKind.Scheduled, ReportRunStatus.Succeeded, null),
						5 => (ReportRunKind.Full, ReportRunStatus.Failed, "parameter_invalid"),
						6 => (ReportRunKind.Full, ReportRunStatus.Denied, "permission_denied"),
						_ => (ReportRunKind.Full, ReportRunStatus.Cancelled, "cancelled_by_user"),
					};

					string parameters =
						"{" + UatMarkers.ReportRunParameterTag +
						$",\"from\":\"{startedAt.AddDays(-30):yyyy-MM-dd}\"" +
						$",\"to\":\"{startedAt:yyyy-MM-dd}\"" +
						$",\"entityType\":\"{(runSeq % 3 == 0 ? "SalesInvoice" : runSeq % 3 == 1 ? "Task" : "")}\"}}";

					long id = await _reportHistory.RecordAsync(new ReportRunRecord
					{
						ReportCode = code,
						Kind = kind,
						Status = status,
						Format = kind == ReportRunKind.Preview ? ReportOutputFormat.Html : formats[runSeq % formats.Length],
						ParametersJson = parameters,
						ParametersHash = Sha256(parameters),
						RowCount = status == ReportRunStatus.Succeeded ? 25 + runSeq * 13 % 900 : 0,
						DurationMs = 120 + runSeq * 37 % 4200,
						ErrorCode = errorCode,
						ErrorMessage = errorCode == null ? null : $"Seeded UAT outcome ({errorCode}).",
						StartedAt = startedAt,
						CorrelationId = Guid.NewGuid(),
					}, ctx, ct);

					if (id > 0) runs++;
				}
			}

			created["reportFavourites"] = favourites;
			created["reportLayouts"] = layouts;
			created["reportRuns"] = runs;
			notes.Add($"reporting: {favourites} favourites, {layouts} saved layouts, {runs} run-history rows " +
					  $"across {contexts.Count} personas; {denied} writes correctly REFUSED by the report gate.");
			foreach (var (who, ctx, roles) in contexts)
				notes.Add($"persona {who}: employee={ctx.EmployeeId} roles=" +
						  (roles.Count == 0 ? "(none — fail-closed)" : string.Join("/", roles)));
		}

		/// The role map from Program.cs, read here rather than duplicated as a guess:
		///   Platform.ReportCatalog    -> ReportPermissions.Public      (any signed-in employee)
		///   Platform.ReportRunHistory -> ReportPermissions.Administer  -> Admin, SuperAdmin
		///   Platform.BusinessEventLog -> reporting.businessevents.view -> Admin, SuperAdmin, Auditor
		/// Kept as a predicate so the seeder does not ATTEMPT a write it knows will be refused — the refusals
		/// worth recording are the ones the service decides, not the ones the seeder could have avoided.
		private static bool MayRun(string reportCode, IReadOnlyCollection<string> roles) => reportCode switch
		{
			"Platform.ReportCatalog" => true,
			"Platform.ReportRunHistory" => roles.Contains("Admin") || roles.Contains("SuperAdmin"),
			"Platform.BusinessEventLog" => roles.Contains("Admin") || roles.Contains("SuperAdmin") || roles.Contains("Auditor"),
			_ => false,
		};

		private static string Sha256(string value)
			=> Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

		// ==========================================================================================
		// COUNTS — every figure resolved through the SAME marker the cleanup uses
		//
		// One ownership definition, read by both. Two definitions is how a cleanup ends up deleting rows a
		// count never claimed, or leaving rows it did.
		// ==========================================================================================
		private async Task<Dictionary<string, int>> CountMarkedAsync(int companyId, CancellationToken ct)
		{
			var taskIds = await OwnedTaskIdsAsync(companyId, ct);
			var eventIds = await OwnedEventIdsAsync(companyId, ct);

			// The favourite ownership rule, identical to the one cleanup applies. It counted every favourite in
			// the company, which was only accidentally right because the table started empty.
			var personaIds = (await PersonasAsync(companyId, ct)).Select(p => p.EmployeeId).ToList();
			var uatLayoutIds = await _db.ReportTemplates.AsNoTracking()
				.Where(t => t.CompanyID == companyId && t.DeletedAt == null && t.Name.StartsWith(UatMarkers.RunId))
				.Select(t => t.Id).ToListAsync(ct);

			var counts = new Dictionary<string, int>
			{
				["company"] = companyId,
				["tasks"] = taskIds.Count,
				["tasksNew"] = await _db.TaskItems.AsNoTracking()
					.CountAsync(t => taskIds.Contains(t.ID) && t.Status == "New", ct),
				["tasksInProgress"] = await _db.TaskItems.AsNoTracking()
					.CountAsync(t => taskIds.Contains(t.ID) && t.Status == "InProgress", ct),
				["tasksDone"] = await _db.TaskItems.AsNoTracking()
					.CountAsync(t => taskIds.Contains(t.ID) && t.Status == "Done", ct),
				["tasksOverdue"] = await _db.TaskItems.AsNoTracking()
					.CountAsync(t => taskIds.Contains(t.ID) && t.Status != "Done"
									 && t.DueDate != null && t.DueDate < DateTime.Now, ct),
				["checklistItems"] = await _db.TaskChecklistItems.AsNoTracking()
					.CountAsync(c => c.CompanyId == companyId && taskIds.Contains(c.TaskId), ct),
				["checklistDone"] = await _db.TaskChecklistItems.AsNoTracking()
					.CountAsync(c => c.CompanyId == companyId && taskIds.Contains(c.TaskId) && c.IsDone, ct),
				["dependencies"] = await _db.TaskDependencies.AsNoTracking()
					.CountAsync(d => d.CompanyId == companyId
									 && (taskIds.Contains(d.PredecessorTaskId) || taskIds.Contains(d.SuccessorTaskId)), ct),
				["taskTemplates"] = await _db.TaskTemplates.AsNoTracking()
					.CountAsync(t => t.CompanyId == companyId && t.Name.StartsWith(UatMarkers.RunId), ct),
				["taskTemplateItems"] = await _db.TaskTemplateItems.AsNoTracking()
					.CountAsync(i => i.CompanyId == companyId
									 && _db.TaskTemplates.Any(t => t.ID == i.TaskTemplateId
																   && t.Name.StartsWith(UatMarkers.RunId)), ct),
				["taskAutoRules"] = await _db.TaskAutoRules.AsNoTracking()
					.CountAsync(r => r.CompanyId == companyId, ct),
				["matchSuggestions"] = await _db.TaskMatchSuggestions.AsNoTracking()
					.CountAsync(s => s.CompanyId == companyId && taskIds.Contains(s.TaskId), ct),
				["timesheetEntries"] = await _db.TimesheetEntries.AsNoTracking()
					.CountAsync(e => e.CompanyId == companyId && taskIds.Contains(e.TaskId), ct),

				["calendarEvents"] = eventIds.Count,
				["calendarAttendees"] = await _db.CalendarEventAttendees.AsNoTracking()
					.CountAsync(a => eventIds.Contains(a.EventId), ct),
				["calendarSchedules"] = await _db.CalendarEventSchedules.AsNoTracking()
					.CountAsync(s => s.CompanyId == companyId && eventIds.Contains(s.EventId), ct),
				["calendarResources"] = await _db.CalendarResources.AsNoTracking()
					.CountAsync(r => r.CompanyId == companyId && r.Name.StartsWith(UatMarkers.ResourceNamePrefix), ct),
				["calendarEventResources"] = await _db.CalendarEventResources.AsNoTracking()
					.CountAsync(r => r.CompanyId == companyId && eventIds.Contains(r.EventId), ct),

				["notifications"] = await _db.Notifications.AsNoTracking()
					.CountAsync(n => n.CompanyID == companyId && n.DedupKey != null
									 && n.DedupKey.StartsWith(UatMarkers.NotificationDedupPrefix), ct),
				["notificationsUnread"] = await _db.Notifications.AsNoTracking()
					.CountAsync(n => n.CompanyID == companyId && !n.IsRead && n.DedupKey != null
									 && n.DedupKey.StartsWith(UatMarkers.NotificationDedupPrefix), ct),
				// Notifications the PRODUCT wrote as a consequence of the seeded task transitions. Owned
				// transitively through the task they are about, which is how cleanup finds them too.
				["notificationsFromTaskEvents"] = await _db.Notifications.AsNoTracking()
					.CountAsync(n => n.CompanyID == companyId && n.EntityType == TaskCalendarEntityCodes.Task
									 && n.EntityId != null && taskIds.Contains(n.EntityId.Value)
									 && (n.DedupKey == null
										 || !n.DedupKey.StartsWith(UatMarkers.NotificationDedupPrefix)), ct),

				// Business events are a CONSEQUENCE of seeding through the real services, never inserted.
				["businessEvents"] = await _db.BusinessEvents.AsNoTracking()
					.CountAsync(e => e.CompanyID == companyId && e.EntityType == TaskCalendarEntityCodes.Task
									 && taskIds.Contains(e.EntityId), ct),

				["reportRuns"] = await _db.ReportRuns.AsNoTracking()
					.CountAsync(r => r.CompanyID == companyId && r.ParametersJson != null
									 && r.ParametersJson.Contains(UatMarkers.ReportRunParameterTag), ct),
				["reportLayouts"] = await _db.ReportTemplates.AsNoTracking()
					.CountAsync(t => t.CompanyID == companyId && t.DeletedAt == null
									 && t.Name.StartsWith(UatMarkers.RunId), ct),
				["reportFavourites"] = await _db.ReportFavorites.AsNoTracking()
					.CountAsync(f => f.CompanyID == companyId
									 && ((f.TemplateId != null && uatLayoutIds.Contains(f.TemplateId.Value))
										 || (f.TemplateId == null
											 && personaIds.Contains(f.EmployeeId)
											 && AllPlatformReports.Contains(f.ReportCode))), ct),
			};

			return counts;
		}

		/// The tasks this run owns. ONE definition, used by counting, by every child-row predicate and by
		/// cleanup.
		///
		/// ORDERED BY ID, AND THAT IS LOAD-BEARING. Callers consume this list POSITIONALLY —
		/// `taskIds.Take(plan.ChecklistTasks)`, `taskIds.Take(plan.TimesheetTasks)`, and the dependency graph
		/// indexes straight into it. SQL Server returns no order without an ORDER BY, so an unordered list made
		/// "the first twelve tasks" mean a DIFFERENT twelve tasks on a second run, and the checklist / timesheet
		/// / dependency steps then found nothing to skip and created a second set. Company 65 doubled its
		/// checklist (13 -> 26), dependencies (5 -> 10) and timesheet entries (7 -> 14) on exactly that path,
		/// while company 1 happened to come back in a stable order and looked fine — which is the worst possible
		/// version of this bug, because the larger dataset hides it.
		private Task<List<int>> OwnedTaskIdsAsync(int companyId, CancellationToken ct)
			=> _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && t.Category != null && t.Category.StartsWith(UatMarkers.RunId))
				.OrderBy(t => t.ID)
				.Select(t => t.ID)
				.ToListAsync(ct);

		private Task<List<int>> OwnedEventIdsAsync(int companyId, CancellationToken ct)
			=> _db.CalendarEvents.AsNoTracking()
				.Where(e => e.CompanyID == companyId && e.Description != null
							&& e.Description.Contains(UatMarkers.CalendarDescriptionTag))
				.OrderBy(e => e.Id)
				.Select(e => e.Id)
				.ToListAsync(ct);

		// ==========================================================================================
		// CLEANUP
		//
		// DELETES ONLY WHAT THE MARKERS OWN, IN FK ORDER, AND NEVER TRUNCATES ANYTHING.
		//
		// The order below is children-before-parents, and it is written out rather than left to cascade
		// behaviour: only TaskTemplateItems has a real database cascade, so relying on cascades would leave
		// checklist lines, dependencies, attendees and schedules orphaned by their parent's deletion.
		//
		// BUSINESS EVENTS ARE DELETED, AND THAT DESERVES A SENTENCE. BusinessEvents is an append-only audit
		// log by design and the product never deletes a row. The cleanup here removes ONLY events whose
		// EntityId is a task THIS RUN created — rows that describe a fact that never happened in the business,
		// on a development clone, and whose continued existence would make the monitor screen reference tasks
		// that no longer exist. Their dispatch rows go first, because BusinessEventDispatch is keyed on them.
		//
		// AUTO RULES ARE NOT DELETED. TaskAutoRule rows already existed for every company before this run; the
		// seeder CONFIGURED them (assignee, enabled state) rather than creating them, so deleting them would
		// destroy pre-existing cloned data. The configuration change is left in place and reported.
		// ==========================================================================================
		public async Task<UatSeedReport> CleanupAsync(string? domain = null, CancellationToken ct = default)
		{
			var started = DateTime.UtcNow;
			var preflight = await PreflightAsync(ct);
			if (!preflight.Allowed)
				return new UatSeedReport
				{
					RunId = UatMarkers.RunId, Mode = "cleanup", Ok = false, Preflight = preflight,
					Notes = preflight.Refusals,
				};

			var context = await _contexts.TryGetCurrentAsync(ct);
			if (context is not { CompanyId: > 0 })
				return new UatSeedReport
				{
					RunId = UatMarkers.RunId, Mode = "cleanup", Ok = false, Preflight = preflight,
					Notes = new[] { "REFUSED: no company resolved. Cleanup never guesses which company to clean." },
				};

			int companyId = context.CompanyId;
			bool All(string d) => domain == null || string.Equals(domain, d, StringComparison.OrdinalIgnoreCase);

			var deleted = new Dictionary<string, int>();
			var notes = new List<string> { $"company={companyId} domain={domain ?? "(all)"}" };
			var personas = await PersonasAsync(companyId, ct);

			var taskIds = await OwnedTaskIdsAsync(companyId, ct);
			var eventIds = await OwnedEventIdsAsync(companyId, ct);

			// ---- calendar (children first) ---------------------------------------------------------
			if (All("calendar"))
			{
				deleted["calendarEventResources"] = await _db.CalendarEventResources
					.Where(r => r.CompanyId == companyId && eventIds.Contains(r.EventId)).ExecuteDeleteAsync(ct);
				deleted["calendarSchedules"] = await _db.CalendarEventSchedules
					.Where(s => s.CompanyId == companyId && eventIds.Contains(s.EventId)).ExecuteDeleteAsync(ct);
				deleted["calendarAttendees"] = await _db.CalendarEventAttendees
					.Where(a => eventIds.Contains(a.EventId)).ExecuteDeleteAsync(ct);
				deleted["calendarEvents"] = await _db.CalendarEvents
					.Where(e => eventIds.Contains(e.Id)).ExecuteDeleteAsync(ct);
				deleted["calendarResources"] = await _db.CalendarResources
					.Where(r => r.CompanyId == companyId && r.Name.StartsWith(UatMarkers.ResourceNamePrefix))
					.ExecuteDeleteAsync(ct);
			}

			// ---- tasks (children first) ------------------------------------------------------------
			if (All("tasks"))
			{
				deleted["checklistItems"] = await _db.TaskChecklistItems
					.Where(c => c.CompanyId == companyId && taskIds.Contains(c.TaskId)).ExecuteDeleteAsync(ct);
				deleted["dependencies"] = await _db.TaskDependencies
					.Where(d => d.CompanyId == companyId
								&& (taskIds.Contains(d.PredecessorTaskId) || taskIds.Contains(d.SuccessorTaskId)))
					.ExecuteDeleteAsync(ct);
				deleted["matchSuggestions"] = await _db.TaskMatchSuggestions
					.Where(s => s.CompanyId == companyId && taskIds.Contains(s.TaskId)).ExecuteDeleteAsync(ct);
				deleted["timesheetEntries"] = await _db.TimesheetEntries
					.Where(e => e.CompanyId == companyId && taskIds.Contains(e.TaskId)).ExecuteDeleteAsync(ct);
				deleted["taskAutoLogs"] = await _db.TaskAutoLogs
					.Where(l => l.CompanyId == companyId && taskIds.Contains(l.TaskId)).ExecuteDeleteAsync(ct);

				// TaskTemplateItems cascade from TaskTemplates in the database; the explicit delete keeps the
				// count honest and does not depend on the cascade being configured that way tomorrow.
				var templateIds = await _db.TaskTemplates.AsNoTracking()
					.Where(t => t.CompanyId == companyId && t.Name.StartsWith(UatMarkers.RunId))
					.Select(t => t.ID).ToListAsync(ct);
				deleted["taskTemplateItems"] = await _db.TaskTemplateItems
					.Where(i => templateIds.Contains(i.TaskTemplateId)).ExecuteDeleteAsync(ct);
				deleted["taskTemplates"] = await _db.TaskTemplates
					.Where(t => templateIds.Contains(t.ID)).ExecuteDeleteAsync(ct);
			}

			// ---- platform rows about the seeded tasks ---------------------------------------------
			if (All("tasks") || All("platform"))
			{
				deleted["notificationsAboutTasks"] = await _db.Notifications
					.Where(n => n.CompanyID == companyId && n.EntityType == TaskCalendarEntityCodes.Task
								&& n.EntityId != null && taskIds.Contains(n.EntityId.Value))
					.ExecuteDeleteAsync(ct);

				var eventRowIds = await _db.BusinessEvents.AsNoTracking()
					.Where(e => e.CompanyID == companyId && e.EntityType == TaskCalendarEntityCodes.Task
								&& taskIds.Contains(e.EntityId))
					.Select(e => e.EventId).ToListAsync(ct);

				deleted["businessEventDispatch"] = await _db.BusinessEventDispatches
					.Where(d => eventRowIds.Contains(d.EventId)).ExecuteDeleteAsync(ct);
				deleted["businessEvents"] = await _db.BusinessEvents
					.Where(e => eventRowIds.Contains(e.EventId)).ExecuteDeleteAsync(ct);
			}

			if (All("tasks"))
			{
				deleted["tasks"] = await _db.TaskItems
					.Where(t => taskIds.Contains(t.ID)).ExecuteDeleteAsync(ct);
			}

			// ---- platform: notifications + reporting ----------------------------------------------
			if (All("platform"))
			{
				deleted["notifications"] = await _db.Notifications
					.Where(n => n.CompanyID == companyId && n.DedupKey != null
								&& n.DedupKey.StartsWith(UatMarkers.NotificationDedupPrefix))
					.ExecuteDeleteAsync(ct);
			}

			if (All("reporting"))
			{
				deleted["reportRuns"] = await _db.ReportRuns
					.Where(r => r.CompanyID == companyId && r.ParametersJson != null
								&& r.ParametersJson.Contains(UatMarkers.ReportRunParameterTag))
					.ExecuteDeleteAsync(ct);

				var layoutIds = await _db.ReportTemplates.AsNoTracking()
					.Where(t => t.CompanyID == companyId && t.Name.StartsWith(UatMarkers.RunId))
					.Select(t => t.Id).ToListAsync(ct);

				// FAVOURITES ARE THE ONE TABLE WITH NO MARKER COLUMN AT ALL — no name, no dedup key, no
				// parameters blob. So ownership is reconstructed from what the seeder can prove it made:
				//
				//   * a favourite pinned to a UAT LAYOUT is ours, transitively (deleted first, because a
				//     favourite left pointing at a deleted layout is exactly the stale row the reporting screens
				//     have to special-case); OR
				//   * a favourite with no template, held by one of the personas the seeder acts as, on one of the
				//     three platform reports it pins.
				//
				// The earlier predicate was `TemplateId == null || <ours>`, which would have deleted EVERY
				// unpinned favourite in the company — including one the owner created by hand while testing. On a
				// clone full of real data that is the exact failure cleanup exists to avoid.
				var personaIds = personas.Select(p => p.EmployeeId).ToList();
				deleted["reportFavourites"] = await _db.ReportFavorites
					.Where(f => f.CompanyID == companyId
								&& ((f.TemplateId != null && layoutIds.Contains(f.TemplateId.Value))
									|| (f.TemplateId == null
										&& personaIds.Contains(f.EmployeeId)
										&& AllPlatformReports.Contains(f.ReportCode))))
					.ExecuteDeleteAsync(ct);
				deleted["reportTemplateVersions"] = await _db.ReportTemplateVersions
					.Where(v => layoutIds.Contains(v.TemplateId)).ExecuteDeleteAsync(ct);
				deleted["reportLayouts"] = await _db.ReportTemplates
					.Where(t => layoutIds.Contains(t.Id)).ExecuteDeleteAsync(ct);
			}

			notes.Add("TaskAutoRule rows were NOT deleted: they pre-existed this run for every company and the " +
					  "seeder only configured them. Deleting them would remove cloned data.");
			notes.Add("No table was truncated, no identity seed was reset, no constraint was disabled.");

			var remaining = await CountMarkedAsync(companyId, ct);
			return new UatSeedReport
			{
				RunId = UatMarkers.RunId, Mode = "cleanup", Ok = true, Preflight = preflight,
				Counts = remaining, Created = deleted, Notes = notes,
				DurationMs = (int)(DateTime.UtcNow - started).TotalMilliseconds,
			};
		}
	}
}
