using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalMicroIsolateView(HospitalMicroIsolate Isolate, HospitalMicroOrganism Organism, IReadOnlyList<HospitalMicroPanelLine> Panel,
		IReadOnlyList<HospitalMicroSusceptibility> Results);
	public sealed record HospitalMicroView(bool Ready, bool IsCulture, HospitalMicroCulture? Culture, IReadOnlyList<HospitalMicroIsolateView> Isolates,
		IReadOnlyList<HospitalMicroOrganism> Organisms, IReadOnlyList<HospitalMicroAntibiotic> Antibiotics);

	public sealed class HospitalMicroAstInput { public int AntibioticId { get; set; } public string? Result { get; set; } public string? Mic { get; set; } }

	// ==========================================================================================
	// HOSPITAL H230 — the culture's bench work. See HospitalMicro.cs for the whole picture.
	// The bench (`lab`) starts, reports preliminaries, adds isolates and their susceptibility, and completes; a second
	// person (`lab-verify`) verifies through the laboratory's own verify — which then finalises the culture here.
	// ==========================================================================================
	public sealed class HospitalMicroService
	{
		public const string CultureAnalyteCode = "CULT";
		public const string NoTables = "Microbiology is not installed yet (hospital_h230_microbiology.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalMicroService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// Whether this lab service is a culture (false when the column is not there yet).
		public static async Task<bool> IsCultureAsync(CrossDbContext db, int companyId, int serviceId, CancellationToken ct = default)
		{
			try { return await db.HospitalLabTests.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && t.ServiceId == serviceId && t.IsCulture, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return false; }
		}

		/// The culture's one result row hangs on this analyte; it is made when a test becomes a culture.
		public static async Task<HospitalLabAnalyte> EnsureCultureAnalyteAsync(CrossDbContext db, int companyId, int serviceId, CancellationToken ct = default)
		{
			var a = await db.HospitalLabAnalytes.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.ServiceId == serviceId && x.Code == CultureAnalyteCode, ct);
			if (a != null) { if (!a.IsActive) { a.IsActive = true; await db.SaveChangesAsync(ct); } return a; }
			a = new HospitalLabAnalyte { CompanyId = companyId, ServiceId = serviceId, Code = CultureAnalyteCode, Name = "نتيجة المزرعة", NameEn = "Culture result", Decimals = 0, SortOrder = 0, IsActive = true };
			db.HospitalLabAnalytes.Add(a);
			await db.SaveChangesAsync(ct);
			return a;
		}

		private async Task<(HospitalLabOrder? Order, HospitalMicroCulture? Culture, HospitalResult? Fail)> LoadAsync(BusinessContext ctx, int orderId, bool needCulture, CancellationToken ct)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Lab, null, ct)) return (null, null, HospitalResult.Fail("forbidden"));
			var o = await _db.HospitalLabOrders.FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == ctx.CompanyId, ct);
			if (o == null) return (null, null, HospitalResult.Fail("not_found"));
			if (!await IsCultureAsync(_db, ctx.CompanyId, o.ServiceId, ct)) return (o, null, HospitalResult.Fail("This test is not a culture"));
			var c = await _db.Set<HospitalMicroCulture>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.LabOrderId == o.ID, ct);
			if (needCulture && c == null) return (o, null, HospitalResult.Fail("Start the culture first"));
			if (c?.Status == HospitalMicroStatus.Final) return (o, c, HospitalResult.Fail("This result is verified and locked"));
			return (o, c, null);
		}

		/// Any change after completion sends the culture back to the bench: the order waits for its result again.
		private static void BackToBench(HospitalLabOrder o, HospitalMicroCulture c)
		{
			if (c.Status != HospitalMicroStatus.Completed) return;
			c.Status = c.PreliminaryAt != null ? HospitalMicroStatus.Preliminary : HospitalMicroStatus.Incubating;
			c.CompletedAt = null; c.CompletedByEmployeeId = null;
			if (o.Status == HospitalLabStatus.Resulted) { o.Status = HospitalLabStatus.Received; o.ResultedAt = null; o.ResultedByEmployeeId = null; }
		}

		public async Task<HospitalMicroView> ViewAsync(BusinessContext ctx, int orderId, CancellationToken ct = default)
		{
			try
			{
				var o = await _db.HospitalLabOrders.AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == ctx.CompanyId, ct);
				if (o == null || !await IsCultureAsync(_db, ctx.CompanyId, o.ServiceId, ct)) return new(true, false, null, Array.Empty<HospitalMicroIsolateView>(), Array.Empty<HospitalMicroOrganism>(), Array.Empty<HospitalMicroAntibiotic>());
				var c = await _db.Set<HospitalMicroCulture>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.LabOrderId == o.ID, ct);
				var organisms = await _db.Set<HospitalMicroOrganism>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.IsActive).OrderBy(x => x.Group).ThenBy(x => x.NameEn).ToListAsync(ct);
				var antibiotics = await _db.Set<HospitalMicroAntibiotic>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.IsActive).OrderBy(x => x.Class).ThenBy(x => x.NameEn).ToListAsync(ct);
				var isolates = new List<HospitalMicroIsolateView>();
				if (c != null)
				{
					var rows = await _db.Set<HospitalMicroIsolate>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.CultureId == c.ID).OrderBy(x => x.No).ToListAsync(ct);
					var ids = rows.Select(r => r.ID).ToList();
					var sus = await _db.Set<HospitalMicroSusceptibility>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.IsolateId)).ToListAsync(ct);
					var allOrg = await _db.Set<HospitalMicroOrganism>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.ID, ct);
					foreach (var r in rows)
					{
						var org = allOrg.GetValueOrDefault(r.OrganismId) ?? new HospitalMicroOrganism { Name = "?", NameEn = "?" };
						isolates.Add(new HospitalMicroIsolateView(r, org, await PanelAsync(ctx.CompanyId, org.Group, ct), sus.Where(s => s.IsolateId == r.ID).ToList()));
					}
				}
				return new(true, true, c, isolates, organisms, antibiotics);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return new(false, true, null, Array.Empty<HospitalMicroIsolateView>(), Array.Empty<HospitalMicroOrganism>(), Array.Empty<HospitalMicroAntibiotic>()); }
		}

		private async Task<IReadOnlyList<HospitalMicroPanelLine>> PanelAsync(int companyId, string orgGroup, CancellationToken ct) =>
			await (from p in _db.Set<HospitalMicroPanelItem>().AsNoTracking()
				   join a in _db.Set<HospitalMicroAntibiotic>().AsNoTracking() on p.AntibioticId equals a.ID
				   where p.CompanyId == companyId && a.CompanyId == companyId && p.Group == orgGroup && a.IsActive
				   orderby p.Tier, p.SortOrder
				   select new HospitalMicroPanelLine(a.ID, a.Code, a.NameEn, p.Tier, p.SortOrder)).ToListAsync(ct);

		/// The culture opens on a received specimen, with its source.
		public async Task<HospitalResult> StartAsync(BusinessContext ctx, int orderId, string? source, string? detail, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, false, ct); if (fail != null) return fail;
				if (o!.Status is HospitalLabStatus.Ordered or HospitalLabStatus.Collected) return HospitalResult.Fail("Receive the specimen in the lab before entering results");
				if (!HospitalMicroRules.Sources.Contains(source ?? "")) return HospitalResult.Fail("Choose the specimen source");
				if (c != null) { BackToBench(o, c); c.Source = source!; c.SourceDetail = Clip(detail, 120); await _db.SaveChangesAsync(ct); return HospitalResult.Success(c.ID); }
				c = new HospitalMicroCulture { CompanyId = ctx.CompanyId, LabOrderId = o.ID, PatientId = o.PatientId, Source = source!, SourceDetail = Clip(detail, 120), StartedAt = nowUtc, StartedByEmployeeId = employeeId };
				_db.Add(c);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A preliminary report: the Gram stain, the growth so far and the text released to the doctor. A positive blood
		/// culture is critical at once — the order's critical flag goes up for the call-back, as any critical value.
		public async Task<HospitalResult> PreliminaryAsync(BusinessContext ctx, int orderId, string? growth, string? gramStain, string? text, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, true, ct); if (fail != null) return fail;
				if (!HospitalMicroGrowth.All.Contains(growth ?? "")) return HospitalResult.Fail("Record whether there is growth");
				if (HospitalMicroRules.PreliminaryRefusal(text) is string why) return HospitalResult.Fail(why);
				if ((gramStain ?? "").Trim().Length > 300) return HospitalResult.Fail("The Gram stain is 300 characters at most");
				BackToBench(o!, c!);
				c!.Growth = growth!; c.GramStain = Clip(gramStain, 300); c.Preliminary = Clip(text, 500); c.PreliminaryAt = nowUtc; c.PreliminaryByEmployeeId = employeeId;
				c.Status = HospitalMicroStatus.Preliminary;
				RaiseCritical(o!, c);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private static void RaiseCritical(HospitalLabOrder o, HospitalMicroCulture c)
		{
			if (HospitalMicroRules.Critical(c.Source, c.Growth) && !o.IsCritical)
			{ o.IsCritical = true; o.CriticalAcknowledgedAt = null; o.CriticalAcknowledgedByEmployeeId = null; o.CriticalNote = null; }
		}

		public async Task<HospitalResult> AddIsolateAsync(BusinessContext ctx, int orderId, int organismId, string? quantity, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, true, ct); if (fail != null) return fail;
				if (HospitalMicroRules.QuantityRefusal(quantity) is string why) return HospitalResult.Fail(why);
				if (!await _db.Set<HospitalMicroOrganism>().AnyAsync(x => x.ID == organismId && x.CompanyId == ctx.CompanyId && x.IsActive, ct)) return HospitalResult.Fail("not_found");
				var nos = await _db.Set<HospitalMicroIsolate>().Where(x => x.CompanyId == ctx.CompanyId && x.CultureId == c!.ID).Select(x => x.No).ToListAsync(ct);
				if (nos.Count >= 6) return HospitalResult.Fail("A culture has 6 isolates at most");
				BackToBench(o!, c!);
				if (c!.Growth != HospitalMicroGrowth.Growth) c.Growth = HospitalMicroGrowth.Growth;
				var row = new HospitalMicroIsolate { CompanyId = ctx.CompanyId, CultureId = c.ID, PatientId = c.PatientId, No = (nos.Count == 0 ? 0 : nos.Max()) + 1, OrganismId = organismId, Quantity = Clip(quantity, 60) };
				_db.Add(row);
				RaiseCritical(o!, c);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveIsolateAsync(BusinessContext ctx, int orderId, int isolateId, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, true, ct); if (fail != null) return fail;
				var i = await _db.Set<HospitalMicroIsolate>().FirstOrDefaultAsync(x => x.ID == isolateId && x.CompanyId == ctx.CompanyId && x.CultureId == c!.ID, ct);
				if (i == null) return HospitalResult.Fail("not_found");
				BackToBench(o!, c!);
				_db.RemoveRange(await _db.Set<HospitalMicroSusceptibility>().Where(x => x.CompanyId == ctx.CompanyId && x.IsolateId == i.ID).ToListAsync(ct));
				_db.Remove(i);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c!.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The isolate's susceptibility, replaced as a whole; its resistance flag is worked out from it.
		public async Task<HospitalResult> SaveAstAsync(BusinessContext ctx, int orderId, int isolateId, IReadOnlyList<HospitalMicroAstInput> lines, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, true, ct); if (fail != null) return fail;
				var i = await _db.Set<HospitalMicroIsolate>().FirstOrDefaultAsync(x => x.ID == isolateId && x.CompanyId == ctx.CompanyId && x.CultureId == c!.ID, ct);
				if (i == null) return HospitalResult.Fail("not_found");
				var given = (lines ?? Array.Empty<HospitalMicroAstInput>()).Where(l => !string.IsNullOrWhiteSpace(l.Result)).ToList();
				if (given.Any(l => !HospitalMicroSir.All.Contains(l.Result!.Trim().ToUpperInvariant()))) return HospitalResult.Fail("A result is S, I or R");
				if (given.Select(l => l.AntibioticId).Distinct().Count() != given.Count) return HospitalResult.Fail("An antibiotic is entered twice");
				var ids = given.Select(l => l.AntibioticId).ToList();
				var abx = await _db.Set<HospitalMicroAntibiotic>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
				if (abx.Count != ids.Count) return HospitalResult.Fail("not_found");
				BackToBench(o!, c!);
				_db.RemoveRange(await _db.Set<HospitalMicroSusceptibility>().Where(x => x.CompanyId == ctx.CompanyId && x.IsolateId == i.ID).ToListAsync(ct));
				foreach (var l in given)
					_db.Add(new HospitalMicroSusceptibility { CompanyId = ctx.CompanyId, IsolateId = i.ID, AntibioticId = l.AntibioticId, Result = l.Result!.Trim().ToUpperInvariant(), Mic = HospitalMicroRules.Mic(l.Mic) });
				var org = await _db.Set<HospitalMicroOrganism>().AsNoTracking().FirstAsync(x => x.ID == i.OrganismId && x.CompanyId == ctx.CompanyId, ct);
				i.Mdro = HospitalMicroRules.Mdro(org.Group, org.IsAureus, given.Select(l => new HospitalMicroAstLine(l.AntibioticId, abx[l.AntibioticId].Code, abx[l.AntibioticId].NameEn, l.Result!.Trim().ToUpperInvariant(), null)).ToList());
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(i.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// The bench completes: the growth is final, the cascade decides what the doctor sees, the summary is written into
		/// the order's result row and the order waits for a second person to verify it.
		public async Task<HospitalResult> CompleteAsync(BusinessContext ctx, int orderId, string? growth, string? comment, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				var (o, c, fail) = await LoadAsync(ctx, orderId, true, ct); if (fail != null) return fail;
				if (!HospitalMicroGrowth.All.Contains(growth ?? "")) return HospitalResult.Fail("Record whether there is growth");
				if ((comment ?? "").Trim().Length > 1000) return HospitalResult.Fail("The comment is 1000 characters at most");
				var isolates = await _db.Set<HospitalMicroIsolate>().Where(x => x.CompanyId == ctx.CompanyId && x.CultureId == c!.ID).OrderBy(x => x.No).ToListAsync(ct);
				var ids = isolates.Select(x => x.ID).ToList();
				var sus = await _db.Set<HospitalMicroSusceptibility>().Where(x => x.CompanyId == ctx.CompanyId && ids.Contains(x.IsolateId)).ToListAsync(ct);
				var orgIds = isolates.Select(x => x.OrganismId).Distinct().ToList();
				var orgs = await _db.Set<HospitalMicroOrganism>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && orgIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
				if (HospitalMicroRules.CompleteRefusal(growth!, isolates.Select(x => (orgs[x.OrganismId].Group, sus.Count(s => s.IsolateId == x.ID))).ToList()) is string why) return HospitalResult.Fail(why);
				var abxIds = sus.Select(s => s.AntibioticId).Distinct().ToList();
				var abx = await _db.Set<HospitalMicroAntibiotic>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && abxIds.Contains(x.ID)).ToDictionaryAsync(x => x.ID, ct);
				var summaries = new List<HospitalMicroIsolateSummary>();
				foreach (var iso in isolates)
				{
					var org = orgs[iso.OrganismId];
					var panel = await PanelAsync(ctx.CompanyId, org.Group, ct);
					var order = panel.ToDictionary(p => p.AntibioticId, p => (p.Tier, p.SortOrder));
					var lines = sus.Where(s => s.IsolateId == iso.ID).OrderBy(s => order.GetValueOrDefault(s.AntibioticId, (9, 999)))
						.Select(s => new HospitalMicroAstLine(s.AntibioticId, abx[s.AntibioticId].Code, abx[s.AntibioticId].NameEn, s.Result, s.Mic)).ToList();
					var reported = HospitalMicroRules.Reported(panel, lines);
					foreach (var s in sus.Where(s => s.IsolateId == iso.ID)) s.Reported = reported.Contains(s.AntibioticId);
					iso.Mdro = HospitalMicroRules.Mdro(org.Group, org.IsAureus, lines);
					summaries.Add(new HospitalMicroIsolateSummary(org.NameEn, iso.Quantity, iso.Mdro, lines.Where(l => reported.Contains(l.AntibioticId)).ToList()));
				}
				c!.Growth = growth!; c.Comment = Clip(comment, 1000);
				c.Mdro = string.Join(",", isolates.Where(x => x.Mdro != null).Select(x => x.Mdro).Distinct());
				if (c.Mdro.Length == 0) c.Mdro = null;
				c.Status = HospitalMicroStatus.Completed; c.CompletedAt = nowUtc; c.CompletedByEmployeeId = employeeId;
				var analyte = await EnsureCultureAnalyteAsync(_db, ctx.CompanyId, o!.ServiceId, ct);
				var row = await _db.HospitalLabResults.FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == o.ID && r.AnalyteId == analyte.ID, ct)
					?? _db.HospitalLabResults.Add(new HospitalLabResult { CompanyId = ctx.CompanyId, LabOrderId = o.ID, AnalyteId = analyte.ID }).Entity;
				row.Name = analyte.Name; row.NameEn = analyte.NameEn; row.Unit = null; row.RefLow = null; row.RefHigh = null; row.Decimals = 0; row.SortOrder = 1; row.Value = null;
				row.TextValue = HospitalMicroRules.Summary(growth!, summaries, c.StartedAt, nowUtc, c.Comment);
				row.Flag = growth == HospitalMicroGrowth.Growth ? HospitalLabFlag.High : HospitalLabFlag.Normal;   // "abnormal" on a positive culture
				o.Status = HospitalLabStatus.Resulted; o.ResultedAt = nowUtc; o.ResultedByEmployeeId = employeeId;
				RaiseCritical(o, c);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(c.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Called by the laboratory's verify once the order is verified: the culture is final and locked, and a resistant
		/// organism opens a suspected case for infection control (admitted patients).
		public static async Task AfterVerifyAsync(CrossDbContext db, IHospitalAccessService access, BusinessContext ctx, HospitalLabOrder o, int? employeeId, DateTime nowUtc, CancellationToken ct)
		{
			HospitalMicroCulture? c;
			try { c = await db.Set<HospitalMicroCulture>().FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.LabOrderId == o.ID, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return; }
			if (c == null || c.Status == HospitalMicroStatus.Final) return;
			c.Status = HospitalMicroStatus.Final; c.FinalAt = nowUtc;
			await db.SaveChangesAsync(ct);
			if (c.Mdro == null) return;
			var names = await (from i in db.Set<HospitalMicroIsolate>().AsNoTracking()
							   join g in db.Set<HospitalMicroOrganism>().AsNoTracking() on i.OrganismId equals g.ID
							   where i.CompanyId == ctx.CompanyId && i.CultureId == c.ID && i.Mdro != null
							   select new { g.NameEn, i.Mdro }).ToListAsync(ct);
			var organism = string.Join(", ", names.Select(n => n.NameEn + " (" + n.Mdro + ")").Distinct());
			var ic = new HospitalInfectionService(db, access);
			var caseId = await ic.OpenLabMdroCaseAsync(ctx, c.PatientId, organism, o.CollectedAt ?? o.ReceivedAt ?? o.OrderedAt, "From culture " + o.AccessionNo + " (" + c.Source + ").", employeeId, nowUtc, ct);
			if (caseId != null) { c.InfectionCaseId = caseId; await db.SaveChangesAsync(ct); }
		}

		// ====================================================================================
		// H231: the cumulative antibiogram — final cultures whose specimen was taken in the period
		// ====================================================================================
		public static async Task<IReadOnlyList<HospitalAbgIsolate>> AntibiogramIsolatesAsync(CrossDbContext db, int companyId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
		{
			try
			{
				var rows = await (from c in db.Set<HospitalMicroCulture>().AsNoTracking()
								  join o in db.HospitalLabOrders.AsNoTracking() on c.LabOrderId equals o.ID
								  join i in db.Set<HospitalMicroIsolate>().AsNoTracking() on c.ID equals i.CultureId
								  join g in db.Set<HospitalMicroOrganism>().AsNoTracking() on i.OrganismId equals g.ID
								  where c.CompanyId == companyId && o.CompanyId == companyId && i.CompanyId == companyId && g.CompanyId == companyId
									&& c.Status == HospitalMicroStatus.Final && (o.CollectedAt ?? o.ReceivedAt ?? o.OrderedAt) >= fromUtc && (o.CollectedAt ?? o.ReceivedAt ?? o.OrderedAt) < toUtc
								  select new { i.ID, i.PatientId, i.OrganismId, g.NameEn, g.Group, At = o.CollectedAt ?? o.ReceivedAt ?? o.OrderedAt }).ToListAsync(ct);
				var ids = rows.Select(r => r.ID).ToList();
				var sus = await (from s in db.Set<HospitalMicroSusceptibility>().AsNoTracking()
								 join a in db.Set<HospitalMicroAntibiotic>().AsNoTracking() on s.AntibioticId equals a.ID
								 where s.CompanyId == companyId && ids.Contains(s.IsolateId)
								 select new { s.IsolateId, s.AntibioticId, a.NameEn, s.Result }).ToListAsync(ct);
				var byIso = sus.ToLookup(x => x.IsolateId);
				return rows.Select(r => new HospitalAbgIsolate(r.ID, r.PatientId, r.OrganismId, r.NameEn, r.Group, r.At,
					byIso[r.ID].Select(x => (x.AntibioticId, x.NameEn, x.Result)).ToList())).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalAbgIsolate>(); }
		}

		public async Task<IReadOnlyList<HospitalAbgCell>?> AntibiogramAsync(BusinessContext ctx, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.ClinicalRead, null, ct)) return null;
			return HospitalAntibiogramRules.Build(await AntibiogramIsolatesAsync(_db, ctx.CompanyId, fromUtc, toUtc, ct));
		}

		// ====================================================================================
		// THE STANDARD DICTIONARY (organisms, antibiotics, panels by group) — added where missing, never overwritten
		// ====================================================================================
		public static readonly (string Code, string Ar, string En, string Group, string Gram, bool Aureus)[] StandardOrganisms =
		{
			("SAUR", "المكورات العنقودية الذهبية", "Staphylococcus aureus", HospitalMicroGroup.Staphylococci, "Pos", true),
			("SCON", "المكورات العنقودية سلبية التخثر", "Coagulase-negative Staphylococcus", HospitalMicroGroup.Staphylococci, "Pos", false),
			("SPYO", "العقدية المقيحة", "Streptococcus pyogenes", HospitalMicroGroup.Streptococci, "Pos", false),
			("SPNE", "العقدية الرئوية", "Streptococcus pneumoniae", HospitalMicroGroup.Streptococci, "Pos", false),
			("SAGA", "العقدية القاطعة للدر", "Streptococcus agalactiae", HospitalMicroGroup.Streptococci, "Pos", false),
			("EFAE", "المكورات المعوية البرازية", "Enterococcus faecalis", HospitalMicroGroup.Enterococci, "Pos", false),
			("EFAM", "المكورات المعوية القولونية", "Enterococcus faecium", HospitalMicroGroup.Enterococci, "Pos", false),
			("ECOL", "الإشريكية القولونية", "Escherichia coli", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("KPNE", "الكلبسيلة الرئوية", "Klebsiella pneumoniae", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("PMIR", "المتقلبة الرائعة", "Proteus mirabilis", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("ECLO", "الأمعائية المذرقية", "Enterobacter cloacae", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("SMAR", "السراتية الذابلة", "Serratia marcescens", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("SALM", "السالمونيلا", "Salmonella spp.", HospitalMicroGroup.Enterobacterales, "Neg", false),
			("PAER", "الزائفة الزنجارية", "Pseudomonas aeruginosa", HospitalMicroGroup.Pseudomonas, "Neg", false),
			("ABAU", "الراكدة البومانية", "Acinetobacter baumannii", HospitalMicroGroup.Acinetobacter, "Neg", false),
			("HINF", "المستدمية النزلية", "Haemophilus influenzae", HospitalMicroGroup.Other, "Neg", false),
			("CALB", "المبيضات البيضاء", "Candida albicans", HospitalMicroGroup.Fungi, "NA", false),
			("CAUR", "المبيضات الأذنية", "Candida auris", HospitalMicroGroup.Fungi, "NA", false),
		};

		public static readonly (string Code, string Ar, string En, string Class)[] StandardAntibiotics =
		{
			("PEN", "بنسلين", "Penicillin", "Penicillins"), ("AMP", "أمبيسيلين", "Ampicillin", "Penicillins"),
			("AMC", "أموكسيسيلين/كلافولانات", "Amoxicillin-clavulanate", "Penicillins"), ("TZP", "بيبراسيلين/تازوباكتام", "Piperacillin-tazobactam", "Penicillins"),
			("OXA", "أوكساسيلين", "Oxacillin", "Penicillins"), ("FOX", "سيفوكسيتين", "Cefoxitin", "Cephalosporins"),
			("CXM", "سيفوروكسيم", "Cefuroxime", "Cephalosporins"), ("CRO", "سيفترياكسون", "Ceftriaxone", "Cephalosporins"),
			("CTX", "سيفوتاكسيم", "Cefotaxime", "Cephalosporins"), ("CAZ", "سيفتازيديم", "Ceftazidime", "Cephalosporins"),
			("FEP", "سيفيبيم", "Cefepime", "Cephalosporins"), ("ETP", "إرتابينيم", "Ertapenem", "Carbapenems"),
			("MEM", "ميروبينيم", "Meropenem", "Carbapenems"), ("IPM", "إيميبينيم", "Imipenem", "Carbapenems"),
			("GEN", "جنتاميسين", "Gentamicin", "Aminoglycosides"), ("AMK", "أميكاسين", "Amikacin", "Aminoglycosides"),
			("CIP", "سيبروفلوكساسين", "Ciprofloxacin", "Fluoroquinolones"), ("LVX", "ليفوفلوكساسين", "Levofloxacin", "Fluoroquinolones"),
			("SXT", "تريميثوبريم/سلفاميثوكسازول", "Trimethoprim-sulfamethoxazole", "Folate inhibitors"), ("NIT", "نيتروفورانتوين", "Nitrofurantoin", "Nitrofurans"),
			("FOS", "فوسفوميسين", "Fosfomycin", "Phosphonics"), ("ERY", "إريثرومايسين", "Erythromycin", "Macrolides"),
			("CLI", "كليندامايسين", "Clindamycin", "Lincosamides"), ("VAN", "فانكومايسين", "Vancomycin", "Glycopeptides"),
			("LZD", "لينزوليد", "Linezolid", "Oxazolidinones"), ("TGC", "تيجيسيكلين", "Tigecycline", "Glycylcyclines"),
			("CST", "كوليستين", "Colistin", "Polymyxins"), ("CZA", "سيفتازيديم/أفيباكتام", "Ceftazidime-avibactam", "Cephalosporins"),
			("DOX", "دوكسيسيكلين", "Doxycycline", "Tetracyclines"), ("RIF", "ريفامبيسين", "Rifampicin", "Rifamycins"),
		};

		/// group → (tier-1 codes, tier-2 codes), in report order.
		public static readonly (string Group, string[] Tier1, string[] Tier2)[] StandardPanels =
		{
			(HospitalMicroGroup.Staphylococci, new[] { "PEN", "OXA", "FOX", "ERY", "CLI", "SXT", "GEN" }, new[] { "VAN", "LZD", "DOX", "RIF" }),
			(HospitalMicroGroup.Streptococci, new[] { "PEN", "AMP", "ERY", "CLI", "CRO" }, new[] { "VAN", "LVX", "LZD" }),
			(HospitalMicroGroup.Enterococci, new[] { "AMP", "VAN", "NIT" }, new[] { "LZD", "TGC" }),
			(HospitalMicroGroup.Enterobacterales, new[] { "AMP", "AMC", "CXM", "CRO", "CAZ", "GEN", "CIP", "SXT", "NIT" }, new[] { "TZP", "FEP", "ETP", "MEM", "AMK", "FOS", "CZA", "TGC", "CST" }),
			(HospitalMicroGroup.Pseudomonas, new[] { "TZP", "CAZ", "FEP", "GEN", "CIP" }, new[] { "MEM", "IPM", "AMK", "CZA", "CST" }),
			(HospitalMicroGroup.Acinetobacter, new[] { "TZP", "CAZ", "GEN", "CIP", "SXT" }, new[] { "MEM", "IPM", "AMK", "TGC", "CST" }),
		};

		public async Task<HospitalResult> SeedStandardAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				int added = 0;
				var orgCodes = (await _db.Set<HospitalMicroOrganism>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.Code).ToListAsync(ct)).ToHashSet();
				foreach (var o in StandardOrganisms.Where(o => !orgCodes.Contains(o.Code)))
				{ _db.Add(new HospitalMicroOrganism { CompanyId = ctx.CompanyId, Code = o.Code, Name = o.Ar, NameEn = o.En, Group = o.Group, Gram = o.Gram, IsAureus = o.Aureus }); added++; }
				var abxCodes = (await _db.Set<HospitalMicroAntibiotic>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.Code).ToListAsync(ct)).ToHashSet();
				foreach (var a in StandardAntibiotics.Where(a => !abxCodes.Contains(a.Code)))
				{ _db.Add(new HospitalMicroAntibiotic { CompanyId = ctx.CompanyId, Code = a.Code, Name = a.Ar, NameEn = a.En, Class = a.Class }); added++; }
				await _db.SaveChangesAsync(ct);
				var abx = await _db.Set<HospitalMicroAntibiotic>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId).ToDictionaryAsync(x => x.Code, x => x.ID, ct);
				var have = (await _db.Set<HospitalMicroPanelItem>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => new { x.Group, x.AntibioticId }).ToListAsync(ct)).Select(x => (x.Group, x.AntibioticId)).ToHashSet();
				foreach (var (group, t1, t2) in StandardPanels)
				{
					int n = 0;
					foreach (var (code, tier) in t1.Select(c => (c, 1)).Concat(t2.Select(c => (c, 2))))
					{
						n++;
						if (!abx.TryGetValue(code, out var id) || have.Contains((group, id))) continue;
						_db.Add(new HospitalMicroPanelItem { CompanyId = ctx.CompanyId, Group = group, AntibioticId = id, Tier = tier, SortOrder = n }); added++;
					}
				}
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(added);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}
	}
}
