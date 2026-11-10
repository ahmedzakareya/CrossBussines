using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H90 — the mortuary. Receiving a body (from an H59 certificate, or unknown), the compartments, the
	// belongings, the police hold and the autopsy, and the release or transfer — refused while anything still holds the
	// body. A body kept beyond 72 hours, or 7 days, is flagged, as are an open police hold and an unidentified body.
	// ==========================================================================================
	public sealed class HospitalMorgueReceiveInput
	{
		/// The H59 certificate; null for an unknown body.
		public int? DeathId { get; set; }
		public string? Description { get; set; }
		public int CompartmentId { get; set; }
		public string? TagNo { get; set; }
		public string? BroughtFrom { get; set; }
		public bool MedicoLegal { get; set; }
		public string? PoliceRef { get; set; }
		public bool AutopsyRequested { get; set; }
		public string? Note { get; set; }
		public List<HospitalMorgueItemInput>? Belongings { get; set; }
	}

	public sealed class HospitalMorgueItemInput
	{
		public string? Item { get; set; }
		public int Quantity { get; set; } = 1;
		public string? Note { get; set; }
	}

	public sealed class HospitalMorgueReleaseInput
	{
		public bool Transfer { get; set; }
		public string? ReleasedTo { get; set; }
		public string? Relation { get; set; }
		public string? IdNo { get; set; }
		public string? Phone { get; set; }
		public string? Destination { get; set; }
		public bool BelongingsHandedOver { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalMorgueStepInput
	{
		public string? PoliceRef { get; set; }
		public string? Note { get; set; }
		public int? CompartmentId { get; set; }
	}

	public static class HospitalMorgueWarning
	{
		public const string Over72h = "Over72h";
		public const string OverWeek = "OverWeek";
		public const string PoliceHold = "PoliceHold";
		public const string Autopsy = "Autopsy";
		public const string Unidentified = "Unidentified";
		public const string NoCertificate = "NoCertificate";
	}

	public sealed record HospitalMorgueCompartmentView(HospitalMorgueCompartment Compartment, HospitalMorgueCase? Case, string? Name, int? Hours, IReadOnlyList<string> Warnings);
	public sealed record HospitalMorgueRow(HospitalMorgueCase Case, string? Name, string? NameEn, string? Mrn, string Compartment, int Hours, IReadOnlyList<string> Warnings);
	public sealed record HospitalMorgueCasePage(HospitalMorgueCase Case, HospitalDeath? Death, string? Name, string? NameEn, string? Mrn, string? Photo, string Compartment,
		IReadOnlyList<HospitalMorgueBelonging> Belongings, int Hours, IReadOnlyList<string> Warnings, IReadOnlyList<string> Holds, IReadOnlyList<(int Id, string Code)> FreeCompartments,
		IReadOnlyDictionary<int, string> People);

	public static class HospitalMorgueRules
	{
		public const int AlertHours = 72;
		public const int LongHours = 7 * 24;

		public static int Hours(HospitalMorgueCase c, DateTime nowUtc) => (int)Math.Floor(((c.ReleasedAt ?? nowUtc) - c.ReceivedAt).TotalHours);

		public static IReadOnlyList<string> Warnings(HospitalMorgueCase c, bool certified, DateTime nowUtc)
		{
			var w = new List<string>();
			if (c.Status != HospitalMorgueStatus.InStorage) return w;
			int h = Hours(c, nowUtc);
			if (h >= LongHours) w.Add(HospitalMorgueWarning.OverWeek);
			else if (h >= AlertHours) w.Add(HospitalMorgueWarning.Over72h);
			if (c.MedicoLegal && c.PoliceClearedAt == null) w.Add(HospitalMorgueWarning.PoliceHold);
			if (c.AutopsyStatus == HospitalAutopsyStatus.Requested) w.Add(HospitalMorgueWarning.Autopsy);
			if (c.PatientId == null) w.Add(HospitalMorgueWarning.Unidentified);
			else if (!certified) w.Add(HospitalMorgueWarning.NoCertificate);
			return w;
		}

		/// What still holds the body, in the order they are usually lifted. Empty: it may go.
		public static IReadOnlyList<string> Holds(HospitalMorgueCase c, bool certified, bool transfer)
		{
			var h = new List<string>();
			if (c.MedicoLegal && c.PoliceClearedAt == null && !transfer) h.Add(HospitalMorgueWarning.PoliceHold);
			if (c.AutopsyStatus == HospitalAutopsyStatus.Requested && !transfer) h.Add(HospitalMorgueWarning.Autopsy);
			if (c.PatientId == null && !transfer) h.Add(HospitalMorgueWarning.Unidentified);
			if (c.PatientId != null && !certified && !transfer) h.Add(HospitalMorgueWarning.NoCertificate);
			return h;
		}

		public static string? ReceiveRefusal(HospitalMorgueReceiveInput i)
		{
			if (i.DeathId == null && string.IsNullOrWhiteSpace(i.Description)) return "Choose the death certificate, or describe an unknown body";
			if (string.IsNullOrWhiteSpace(i.TagNo)) return "Write the body tag number";
			if (i.Belongings != null && i.Belongings.Any(b => string.IsNullOrWhiteSpace(b.Item) || b.Quantity is < 1 or > 999)) return "Each belonging needs a name and a quantity";
			return null;
		}

		public static string? ReleaseRefusal(HospitalMorgueCase c, HospitalMorgueReleaseInput i, bool certified, bool hasBelongings)
		{
			if (c.Status != HospitalMorgueStatus.InStorage) return "This body is no longer in the mortuary";
			if (Holds(c, certified, i.Transfer).Count > 0) return "Something still holds the body: lift it first";
			if (string.IsNullOrWhiteSpace(i.ReleasedTo)) return "Write who receives the body";
			if (!i.Transfer && string.IsNullOrWhiteSpace(i.Relation)) return "Write the receiver's relation to the deceased";
			if (string.IsNullOrWhiteSpace(i.IdNo)) return "Write the receiver's identity number";
			if (i.Transfer && string.IsNullOrWhiteSpace(i.Destination)) return "Write where the body is transferred";
			if (hasBelongings && !i.BelongingsHandedOver) return "Hand the belongings over with the body";
			return null;
		}
	}

	public sealed class HospitalMorgueService
	{
		public const string NoTables = "The mortuary is not installed yet (hospital_h90_morgue.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalMorgueService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
		private IQueryable<HospitalMorgueCompartment> Compartments(BusinessContext ctx) => _db.Set<HospitalMorgueCompartment>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalMorgueCase> Cases(BusinessContext ctx) => _db.Set<HospitalMorgueCase>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalMorgueBelonging> Belongings(BusinessContext ctx) => _db.Set<HospitalMorgueBelonging>().Where(x => x.CompanyId == ctx.CompanyId);

		public async Task<bool> CanWorkAsync(BusinessContext ctx, CancellationToken ct) =>
			await _access.CanAsync(ctx, HospitalActions.Admit, null, ct) || await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
		public Task<bool> CanManageAsync(BusinessContext ctx, CancellationToken ct) => _access.CanAsync(ctx, HospitalActions.Manage, null, ct);

		private async Task<bool> CertifiedAsync(BusinessContext ctx, HospitalMorgueCase c, CancellationToken ct) =>
			c.DeathId is int d && await _db.Set<HospitalDeath>().AnyAsync(x => x.ID == d && x.CompanyId == ctx.CompanyId && x.VoidedAt == null, ct);

		/// Adds compartments by code ("A1-A6" or "F1,F2"), skipping the codes already there.
		public async Task<HospitalResult> AddCompartmentsAsync(BusinessContext ctx, string? codes, string? kind, CancellationToken ct = default)
		{
			if (!await CanManageAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalMorgueKind.All.Contains(kind ?? "")) return HospitalResult.Fail("Choose the kind of compartment");
			var list = ExpandCodes(codes);
			if (list == null || list.Count == 0) return HospitalResult.Fail("Write the codes, like A1-A6 or F1,F2");
			try
			{
				var have = (await Compartments(ctx).Select(x => x.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
				int n = 0;
				foreach (var c in list.Where(c => !have.Contains(c))) { _db.Add(new HospitalMorgueCompartment { CompanyId = ctx.CompanyId, Code = c, Kind = kind!, IsActive = true }); n++; }
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(n);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// "A1-A6" → A1…A6; "F1,F2" → F1, F2; null when a part cannot be read or there are more than 100.
		public static List<string>? ExpandCodes(string? codes)
		{
			var outp = new List<string>();
			foreach (var part in (codes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				var m = System.Text.RegularExpressions.Regex.Match(part, @"^([A-Za-z]{0,3})(\d{1,3})\s*-\s*\1?(\d{1,3})$");
				if (m.Success)
				{
					int a = int.Parse(m.Groups[2].Value), b = int.Parse(m.Groups[3].Value);
					if (b < a || b - a > 99) return null;
					for (int k = a; k <= b; k++) outp.Add(m.Groups[1].Value.ToUpperInvariant() + k);
				}
				else if (System.Text.RegularExpressions.Regex.IsMatch(part, @"^[A-Za-z0-9]{1,10}$")) outp.Add(part.ToUpperInvariant());
				else return null;
			}
			return outp.Count > 100 ? null : outp.Distinct().ToList();
		}

		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, HospitalMorgueReceiveInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await CanWorkAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalMorgueRules.ReceiveRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				int? patientId = null; bool medicoLegal = input.MedicoLegal, autopsy = input.AutopsyRequested;
				if (input.DeathId is int did)
				{
					var d = await _db.Set<HospitalDeath>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == did && x.CompanyId == ctx.CompanyId, ct);
					if (d == null) return HospitalResult.Fail("not_found");
					if (d.VoidedAt != null) return HospitalResult.Fail("This death certificate is voided");
					if (await Cases(ctx).AnyAsync(x => x.DeathId == did, ct)) return HospitalResult.Fail("This body is already in the mortuary register");
					patientId = d.PatientId;
					// an unnatural or undetermined manner is a police case, and the certificate's autopsy request carries over
					medicoLegal |= d.Manner is not (HospitalDeathManner.Natural);
					autopsy |= d.AutopsyRequested;
				}
				else medicoLegal = true;   // an unknown body is always a police case
				var comp = await Compartments(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.CompartmentId, ct);
				if (comp == null || !comp.IsActive) return HospitalResult.Fail("Choose a free compartment");
				if (await Cases(ctx).AnyAsync(x => x.CompartmentId == comp.ID && x.Status == HospitalMorgueStatus.InStorage, ct)) return HospitalResult.Fail("Choose a free compartment");
				var tag = input.TagNo!.Trim();
				if (await Cases(ctx).AnyAsync(x => x.TagNo == tag && x.Status == HospitalMorgueStatus.InStorage, ct)) return HospitalResult.Fail("Another body in the mortuary has this tag");
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				var nos = await Cases(ctx).Select(x => x.CaseNo).ToListAsync(ct);
				var row = new HospitalMorgueCase
				{
					CompanyId = ctx.CompanyId, CaseNo = HospitalDentalPracticeRules.NextNo(nos, "MG-", 6), PatientId = patientId, DeathId = input.DeathId, Description = Clip(input.Description, 500),
					TagNo = tag[..Math.Min(30, tag.Length)], CompartmentId = comp.ID, Status = HospitalMorgueStatus.InStorage, ReceivedAt = nowUtc, ReceivedByEmployeeId = employeeId,
					BroughtFrom = Clip(input.BroughtFrom, 150), MedicoLegal = medicoLegal, PoliceRef = Clip(input.PoliceRef, 60),
					AutopsyStatus = autopsy ? HospitalAutopsyStatus.Requested : HospitalAutopsyStatus.None, Note = Clip(input.Note, 500),
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				foreach (var b in input.Belongings ?? new())
					_db.Add(new HospitalMorgueBelonging { CompanyId = ctx.CompanyId, CaseId = row.ID, Item = Clip(b.Item, 150)!, Quantity = b.Quantity, Note = Clip(b.Note, 200), RecordedAt = nowUtc, RecordedByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }) { return HospitalResult.Fail("Choose a free compartment"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private async Task<(HospitalMorgueCase? C, HospitalResult? Fail)> OpenCaseAsync(BusinessContext ctx, int id, CancellationToken ct)
		{
			if (!await CanWorkAsync(ctx, ct)) return (null, HospitalResult.Fail("forbidden"));
			var c = await Cases(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (c == null) return (null, HospitalResult.Fail("not_found"));
			if (c.Status != HospitalMorgueStatus.InStorage) return (null, HospitalResult.Fail("This body is no longer in the mortuary"));
			return (c, null);
		}

		public async Task<HospitalResult> ClearPoliceAsync(BusinessContext ctx, int id, HospitalMorgueStepInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (c, fail) = await OpenCaseAsync(ctx, id, ct); if (fail != null) return fail;
				if (!c!.MedicoLegal || c.PoliceClearedAt != null) return HospitalResult.Fail("There is no police hold to lift");
				if (string.IsNullOrWhiteSpace(input.PoliceRef ?? c.PoliceRef)) return HospitalResult.Fail("Write the police report number");
				c.PoliceRef = Clip(input.PoliceRef, 60) ?? c.PoliceRef; c.PoliceClearedAt = nowUtc; c.PoliceClearedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AutopsyAsync(BusinessContext ctx, int id, bool done, HospitalMorgueStepInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (c, fail) = await OpenCaseAsync(ctx, id, ct); if (fail != null) return fail;
				if (done)
				{
					if (c!.AutopsyStatus != HospitalAutopsyStatus.Requested) return HospitalResult.Fail("No autopsy is waiting");
					if (string.IsNullOrWhiteSpace(input.Note)) return HospitalResult.Fail("Write who performed the autopsy and where the report is");
					c.AutopsyStatus = HospitalAutopsyStatus.Done; c.AutopsyDoneAt = nowUtc; c.AutopsyNote = Clip(input.Note, 500);
				}
				else
				{
					if (c!.AutopsyStatus != HospitalAutopsyStatus.None) return HospitalResult.Fail("An autopsy is already requested");
					c.AutopsyStatus = HospitalAutopsyStatus.Requested; c.AutopsyNote = Clip(input.Note, 500);
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> MoveAsync(BusinessContext ctx, int id, HospitalMorgueStepInput input, CancellationToken ct = default)
		{
			try
			{
				var (c, fail) = await OpenCaseAsync(ctx, id, ct); if (fail != null) return fail;
				var comp = await Compartments(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == input.CompartmentId, ct);
				if (comp == null || !comp.IsActive || comp.ID == c!.CompartmentId || await Cases(ctx).AnyAsync(x => x.CompartmentId == comp.ID && x.Status == HospitalMorgueStatus.InStorage, ct))
					return HospitalResult.Fail("Choose a free compartment");
				c.CompartmentId = comp.ID;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 }) { return HospitalResult.Fail("Choose a free compartment"); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> AddBelongingAsync(BusinessContext ctx, int id, HospitalMorgueItemInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (c, fail) = await OpenCaseAsync(ctx, id, ct); if (fail != null) return fail;
				if (string.IsNullOrWhiteSpace(input.Item) || input.Quantity is < 1 or > 999) return HospitalResult.Fail("Each belonging needs a name and a quantity");
				var row = new HospitalMorgueBelonging { CompanyId = ctx.CompanyId, CaseId = c!.ID, Item = Clip(input.Item, 150)!, Quantity = input.Quantity, Note = Clip(input.Note, 200), RecordedAt = nowUtc, RecordedByEmployeeId = employeeId };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> ReleaseAsync(BusinessContext ctx, int id, HospitalMorgueReleaseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				if (!await CanWorkAsync(ctx, ct)) return HospitalResult.Fail("forbidden");
				var c = await Cases(ctx).FirstOrDefaultAsync(x => x.ID == id, ct);
				if (c == null) return HospitalResult.Fail("not_found");
				bool certified = await CertifiedAsync(ctx, c, ct), has = await Belongings(ctx).AnyAsync(b => b.CaseId == c.ID, ct);
				if (HospitalMorgueRules.ReleaseRefusal(c, input, certified, has) is string why) return HospitalResult.Fail(why);
				c.Status = input.Transfer ? HospitalMorgueStatus.Transferred : HospitalMorgueStatus.Released; c.ReleasedAt = nowUtc; c.ReleasedByEmployeeId = employeeId;
				c.ReleasedTo = Clip(input.ReleasedTo, 150); c.ReleasedToRelation = Clip(input.Relation, 50); c.ReleasedToIdNo = Clip(input.IdNo, 30); c.ReleasedToPhone = Clip(input.Phone, 30);
				c.Destination = Clip(input.Destination, 150);
				if (!string.IsNullOrWhiteSpace(input.Note)) c.Note = Clip(((c.Note == null ? "" : c.Note + " · ") + input.Note.Trim()), 500);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------------------------------------------------------- reading
		public async Task<(IReadOnlyList<HospitalMorgueCompartmentView> Board, bool Ready)> BoardAsync(BusinessContext ctx, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var comps = await Compartments(ctx).AsNoTracking().Where(x => x.IsActive).ToListAsync(ct);
				var cases = await (from c in Cases(ctx).AsNoTracking() where c.Status == HospitalMorgueStatus.InStorage
								   join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID into pj from p in pj.DefaultIfEmpty()
								   select new { c, Name = p == null ? null : p.Name, NameEn = p == null ? null : p.NameEn }).ToListAsync(ct);
				var deathIds = cases.Where(x => x.c.DeathId != null).Select(x => x.c.DeathId!.Value).ToList();
				var valid = (await _db.Set<HospitalDeath>().AsNoTracking().Where(d => deathIds.Contains(d.ID) && d.VoidedAt == null).Select(d => d.ID).ToListAsync(ct)).ToHashSet();
				var board = comps.OrderBy(x => x.Kind).ThenBy(x => x.Code.Length).ThenBy(x => x.Code).Select(k =>
				{
					var x = cases.FirstOrDefault(y => y.c.CompartmentId == k.ID);
					if (x == null) return new HospitalMorgueCompartmentView(k, null, null, null, Array.Empty<string>());
					bool cert = x.c.DeathId is int d && valid.Contains(d);
					return new HospitalMorgueCompartmentView(k, x.c, x.Name == null ? null : DisplayName.Of(x.Name, x.NameEn), HospitalMorgueRules.Hours(x.c, nowUtc), HospitalMorgueRules.Warnings(x.c, cert, nowUtc));
				}).ToList();
				return (board, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalMorgueCompartmentView>(), false); }
		}

		public async Task<(IReadOnlyList<HospitalMorgueRow> Rows, int Total)> ListAsync(BusinessContext ctx, string? q, string? filter, int page, int pageSize, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var query = from c in Cases(ctx).AsNoTracking()
							join k in Compartments(ctx).AsNoTracking() on c.CompartmentId equals k.ID
							join p in _db.HospitalPatients.AsNoTracking() on c.PatientId equals p.ID into pj from p in pj.DefaultIfEmpty()
							select new { c, k.Code, Name = p == null ? null : p.Name, NameEn = p == null ? null : p.NameEn, Mrn = p == null ? null : p.Mrn };
				query = filter switch
				{
					"out" => query.Where(x => x.c.Status != HospitalMorgueStatus.InStorage),
					"police" => query.Where(x => x.c.Status == HospitalMorgueStatus.InStorage && x.c.MedicoLegal && x.c.PoliceClearedAt == null),
					_ => query.Where(x => x.c.Status == HospitalMorgueStatus.InStorage),
				};
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.c.CaseNo.Contains(s) || x.c.TagNo.Contains(s) || (x.Name != null && x.Name.Contains(s)) || (x.NameEn != null && x.NameEn.Contains(s))
					|| (x.Mrn != null && x.Mrn.Contains(s)) || (x.c.Description != null && x.c.Description.Contains(s)) || (x.c.ReleasedTo != null && x.c.ReleasedTo.Contains(s))); }
				int total = await query.CountAsync(ct);
				var list = await (filter == "out" ? query.OrderByDescending(x => x.c.ReleasedAt) : query.OrderBy(x => x.c.ReceivedAt)).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var deathIds = list.Where(x => x.c.DeathId != null).Select(x => x.c.DeathId!.Value).ToList();
				var valid = (await _db.Set<HospitalDeath>().AsNoTracking().Where(d => deathIds.Contains(d.ID) && d.VoidedAt == null).Select(d => d.ID).ToListAsync(ct)).ToHashSet();
				return (list.Select(x => new HospitalMorgueRow(x.c, x.Name, x.NameEn, x.Mrn, x.Code, HospitalMorgueRules.Hours(x.c, nowUtc),
					HospitalMorgueRules.Warnings(x.c, x.c.DeathId is int d && valid.Contains(d), nowUtc))).ToList(), total);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (Array.Empty<HospitalMorgueRow>(), 0); }
		}

		/// The certificates whose body is not yet in the register (the receive picker), the newest first.
		public async Task<IReadOnlyList<(int Id, string Text)>> PendingDeathsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var taken = Cases(ctx).Where(c => c.DeathId != null).Select(c => c.DeathId);
				return (await (from d in _db.Set<HospitalDeath>().AsNoTracking()
							   join p in _db.HospitalPatients.AsNoTracking() on d.PatientId equals p.ID
							   where d.CompanyId == ctx.CompanyId && d.VoidedAt == null && !taken.Contains(d.ID)
							   orderby d.DiedAt descending
							   select new { d.ID, d.DeathNo, p.Name, p.NameEn, p.Mrn }).Take(200).ToListAsync(ct))
					.Select(x => (x.ID, x.DeathNo + " — " + DisplayName.Of(x.Name, x.NameEn) + " — " + x.Mrn)).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<(int, string)>(); }
		}

		public async Task<HospitalMorgueCasePage?> CasePageAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var c = await Cases(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
				if (c == null) return null;
				var death = c.DeathId == null ? null : await _db.Set<HospitalDeath>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.DeathId, ct);
				var p = c.PatientId == null ? null : await _db.HospitalPatients.AsNoTracking().Where(x => x.ID == c.PatientId).Select(x => new { x.Name, x.NameEn, x.Mrn, x.PhotoPath }).FirstOrDefaultAsync(ct);
				var comps = await Compartments(ctx).AsNoTracking().Where(x => x.IsActive).ToListAsync(ct);
				var busy = (await Cases(ctx).AsNoTracking().Where(x => x.Status == HospitalMorgueStatus.InStorage).Select(x => x.CompartmentId).ToListAsync(ct)).ToHashSet();
				var items = await Belongings(ctx).AsNoTracking().Where(b => b.CaseId == id).OrderBy(b => b.ID).ToListAsync(ct);
				bool cert = death != null && death.VoidedAt == null;
				var eids = new[] { c.ReceivedByEmployeeId, c.ReleasedByEmployeeId, c.PoliceClearedByEmployeeId }.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new HospitalMorgueCasePage(c, death, p?.Name, p?.NameEn, p?.Mrn, p?.PhotoPath, comps.FirstOrDefault(k => k.ID == c.CompartmentId)?.Code ?? "?", items,
					HospitalMorgueRules.Hours(c, nowUtc), HospitalMorgueRules.Warnings(c, cert, nowUtc), HospitalMorgueRules.Holds(c, cert, false),
					comps.Where(k => !busy.Contains(k.ID)).OrderBy(k => k.Code.Length).ThenBy(k => k.Code).Select(k => (k.ID, k.Code)).ToList(), people);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}
	}
}
