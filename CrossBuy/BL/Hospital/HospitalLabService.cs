using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL LABORATORY SERVICE (H8).
	//
	//   · A lab order is born from a signed visit's lab order line, one to one (SyncOrdersAsync is
	//     idempotent and runs before the worklist reads, so a visit signed before the lab existed is
	//     picked up too). It gets an accession number LAB-000001…
	//   · Ordered → Collected → Received → Resulted → Verified, each step stamped with who and when.
	//     Results are entered only on a received specimen and changed only until verified.
	//   · Verification is the SECOND step: `lab-verify`, and never by the person who entered the result
	//     unless they administer the hospital. Only a verified result reaches the doctor and the file.
	//   · Each value is flagged against its analyte (L/H, LL/HH past a critical limit); a critical value
	//     raises the order's flag until someone records that the doctor was told.
	// ==========================================================================================

	public sealed class HospitalLabAnalyteInput
	{
		public int? Id { get; set; }
		public int ServiceId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string? Unit { get; set; }
		public decimal? RefLow { get; set; }
		public decimal? RefHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
		public int Decimals { get; set; } = 1;
		public bool IsActive { get; set; } = true;
		public decimal? DeltaPercent { get; set; }   // H233
		public bool AutoVerify { get; set; }          // H233
	}

	/// H233: a reference range for a sex and an age band; the band is given in days, months or years.
	public sealed class HospitalLabRangeInput
	{
		public int AnalyteId { get; set; }
		public string? Sex { get; set; }
		public decimal AgeFrom { get; set; }
		public decimal? AgeTo { get; set; }
		/// days | months | years
		public string? AgeUnit { get; set; }
		public decimal? RefLow { get; set; }
		public decimal? RefHigh { get; set; }
		public decimal? CriticalLow { get; set; }
		public decimal? CriticalHigh { get; set; }
	}

	public sealed class HospitalLabTestInput
	{
		public string Specimen { get; set; } = "";
		public string? SpecimenEn { get; set; }
		public string Section { get; set; } = "";
		public string? SectionEn { get; set; }
		public bool IsCulture { get; set; }   // H230
	}

	public sealed record HospitalLabRow(int Id, string AccessionNo, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Code, string Name, string NameEn, string Priority, string Status, DateTime OrderedAt, string? From, string? FromEn, bool IsCritical, bool CriticalOpen);

	public sealed record HospitalLabStats(int OrdersToday, int? AvgTurnaroundMinutes, int AwaitingVerification, int OpenCriticals, IReadOnlyDictionary<string, int> Pipeline);

	/// One analyte on the result screen: its definition, the value entered (if any) and the patient's previous verified value.
	public sealed record HospitalLabLine(HospitalLabAnalyte Analyte, HospitalLabResult? Result, decimal? Previous);

	public sealed record HospitalLabOrderView(HospitalLabOrder Order, HospitalPatient Patient, HospitalLabTest? Setup, IReadOnlyList<HospitalLabLine> Lines,
		IReadOnlyDictionary<int, string> People);

	/// A patient's lab results for the file and the consultation: the verified orders (only these reach a doctor)
	/// and how many are still on the bench.
	public sealed record HospitalLabHistory(IReadOnlyList<(HospitalLabOrder Order, IReadOnlyList<HospitalLabResult> Results)> Verified, int Pending, bool Compact);

	public sealed record HospitalLabSetupRow(HospitalService Service, HospitalLabTest? Setup, IReadOnlyList<HospitalLabAnalyte> Analytes);

	public sealed class HospitalLabService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;

		public HospitalLabService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ====================================================================================
		// THE RULE (pure)
		// ====================================================================================

		public static string Flag(decimal? v, decimal? refLow, decimal? refHigh, decimal? critLow, decimal? critHigh)
		{
			if (v is not decimal x) return HospitalLabFlag.Normal;
			if (critLow is decimal cl && x < cl) return HospitalLabFlag.CriticalLow;
			if (critHigh is decimal ch && x > ch) return HospitalLabFlag.CriticalHigh;
			if (refLow is decimal rl && x < rl) return HospitalLabFlag.Low;
			if (refHigh is decimal rh && x > rh) return HospitalLabFlag.High;
			return HospitalLabFlag.Normal;
		}

		public static bool IsCriticalFlag(string flag) => flag is HospitalLabFlag.CriticalLow or HospitalLabFlag.CriticalHigh;

		// ====================================================================================
		// ORDERS FROM VISITS
		// ====================================================================================

		private async Task<string> NextAccessionAsync(BusinessContext ctx, CancellationToken ct)
		{
			var nos = await _db.HospitalLabOrders.Where(o => o.CompanyId == ctx.CompanyId && o.AccessionNo.StartsWith("LAB-")).Select(o => o.AccessionNo).ToListAsync(ct);
			int max = 0;
			foreach (var n in nos) if (int.TryParse(n.AsSpan(4), out var v) && v > max) max = v;
			return "LAB-" + (max + 1).ToString("000000");
		}

		/// Creates the lab order of every signed visit's lab order line that has none yet. Idempotent.
		public async Task<int> SyncOrdersAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var pending = await (from o in _db.HospitalEncounterOrders.AsNoTracking()
								 join e in _db.HospitalEncounters.AsNoTracking() on o.EncounterId equals e.ID
								 where o.CompanyId == ctx.CompanyId && e.CompanyId == ctx.CompanyId && o.Category == HospitalServiceCategories.Lab && e.Status == HospitalEncounterStatus.Signed
									   && !_db.HospitalLabOrders.Any(l => l.CompanyId == ctx.CompanyId && l.EncounterOrderId == o.ID)
								 orderby o.ID
								 select new { o, e.PatientId, e.SignedAt, e.SignedByEmployeeId, e.ID }).ToListAsync(ct);
			if (pending.Count == 0) return 0;
			var next = await NextAccessionAsync(ctx, ct);
			int n = int.Parse(next.AsSpan(4));
			foreach (var p in pending)
				_db.HospitalLabOrders.Add(new HospitalLabOrder
				{
					CompanyId = ctx.CompanyId, AccessionNo = "LAB-" + (n++).ToString("000000"), PatientId = p.PatientId, EncounterId = p.ID, EncounterOrderId = p.o.ID,
					ServiceId = p.o.ServiceId, Code = p.o.Code, Name = p.o.Name, NameEn = p.o.NameEn, OrderedByEmployeeId = p.SignedByEmployeeId,
					Priority = HospitalLabPriority.Routine, Status = HospitalLabStatus.Ordered, OrderedAt = p.SignedAt ?? DateTime.UtcNow,
				});
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return 0; }   // another request created them first
			return pending.Count;
		}

		// ====================================================================================
		// READS
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalLabRow>> WorklistAsync(BusinessContext ctx, string? status, int? patientId = null, CancellationToken ct = default)
		{
			var q = from l in _db.HospitalLabOrders.AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on l.PatientId equals p.ID
					join e0 in _db.HospitalEncounters.AsNoTracking() on l.EncounterId equals (int?)e0.ID into es
					from e in es.DefaultIfEmpty()
					join d0 in _db.HospitalPractitioners.AsNoTracking() on e.PractitionerId equals d0.ID into ds
					from d in ds.DefaultIfEmpty()
					where l.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
					select new { l, p, d };
			if (status != null) q = q.Where(x => x.l.Status == status);
			else q = q.Where(x => x.l.Status != HospitalLabStatus.Verified || x.l.VerifiedAt >= DateTime.UtcNow.AddDays(-2));
			if (patientId != null) q = q.Where(x => x.l.PatientId == patientId);
			var rows = await q.OrderByDescending(x => x.l.Priority == HospitalLabPriority.Stat).ThenByDescending(x => x.l.Priority == HospitalLabPriority.Urgent).ThenByDescending(x => x.l.OrderedAt).Take(300).ToListAsync(ct);
			return rows.Select(x => new HospitalLabRow(x.l.ID, x.l.AccessionNo, x.p.ID, x.p.Name, x.p.NameEn, x.p.Mrn, x.p.PhotoPath, x.l.Code, x.l.Name, x.l.NameEn, x.l.Priority, x.l.Status, x.l.OrderedAt,
				x.d?.Name, x.d?.NameEn, x.l.IsCritical, x.l.IsCritical && x.l.CriticalAcknowledgedAt == null)).ToList();
		}

		public async Task<HospitalLabStats> StatsAsync(BusinessContext ctx, DateTime dayStartUtc, CancellationToken ct = default)
		{
			var all = _db.HospitalLabOrders.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId);
			int today = await all.CountAsync(l => l.OrderedAt >= dayStartUtc, ct);
			var done = await all.Where(l => l.VerifiedAt >= dayStartUtc).Select(l => new { l.OrderedAt, l.VerifiedAt }).ToListAsync(ct);
			int? tat = done.Count == 0 ? null : (int)Math.Round(done.Average(d => (d.VerifiedAt!.Value - d.OrderedAt).TotalMinutes));
			int awaiting = await all.CountAsync(l => l.Status == HospitalLabStatus.Resulted, ct);
			int critical = await all.CountAsync(l => l.IsCritical && l.CriticalAcknowledgedAt == null, ct);
			var pipe = await all.Where(l => l.Status != HospitalLabStatus.Verified || l.VerifiedAt >= dayStartUtc).GroupBy(l => l.Status).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
			return new HospitalLabStats(today, tat, awaiting, critical, HospitalLabStatus.All.ToDictionary(s => s, s => pipe.GetValueOrDefault(s)));
		}

		public async Task<HospitalLabOrderView?> OrderAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			var o = await _db.HospitalLabOrders.AsNoTracking().FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			if (o == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstAsync(x => x.ID == o.PatientId && x.CompanyId == ctx.CompanyId, ct);
			var setup = await _db.HospitalLabTests.AsNoTracking().FirstOrDefaultAsync(t => t.CompanyId == ctx.CompanyId && t.ServiceId == o.ServiceId, ct);
			var results = await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == o.ID).ToListAsync(ct);
			var analytes = await _db.HospitalLabAnalytes.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.ServiceId == o.ServiceId && (a.IsActive || results.Select(r => r.AnalyteId).Contains(a.ID)))
				.OrderBy(a => a.SortOrder).ThenBy(a => a.ID).ToListAsync(ct);
			// The previous VERIFIED value of each analyte for this patient (an unverified one is not a result yet).
			var ids = analytes.Select(a => a.ID).ToList();
			var prev = await (from r in _db.HospitalLabResults.AsNoTracking()
							  join l in _db.HospitalLabOrders.AsNoTracking() on r.LabOrderId equals l.ID
							  where r.CompanyId == ctx.CompanyId && l.CompanyId == ctx.CompanyId && l.PatientId == o.PatientId && l.ID != o.ID && l.Status == HospitalLabStatus.Verified && ids.Contains(r.AnalyteId)
							  orderby l.VerifiedAt descending
							  select new { r.AnalyteId, r.Value }).ToListAsync(ct);
			var lines = analytes.Select(a => new HospitalLabLine(a, results.FirstOrDefault(r => r.AnalyteId == a.ID), prev.FirstOrDefault(x => x.AnalyteId == a.ID)?.Value)).ToList();
			var empIds = new[] { o.OrderedByEmployeeId, o.CollectedByEmployeeId, o.ReceivedByEmployeeId, o.ResultedByEmployeeId, o.VerifiedByEmployeeId, o.CriticalAcknowledgedByEmployeeId }
				.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var people = await _db.Employee.AsNoTracking().Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
				.ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
			return new HospitalLabOrderView(o, p, setup, lines, people);
		}

		/// A patient's verified lab orders, newest first (the patient file and the consultation).
		public async Task<IReadOnlyList<(HospitalLabOrder Order, IReadOnlyList<HospitalLabResult> Results)>> VerifiedForPatientAsync(BusinessContext ctx, int patientId, int? encounterId = null, CancellationToken ct = default)
		{
			var q = _db.HospitalLabOrders.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.PatientId == patientId && l.Status == HospitalLabStatus.Verified);
			if (encounterId != null) q = q.Where(l => l.EncounterId == encounterId);
			var orders = await q.OrderByDescending(l => l.VerifiedAt).Take(50).ToListAsync(ct);
			var ids = orders.Select(o => o.ID).ToList();
			var res = await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && ids.Contains(r.LabOrderId)).OrderBy(r => r.SortOrder).ToListAsync(ct);
			return orders.Select(o => (o, (IReadOnlyList<HospitalLabResult>)res.Where(r => r.LabOrderId == o.ID).ToList())).ToList();
		}

		public async Task<HospitalLabHistory> HistoryAsync(BusinessContext ctx, int patientId, bool compact, CancellationToken ct = default)
		{
			var verified = await VerifiedForPatientAsync(ctx, patientId, null, ct);
			int pending = await _db.HospitalLabOrders.CountAsync(l => l.CompanyId == ctx.CompanyId && l.PatientId == patientId && l.Status != HospitalLabStatus.Verified, ct);
			return new HospitalLabHistory(compact ? verified.Take(5).ToList() : verified, pending, compact);
		}

		// ====================================================================================
		// THE BENCH
		// ====================================================================================

		private async Task<(HospitalLabOrder? Order, HospitalResult? Fail)> LoadAsync(BusinessContext ctx, int id, string action, CancellationToken ct)
		{
			if (!await Can(ctx, action, ct)) return (null, HospitalResult.Fail("forbidden"));
			var o = await _db.HospitalLabOrders.FirstOrDefaultAsync(l => l.ID == id && l.CompanyId == ctx.CompanyId, ct);
			return o == null ? (null, HospitalResult.Fail("not_found")) : (o, null);
		}

		/// H206: the specimen is collected at the bedside with the patient's wristband and the tube's label scanned (the label
		/// prints the accession number) — or, when that cannot be done, with the reason written. A wrong patient or a wrong
		/// tube stops the collection, and the refused scan is kept.
		public async Task<HospitalResult> CollectAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default,
			string? wristband = null, string? tube = null, string? scanOverride = null)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Lab, ct); if (fail != null) return fail;
			if (o!.Status != HospitalLabStatus.Ordered) return HospitalResult.Fail("The specimen was already collected");
			var mrn = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId).Select(p => p.Mrn).FirstAsync(ct);
			HospitalBedsideScan scan;
			if (HospitalBedsideScanRules.Scanned(wristband, tube))
			{
				if (HospitalBedsideScanRules.SpecimenRefusal(mrn, o.AccessionNo, wristband!, tube!) is string why)
				{
					// the refused scan is kept on its own; the order is not touched
					_db.Entry(o).State = EntityState.Unchanged;
					_db.Add(HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Specimen, o.PatientId, null, o.ID, wristband, tube, null, HospitalBedsideScanOutcome.Refused, why, employeeId, nowUtc));
					try { await _db.SaveChangesAsync(ct); } catch (DbUpdateException) { }
					return HospitalResult.Fail(why);
				}
				scan = HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Specimen, o.PatientId, null, o.ID, wristband, tube, null, HospitalBedsideScanOutcome.Matched, null, employeeId, nowUtc);
			}
			else if (string.IsNullOrWhiteSpace(scanOverride)) return HospitalResult.Fail(HospitalBedsideScanRules.ScanOrReasonSpecimen);
			else scan = HospitalBedsideScanCheck.Row(ctx, HospitalBedsideScanKind.Specimen, o.PatientId, null, o.ID, wristband, tube, null, HospitalBedsideScanOutcome.Override, scanOverride, employeeId, nowUtc);
			o.Status = HospitalLabStatus.Collected; o.CollectedAt = nowUtc; o.CollectedByEmployeeId = employeeId;
			scan.DoneId = o.ID;
			_db.Add(scan);
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Lab, ct); if (fail != null) return fail;
			if (o!.Status != HospitalLabStatus.Collected) return HospitalResult.Fail("Collect the specimen before receiving it in the lab");
			o.Status = HospitalLabStatus.Received; o.ReceivedAt = nowUtc; o.ReceivedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		public async Task<HospitalResult> SetPriorityAsync(BusinessContext ctx, int id, string priority, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Lab, ct); if (fail != null) return fail;
			if (!HospitalLabPriority.All.Contains(priority)) return HospitalResult.Fail("Unknown priority");
			if (o!.Status == HospitalLabStatus.Verified) return HospitalResult.Fail("This result is verified and locked");
			o.Priority = priority; await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		/// Enters (or corrects) the results of a received specimen. Values are keyed by analyte id; an empty
		/// value is left unrecorded. Every analyte must have a value before the order counts as resulted.
		public async Task<HospitalResult> SaveResultsAsync(BusinessContext ctx, int id, IReadOnlyDictionary<int, string?> values, string? comment, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Lab, ct); if (fail != null) return fail;
			if (o!.Status == HospitalLabStatus.Verified) return HospitalResult.Fail("This result is verified and locked");
			if (o.Status is HospitalLabStatus.Ordered or HospitalLabStatus.Collected) return HospitalResult.Fail("Receive the specimen in the lab before entering results");
			if (await HospitalMicroService.IsCultureAsync(_db, ctx.CompanyId, o.ServiceId, ct)) return HospitalResult.Fail("A culture is reported from its microbiology panel");   // H230
			// H232: no patient result while the test's quality control is out — run a control that is accepted first
			if (await HospitalLabQcService.OutOfControlAsync(_db, ctx.CompanyId, o.ServiceId, ct) is { Count: > 0 } outQc)
				return HospitalResult.Fail("Quality control is out for {0}: run an accepted control before patient results", string.Join(", ", outQc));
			var analytes = await _db.HospitalLabAnalytes.Where(a => a.CompanyId == ctx.CompanyId && a.ServiceId == o.ServiceId && a.IsActive).OrderBy(a => a.SortOrder).ThenBy(a => a.ID).ToListAsync(ct);
			if (analytes.Count == 0) return HospitalResult.Fail("This test has no analytes yet. Set it up in lab tests first");
			if (values.Keys.Any(k => analytes.All(a => a.ID != k))) return HospitalResult.Fail("not_found");

			var existing = await _db.HospitalLabResults.Where(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == o.ID).ToListAsync(ct);
			bool critical = false; int filled = 0, order = 0;
			// H233: the patient's own range (age and sex at collection), the previous verified value for the delta check
			var aIds = analytes.Select(x => x.ID).ToList();
			var pat = await _db.HospitalPatients.AsNoTracking().Where(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId).Select(p => new { p.Sex, p.BirthDate }).FirstOrDefaultAsync(ct);
			int? ageDays = HospitalLabAutoRules.AgeDays(pat?.BirthDate, (o.CollectedAt ?? nowUtc).ToLocalTime());
			List<HospitalLabRefRange> ranges;
			try { ranges = await _db.Set<HospitalLabRefRange>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && aIds.Contains(r.AnalyteId)).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { ranges = new(); }
			var since = nowUtc.AddDays(-HospitalLabAutoRules.DeltaWindowDays);
			var previous = (await (from r in _db.HospitalLabResults.AsNoTracking()
								   join po in _db.HospitalLabOrders.AsNoTracking() on r.LabOrderId equals po.ID
								   where r.CompanyId == ctx.CompanyId && po.CompanyId == ctx.CompanyId && po.PatientId == o.PatientId && po.ID != o.ID
									 && po.Status == HospitalLabStatus.Verified && po.VerifiedAt >= since && aIds.Contains(r.AnalyteId) && r.Value != null
								   select new { r.AnalyteId, r.Value, po.VerifiedAt }).ToListAsync(ct))
				.GroupBy(x => x.AnalyteId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.VerifiedAt).First().Value);
			var auto = new List<(bool AutoVerify, string Flag, bool Delta, bool HasValue)>();
			foreach (var a in analytes)
			{
				order++;
				var raw = values.GetValueOrDefault(a.ID)?.Trim();
				var row = existing.FirstOrDefault(r => r.AnalyteId == a.ID);
				if (string.IsNullOrEmpty(raw)) { if (row != null) _db.HospitalLabResults.Remove(row); continue; }
				decimal? num = decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? Math.Round(d, a.Decimals) : null;
				if (num == null && a.Unit != null && (a.RefLow != null || a.RefHigh != null)) return HospitalResult.Fail("{0} needs a number", DisplayName.Of(a.Name, a.NameEn));
				var lim = HospitalLabAutoRules.Limits(a, ranges.Where(r => r.AnalyteId == a.ID), pat?.Sex, ageDays);   // H233
				var flag = Flag(num, lim.RefLow, lim.RefHigh, lim.CriticalLow, lim.CriticalHigh);
				critical |= IsCriticalFlag(flag);
				row ??= _db.HospitalLabResults.Add(new HospitalLabResult { CompanyId = ctx.CompanyId, LabOrderId = o.ID, AnalyteId = a.ID }).Entity;
				row.Name = a.Name; row.NameEn = a.NameEn; row.Unit = a.Unit; row.RefLow = lim.RefLow; row.RefHigh = lim.RefHigh; row.Decimals = a.Decimals; row.SortOrder = order;
				row.Value = num; row.TextValue = num == null ? raw[..Math.Min(200, raw.Length)] : null; row.Flag = flag;
				row.DeltaPrevious = previous.GetValueOrDefault(a.ID); row.DeltaFailed = HospitalLabAutoRules.DeltaFailed(num, row.DeltaPrevious, a.DeltaPercent);   // H233
				auto.Add((a.AutoVerify, flag, row.DeltaFailed, true));
				filled++;
			}
			o.Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(1000, comment.Trim().Length)];
			if (filled == analytes.Count) { o.Status = HospitalLabStatus.Resulted; o.ResultedAt = nowUtc; o.ResultedByEmployeeId = employeeId; }
			else if (o.Status == HospitalLabStatus.Resulted) { o.Status = HospitalLabStatus.Received; o.ResultedAt = null; o.ResultedByEmployeeId = null; }
			// A critical value that appears raises the flag; a corrected one that is no longer critical lowers it.
			if (critical && !o.IsCritical) { o.IsCritical = true; o.CriticalAcknowledgedAt = null; o.CriticalAcknowledgedByEmployeeId = null; o.CriticalNote = null; }
			else if (!critical) { o.IsCritical = false; o.CriticalAcknowledgedAt = null; o.CriticalAcknowledgedByEmployeeId = null; o.CriticalNote = null; }
			// H233: autoverification — every analyte allows it, every value within its range, no delta, nothing critical
			bool autoVerified = o.Status == HospitalLabStatus.Resulted && !o.IsCritical && auto.Count == analytes.Count && HospitalLabAutoRules.CanAutoVerify(auto);
			o.AutoVerified = autoVerified;
			if (autoVerified) { o.Status = HospitalLabStatus.Verified; o.VerifiedAt = nowUtc; o.VerifiedByEmployeeId = null; }
			await _db.SaveChangesAsync(ct);
			if (autoVerified) await HospitalSmsService.QueueLabReadyAsync(_db, ctx.CompanyId, o.ID, employeeId, nowUtc, ct);
			return HospitalResult.Success(o.ID);
		}

		// ---- H233: reference ranges by age and sex (the administrator's) ----
		public async Task<IReadOnlyList<HospitalLabRefRange>> RangesAsync(BusinessContext ctx, int analyteId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalLabRefRange>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.AnalyteId == analyteId).OrderBy(r => r.Sex).ThenBy(r => r.AgeFromDays).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return Array.Empty<HospitalLabRefRange>(); }
		}

		public static int Days(decimal v, string? unit) => (int)Math.Round(unit switch { "years" => v * 365.25m, "months" => v * 30.4375m, _ => v }, MidpointRounding.AwayFromZero);

		public async Task<HospitalResult> SaveRangeAsync(BusinessContext ctx, HospitalLabRangeInput i, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			string? sex = string.IsNullOrWhiteSpace(i.Sex) ? null : i.Sex.Trim().ToUpperInvariant();
			int from = Days(i.AgeFrom, i.AgeUnit); int? to = i.AgeTo is decimal t ? Days(t, i.AgeUnit) : null;
			if (HospitalLabAutoRules.RangeRefusal(sex, from, to, i.RefLow, i.RefHigh) is string why) return HospitalResult.Fail(why);
			if (!await _db.HospitalLabAnalytes.AnyAsync(a => a.ID == i.AnalyteId && a.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var row = new HospitalLabRefRange { CompanyId = ctx.CompanyId, AnalyteId = i.AnalyteId, Sex = sex, AgeFromDays = from, AgeToDays = to, RefLow = i.RefLow, RefHigh = i.RefHigh, CriticalLow = i.CriticalLow, CriticalHigh = i.CriticalHigh };
				_db.Add(row); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return HospitalResult.Fail("Reference ranges are not installed yet (hospital_h233_lab_ranges_autoverify.sql)."); }
		}

		public async Task<HospitalResult> DeleteRangeAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var row = await _db.Set<HospitalLabRefRange>().FirstOrDefaultAsync(r => r.ID == id && r.CompanyId == ctx.CompanyId, ct);
			if (row == null) return HospitalResult.Fail("not_found");
			_db.Remove(row); await _db.SaveChangesAsync(ct); return HospitalResult.Success(id);
		}

		/// The second step: a specialist verifies, and only then does the doctor see the result.
		public async Task<HospitalResult> VerifyAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.LabVerify, ct); if (fail != null) return fail;
			if (o!.Status == HospitalLabStatus.Verified) return HospitalResult.Fail("This result is verified and locked");
			if (o.Status != HospitalLabStatus.Resulted) return HospitalResult.Fail("Enter every result before verifying");
			if (employeeId != null && o.ResultedByEmployeeId == employeeId && !await Can(ctx, HospitalActions.Manage, ct))
				return HospitalResult.Fail("A different person verifies the results they did not enter");
			o.Status = HospitalLabStatus.Verified; o.VerifiedAt = nowUtc; o.VerifiedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			await HospitalMicroService.AfterVerifyAsync(_db, _access, ctx, o, employeeId, nowUtc, ct);   // H230: the culture is final; a resistant organism goes to infection control
			await HospitalSmsService.QueueLabReadyAsync(_db, ctx.CompanyId, o.ID, employeeId, nowUtc, ct);   // H70: "your results are ready"
			return HospitalResult.Success(o.ID);
		}

		/// Records that the doctor was told of a critical value — who called, when, and a note (whom they spoke to).
		public async Task<HospitalResult> AcknowledgeCriticalAsync(BusinessContext ctx, int id, string? note, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var (o, fail) = await LoadAsync(ctx, id, HospitalActions.Lab, ct); if (fail != null) return fail;
			if (!o!.IsCritical) return HospitalResult.Fail("This order has no critical value");
			if (o.CriticalAcknowledgedAt != null) return HospitalResult.Fail("Already recorded");
			if (string.IsNullOrWhiteSpace(note)) return HospitalResult.Fail("Write whom you told");
			o.CriticalAcknowledgedAt = nowUtc; o.CriticalAcknowledgedByEmployeeId = employeeId; o.CriticalNote = note.Trim()[..Math.Min(400, note.Trim().Length)];
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(o.ID);
		}

		// ====================================================================================
		// SETUP: tests and analytes
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalLabSetupRow>> SetupAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Lab && s.IsActive).OrderBy(s => s.SortOrder).ThenBy(s => s.Code).ToListAsync(ct);
			var tests = await _db.HospitalLabTests.AsNoTracking().Where(t => t.CompanyId == ctx.CompanyId).ToListAsync(ct);
			var analytes = await _db.HospitalLabAnalytes.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId).OrderBy(a => a.SortOrder).ThenBy(a => a.ID).ToListAsync(ct);
			return services.Select(s => new HospitalLabSetupRow(s, tests.FirstOrDefault(t => t.ServiceId == s.ID), analytes.Where(a => a.ServiceId == s.ID).ToList())).ToList();
		}

		public async Task<HospitalResult> SaveTestAsync(BusinessContext ctx, int serviceId, HospitalLabTestInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (string.IsNullOrWhiteSpace(input.Specimen) || string.IsNullOrWhiteSpace(input.Section)) return HospitalResult.Fail("Enter the specimen and the section");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == serviceId && s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Lab, ct)) return HospitalResult.Fail("not_found");
			var t = await _db.HospitalLabTests.FirstOrDefaultAsync(x => x.CompanyId == ctx.CompanyId && x.ServiceId == serviceId, ct);
			if (t == null) { t = new HospitalLabTest { CompanyId = ctx.CompanyId, ServiceId = serviceId }; _db.HospitalLabTests.Add(t); }
			t.Specimen = input.Specimen.Trim(); t.SpecimenEn = input.SpecimenEn?.Trim(); t.Section = input.Section.Trim(); t.SectionEn = input.SectionEn?.Trim();
			t.IsCulture = input.IsCulture;   // H230
			await _db.SaveChangesAsync(ct);
			if (t.IsCulture) await HospitalMicroService.EnsureCultureAnalyteAsync(_db, ctx.CompanyId, serviceId, ct);
			return HospitalResult.Success(t.ID);
		}

		public async Task<HospitalResult> SaveAnalyteAsync(BusinessContext ctx, HospitalLabAnalyteInput input, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim().ToUpperInvariant();
			if (code.Length is 0 or > 30) return HospitalResult.Fail("Enter a code of up to 30 characters");
			if (string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the name in both languages");
			if (input.RefLow > input.RefHigh) return HospitalResult.Fail("The low limit is above the high limit");
			if (input.CriticalLow > input.RefLow || input.CriticalHigh < input.RefHigh) return HospitalResult.Fail("A critical limit lies outside the reference range");
			if (input.Decimals is < 0 or > 4) return HospitalResult.Fail("Decimals are 0 to 4");
			if (!await _db.HospitalServices.AnyAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Lab, ct)) return HospitalResult.Fail("not_found");
			int self = input.Id ?? 0;
			if (await _db.HospitalLabAnalytes.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.ServiceId == input.ServiceId && a.Code == code && a.ID != self, ct)) return HospitalResult.Fail("Another analyte of this test uses this code");
			HospitalLabAnalyte? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalLabAnalytes.FirstOrDefaultAsync(a => a.ID == id && a.CompanyId == ctx.CompanyId && a.ServiceId == input.ServiceId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
			}
			else
			{
				int sort = (await _db.HospitalLabAnalytes.Where(a => a.CompanyId == ctx.CompanyId && a.ServiceId == input.ServiceId).MaxAsync(a => (int?)a.SortOrder, ct) ?? 0) + 10;
				row = new HospitalLabAnalyte { CompanyId = ctx.CompanyId, ServiceId = input.ServiceId, SortOrder = sort };
				_db.HospitalLabAnalytes.Add(row);
			}
			row.Code = code; row.Name = input.Name.Trim(); row.NameEn = input.NameEn.Trim(); row.Unit = string.IsNullOrWhiteSpace(input.Unit) ? null : input.Unit.Trim();
			row.RefLow = input.RefLow; row.RefHigh = input.RefHigh; row.CriticalLow = input.CriticalLow; row.CriticalHigh = input.CriticalHigh; row.Decimals = input.Decimals; row.IsActive = input.IsActive;
			if (input.DeltaPercent is < 0 or > 1000) return HospitalResult.Fail("The delta limit is 0 to 1000 %");
			row.DeltaPercent = input.DeltaPercent is > 0 ? input.DeltaPercent : null; row.AutoVerify = input.AutoVerify;   // H233
			await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID);
		}

		/// The common panels for the starter lab services (codes from the billing seed), with adult ranges.
		/// Ranges are typical textbook values to be confirmed by the lab director, not a clinical standard.
		public async Task<HospitalResult> SeedStandardAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.Category == HospitalServiceCategories.Lab).ToDictionaryAsync(s => s.Code, s => s.ID, ct);
			var haveTests = (await _db.HospitalLabTests.Where(t => t.CompanyId == ctx.CompanyId).Select(t => t.ServiceId).ToListAsync(ct)).ToHashSet();
			var haveAnalytes = (await _db.HospitalLabAnalytes.Where(a => a.CompanyId == ctx.CompanyId).Select(a => a.ServiceId + "|" + a.Code).ToListAsync(ct)).ToHashSet();
			int added = 0;
			foreach (var (code, spec, specEn, sec, secEn, analytes) in StandardPanels)
			{
				if (!services.TryGetValue(code, out var sid)) continue;
				if (!haveTests.Contains(sid)) _db.HospitalLabTests.Add(new HospitalLabTest { CompanyId = ctx.CompanyId, ServiceId = sid, Specimen = spec, SpecimenEn = specEn, Section = sec, SectionEn = secEn });
				int sort = 0;
				foreach (var a in analytes)
				{
					sort += 10;
					if (haveAnalytes.Contains(sid + "|" + a.Code)) continue;
					_db.HospitalLabAnalytes.Add(new HospitalLabAnalyte { CompanyId = ctx.CompanyId, ServiceId = sid, Code = a.Code, Name = a.Ar, NameEn = a.En, Unit = a.Unit, RefLow = a.Lo, RefHigh = a.Hi, CriticalLow = a.CLo, CriticalHigh = a.CHi, Decimals = a.Dec, SortOrder = sort });
					added++;
				}
			}
			await _db.SaveChangesAsync(ct);
			// H233: the standard bands by age and sex, for analytes that have none yet
			try
			{
				var codes = HospitalLabAutoRules.Standard.Select(x => x.Code).Distinct().ToList();
				var targets = await _db.HospitalLabAnalytes.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && codes.Contains(a.Code)).Select(a => new { a.ID, a.Code }).ToListAsync(ct);
				var withRanges = (await _db.Set<HospitalLabRefRange>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId).Select(r => r.AnalyteId).Distinct().ToListAsync(ct)).ToHashSet();
				foreach (var t in targets.Where(t => !withRanges.Contains(t.ID)))
					foreach (var b in HospitalLabAutoRules.Standard.Where(x => x.Code == t.Code))
					{ _db.Add(new HospitalLabRefRange { CompanyId = ctx.CompanyId, AnalyteId = t.ID, Sex = b.Sex, AgeFromDays = b.From, AgeToDays = b.To, RefLow = b.Low, RefHigh = b.High }); added++; }
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { }
			return HospitalResult.Success(added);
		}

		private sealed record A(string Code, string Ar, string En, string? Unit, decimal? Lo, decimal? Hi, decimal? CLo, decimal? CHi, int Dec);
		private static readonly (string Code, string Spec, string SpecEn, string Sec, string SecEn, A[] Analytes)[] StandardPanels =
		{
			("LAB-CBC", "دم كامل EDTA", "Whole blood EDTA", "أمراض الدم", "Haematology", new[] {
				new A("WBC", "كريات الدم البيضاء", "White blood cells", "×10⁹/L", 4.0m, 11.0m, 2.0m, 30.0m, 1),
				new A("RBC", "كريات الدم الحمراء", "Red blood cells", "×10¹²/L", 4.2m, 5.9m, null, null, 2),
				new A("HGB", "الهيموجلوبين", "Haemoglobin", "g/dL", 12.0m, 17.5m, 7.0m, 20.0m, 1),
				new A("HCT", "الهيماتوكريت", "Haematocrit", "%", 36m, 52m, 20m, 60m, 1),
				new A("PLT", "الصفائح الدموية", "Platelets", "×10⁹/L", 150m, 400m, 50m, 1000m, 0),
				new A("NEUT", "العدلات", "Neutrophils", "%", 40m, 75m, null, null, 0) }),
			("LAB-HBA1C", "دم كامل EDTA", "Whole blood EDTA", "الكيمياء الحيوية", "Clinical chemistry", new[] { new A("HBA1C", "السكر التراكمي", "HbA1c", "%", 4.0m, 5.6m, null, null, 1) }),
			("LAB-FBS", "مصل", "Serum", "الكيمياء الحيوية", "Clinical chemistry", new[] { new A("GLU", "سكر صائم", "Fasting glucose", "mg/dL", 70m, 100m, 40m, 450m, 0) }),
			("LAB-LIPID", "مصل", "Serum", "الكيمياء الحيوية", "Clinical chemistry", new[] {
				new A("CHOL", "الكوليسترول الكلي", "Total cholesterol", "mg/dL", null, 200m, null, null, 0),
				new A("TG", "الدهون الثلاثية", "Triglycerides", "mg/dL", null, 150m, null, null, 0),
				new A("HDL", "الكوليسترول النافع", "HDL cholesterol", "mg/dL", 40m, null, null, null, 0),
				new A("LDL", "الكوليسترول الضار", "LDL cholesterol", "mg/dL", null, 130m, null, null, 0) }),
			("LAB-KFT", "مصل", "Serum", "الكيمياء الحيوية", "Clinical chemistry", new[] {
				new A("UREA", "اليوريا", "Urea", "mg/dL", 15m, 45m, null, 200m, 0),
				new A("CREA", "الكرياتينين", "Creatinine", "mg/dL", 0.6m, 1.3m, null, 7.0m, 2),
				new A("K", "البوتاسيوم", "Potassium", "mmol/L", 3.5m, 5.1m, 2.8m, 6.2m, 1),
				new A("NA", "الصوديوم", "Sodium", "mmol/L", 135m, 145m, 120m, 160m, 0) }),
			("LAB-LFT", "مصل", "Serum", "الكيمياء الحيوية", "Clinical chemistry", new[] {
				new A("ALT", "إنزيم ALT", "ALT", "U/L", 7m, 56m, null, 1000m, 0),
				new A("AST", "إنزيم AST", "AST", "U/L", 10m, 40m, null, 1000m, 0),
				new A("TBIL", "البيليروبين الكلي", "Total bilirubin", "mg/dL", 0.1m, 1.2m, null, 15m, 1),
				new A("ALB", "الألبومين", "Albumin", "g/dL", 3.5m, 5.0m, null, null, 1) }),
			("LAB-TSH", "مصل", "Serum", "المناعة والهرمونات", "Immunology & hormones", new[] { new A("TSH", "هرمون الغدة الدرقية", "TSH", "mIU/L", 0.4m, 4.0m, null, null, 2) }),
			("LAB-URINE", "بول", "Urine", "الكيمياء الحيوية", "Clinical chemistry", new[] {
				new A("PH", "الحموضة", "pH", null, 4.5m, 8.0m, null, null, 1),
				new A("PRO", "البروتين", "Protein", null, null, null, null, null, 0),
				new A("GLUU", "السكر في البول", "Glucose", null, null, null, null, null, 0) }),
			("LAB-CRP", "مصل", "Serum", "المناعة والهرمونات", "Immunology & hormones", new[] { new A("CRP", "بروتين سي التفاعلي", "CRP", "mg/L", null, 5m, null, null, 1) }),
			("LAB-TROP", "مصل", "Serum", "المناعة والهرمونات", "Immunology & hormones", new[] { new A("TROP", "تروبونين I", "Troponin I", "ng/mL", null, 0.04m, null, 0.4m, 2) }),
			("LAB-VITD", "مصل", "Serum", "المناعة والهرمونات", "Immunology & hormones", new[] { new A("VITD", "فيتامين د", "Vitamin D (25-OH)", "ng/mL", 30m, 100m, 10m, null, 0) }),
		};
	}
}
