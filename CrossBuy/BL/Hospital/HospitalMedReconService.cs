using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H72 — medication reconciliation. Anyone caring for the patient (nurse, pharmacist, doctor) lists the home
	// medicines; a doctor decides each and signs the stage. A signed stage is locked. At discharge the medicines still
	// running in hospital are offered for a decision straight from the orders (a line is written the first time one is
	// decided — a page view writes nothing), and the signed list for home can be written into the discharge summary.
	// ==========================================================================================
	public sealed class HospitalMedLineInput
	{
		public int AdmissionId { get; set; }
		/// Home (the history) or New (a medicine new for home, at discharge).
		public string? Kind { get; set; }
		public string? Drug { get; set; }
		public string? Dose { get; set; }
		public string? Route { get; set; }
		public string? Frequency { get; set; }
		public string? LastTaken { get; set; }
		public string? Source { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalMedDecideInput
	{
		public int AdmissionId { get; set; }
		public string? Stage { get; set; }
		/// A reconciliation line, or — at discharge — a hospital order not decided yet.
		public int? LineId { get; set; }
		public int? MedOrderId { get; set; }
		public string? Decision { get; set; }
		public string? Reason { get; set; }
	}

	public sealed record HospitalMedReconView(int AdmissionId, int PatientId, string Name, string NameEn, string Mrn, string? Photo, string? Allergies, DateTime AdmittedAt,
		string AdmissionStatus, IReadOnlyList<HospitalMedReconLine> Lines, IReadOnlyList<HospitalMedOrder> OpenOrders, HospitalMedRecon? AdmissionDone, HospitalMedRecon? DischargeDone,
		bool SummaryDraft, IReadOnlyDictionary<int, string> People, bool Ready);

	public static class HospitalMedReconRules
	{
		/// THE WARD BOARD'S BADGE, AS A DECISION THAT CAN BE TESTED WITHOUT A DATABASE.
		///
		/// Three states, not two. The green one is a claim that this patient's home medicines were
		/// reconciled and signed; the red one is a claim that they were not and are overdue. When
		/// the module is not installed the board is entitled to neither, and the bug this replaces
		/// made it assert the GREEN one for every patient on the ward.
		///
		/// Written as a rule rather than inline so the invariant has somewhere to be proved: not
		/// ready can never produce signed, whatever the set behind it says.
		public static (bool Signed, bool Overdue) Badge(bool ready, bool inSignedSet, DateTime admittedAt, DateTime nowUtc)
		{
			if (!ready) return (false, false);
			return (inSignedSet, !inSignedSet && admittedAt <= nowUtc.AddHours(-24));
		}

		public static string? LineRefusal(HospitalMedLineInput i)
		{
			if (i.Kind is not (HospitalMedLineKind.Home or HospitalMedLineKind.New)) return "not_found";
			if (string.IsNullOrWhiteSpace(i.Drug)) return "Write the medicine";
			if (i.Drug.Trim().Length > 200) return "The medicine's name is too long";
			if (!string.IsNullOrEmpty(i.Route) && !HospitalMedRoute.All.Contains(i.Route)) return "Choose the route";
			if (i.Kind == HospitalMedLineKind.Home && !string.IsNullOrEmpty(i.Source) && !HospitalMedSource.All.Contains(i.Source)) return "Choose where the history came from";
			if (i.Kind == HospitalMedLineKind.New && string.IsNullOrWhiteSpace(i.Dose)) return "Write the dose";
			return null;
		}

		/// What a decision needs: a known decision for the stage; on admission only a home medicine is decided; anything
		/// but "continue" says why (a changed dose says what it changes to).
		public static string? DecideRefusal(string stage, HospitalMedReconLine? line, bool isOrder, string? decision, string? reason)
		{
			if (!HospitalMedReconStage.All.Contains(stage)) return "not_found";
			if (!HospitalMedDecision.For(stage).Contains(decision ?? "")) return "Choose the decision";
			if (stage == HospitalMedReconStage.Admission && (isOrder || line?.Kind != HospitalMedLineKind.Home)) return "On admission only the home medicines are reconciled";
			if (stage == HospitalMedReconStage.Discharge && line?.Kind == HospitalMedLineKind.New) return "A medicine new for home is already on the list";
			if (decision != HospitalMedDecision.Continue && string.IsNullOrWhiteSpace(reason))
				return decision == HospitalMedDecision.Change ? "Write the new dose or how it changes" : "Write why";
			return null;
		}

		/// Whether a stage can be signed: on admission every home medicine is decided (or none is taken, and none is
		/// listed); at discharge every home medicine and every medicine still running in hospital is decided.
		public static string? CompleteRefusal(string stage, IReadOnlyList<HospitalMedReconLine> lines, IReadOnlyCollection<int> openOrderIds, bool noHomeMeds)
		{
			var home = lines.Where(l => l.Kind == HospitalMedLineKind.Home).ToList();
			if (stage == HospitalMedReconStage.Admission)
			{
				if (noHomeMeds && home.Count > 0) return "Home medicines are listed: remove them, or clear the no-home-medicines tick";
				if (!noHomeMeds && home.Count == 0) return "List the home medicines, or record that the patient takes none";
				if (home.Any(l => l.AdmitDecision == null)) return "Decide every home medicine first";
				return null;
			}
			if (stage != HospitalMedReconStage.Discharge) return "not_found";
			if (home.Any(l => l.DischargeDecision == null)) return "Decide every home medicine first";
			var decidedOrders = lines.Where(l => l.Kind == HospitalMedLineKind.Inpatient && l.DischargeDecision != null && l.MedOrderId != null).Select(l => l.MedOrderId!.Value).ToHashSet();
			if (openOrderIds.Any(id => !decidedOrders.Contains(id))) return "Decide every medicine still given in hospital first";
			return null;
		}

		/// The medicines the patient goes home with — what continues or changes, and what is new — as their parts (the
		/// screen isolates each part, since a Latin drug name and an Arabic frequency scramble when run together), and the
		/// note that says how it changes or for how long.
		public static IReadOnlyList<(string[] Parts, string? Note)> HomeLines(IReadOnlyList<HospitalMedReconLine> lines) => lines
			.Where(l => l.Kind == HospitalMedLineKind.New || l.DischargeDecision is HospitalMedDecision.Continue or HospitalMedDecision.Change)
			.Select(l => (new[] { l.Drug, l.Dose, l.Route, l.Frequency }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray(),
				(l.DischargeDecision == HospitalMedDecision.Change || l.Kind == HospitalMedLineKind.New) && !string.IsNullOrWhiteSpace(l.DischargeNote) ? l.DischargeNote : null))
			.ToList();

		/// The same list as text, one line each (written into the discharge summary).
		public static IReadOnlyList<string> HomeList(IReadOnlyList<HospitalMedReconLine> lines) =>
			HomeLines(lines).Select(x => string.Join(" ", x.Parts) + (x.Note != null ? " — " + x.Note : "")).ToList();

		/// The admission reconciliation is overdue a day after admission.
		public static bool AdmissionOverdue(DateTime admittedAtUtc, HospitalMedRecon? done, DateTime nowUtc) => done == null && admittedAtUtc <= nowUtc.AddHours(-24);
	}

	public sealed class HospitalMedReconService
	{
		public const string NoTables = "Medication reconciliation is not installed yet (hospital_h72_med_recon.sql).";
		public const string Signed = "This reconciliation is signed and cannot change";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalMedReconService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private IQueryable<HospitalMedReconLine> Lines(BusinessContext ctx, int admissionId) => _db.Set<HospitalMedReconLine>().Where(l => l.CompanyId == ctx.CompanyId && l.AdmissionId == admissionId);
		private Task<HospitalMedRecon?> DoneAsync(BusinessContext ctx, int admissionId, string stage, CancellationToken ct) =>
			_db.Set<HospitalMedRecon>().AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.AdmissionId == admissionId && r.Stage == stage, ct);
		private Task<HospitalAdmission?> AdmissionAsync(BusinessContext ctx, int id, CancellationToken ct) =>
			_db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == id && a.CompanyId == ctx.CompanyId, ct);

		/// The admissions of this list whose admission reconciliation is signed, and whether the
		/// question could be answered at all (the nursing station flags the rest).
		///
		/// IT USED TO ANSWER "ALL OF THEM" WHEN THE TABLES WERE MISSING, with the comment "not
		/// installed: nothing to flag". The intent was sound — do not paint a ward red over a
		/// feature nobody installed — but the ward board reads this set as PROOF: _ObsPanel renders
		/// a green "Medicines reconciled" badge for every admission it contains. So an uninstalled
		/// module did not go quiet, it certified every patient on the ward.
		///
		/// The readiness travels with the answer instead. The board still shows no red when the
		/// module is absent, and it no longer shows green either.
		public static async Task<(HashSet<int> Signed, bool Ready)> AdmissionSignedAsync(CrossDbContext db, int companyId, IReadOnlyCollection<int> admissionIds, CancellationToken ct)
		{
			try
			{
				return ((await db.Set<HospitalMedRecon>().AsNoTracking().Where(r => r.CompanyId == companyId && r.Stage == HospitalMedReconStage.Admission && admissionIds.Contains(r.AdmissionId))
					.Select(r => r.AdmissionId).ToListAsync(ct)).ToHashSet(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new HashSet<int>(), false); }
		}

		public async Task<HospitalMedReconView?> PageAsync(BusinessContext ctx, int admissionId, CancellationToken ct = default)
		{
			var head = await (from a in _db.HospitalAdmissions.AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on a.PatientId equals p.ID
							  where a.ID == admissionId && a.CompanyId == ctx.CompanyId
							  select new { a.ID, a.PatientId, p.Name, p.NameEn, p.Mrn, p.PhotoPath, p.Allergies, a.AdmittedAt, a.Status }).FirstOrDefaultAsync(ct);
			if (head == null) return null;
			var orders = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId && o.Status == HospitalMedOrderStatus.Active)
				.OrderBy(o => o.CreatedAt).ToListAsync(ct);
			bool draft = await _db.Set<HospitalDischargeSummary>().AnyAsync(s => s.CompanyId == ctx.CompanyId && s.AdmissionId == admissionId && s.Status == HospitalDischargeSummaryStatus.Draft, ct);
			try
			{
				var lines = await Lines(ctx, admissionId).AsNoTracking().OrderBy(l => l.CreatedAt).ThenBy(l => l.ID).ToListAsync(ct);
				var a = await DoneAsync(ctx, admissionId, HospitalMedReconStage.Admission, ct);
				var d = await DoneAsync(ctx, admissionId, HospitalMedReconStage.Discharge, ct);
				var ids = lines.SelectMany(l => new[] { l.CreatedByEmployeeId, l.DecidedByEmployeeId }).Append(a?.CompletedByEmployeeId).Append(d?.CompletedByEmployeeId)
					.Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
					.Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, e => DisplayName.Of(e.FullName ?? "", e.FullNameEn), ct);
				return new(head.ID, head.PatientId, head.Name, head.NameEn, head.Mrn, head.PhotoPath, head.Allergies, head.AdmittedAt, head.Status, lines, orders, a, d, draft, people, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex))
			{
				return new(head.ID, head.PatientId, head.Name, head.NameEn, head.Mrn, head.PhotoPath, head.Allergies, head.AdmittedAt, head.Status, Array.Empty<HospitalMedReconLine>(), orders, null, null, draft, new Dictionary<int, string>(), false);
			}
		}

		public async Task<HospitalResult> AddLineAsync(BusinessContext ctx, HospitalMedLineInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			// the history is taken by whoever cares for the patient; a medicine new for home is a doctor's
			var need = input.Kind == HospitalMedLineKind.New ? HospitalActions.Consult : HospitalActions.Admit;
			if (!await _access.CanAsync(ctx, need, null, ct) && !await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalMedReconRules.LineRefusal(input) is string why) return HospitalResult.Fail(why);
			var adm = await AdmissionAsync(ctx, input.AdmissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			try
			{
				var stage = input.Kind == HospitalMedLineKind.Home ? HospitalMedReconStage.Admission : HospitalMedReconStage.Discharge;
				if (await DoneAsync(ctx, adm.ID, stage, ct) != null) return HospitalResult.Fail(Signed);
				var row = new HospitalMedReconLine
				{
					CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Kind = input.Kind!, Drug = input.Drug!.Trim(), Dose = Clip(input.Dose, 60),
					Route = string.IsNullOrEmpty(input.Route) ? null : input.Route, Frequency = Clip(input.Frequency, 100),
					LastTaken = input.Kind == HospitalMedLineKind.Home ? Clip(input.LastTaken, 60) : null, Source = input.Kind == HospitalMedLineKind.Home && !string.IsNullOrEmpty(input.Source) ? input.Source : null,
					DischargeNote = input.Kind == HospitalMedLineKind.New ? Clip(input.Note, 300) : null,
					CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
				};
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// A line no one has decided yet can be taken off the list (written by mistake); after a decision it stays.
		public async Task<HospitalResult> RemoveLineAsync(BusinessContext ctx, int lineId, int? employeeId, CancellationToken ct = default)
		{
			try
			{
				var l = await _db.Set<HospitalMedReconLine>().FirstOrDefaultAsync(x => x.ID == lineId && x.CompanyId == ctx.CompanyId, ct);
				if (l == null || l.Kind == HospitalMedLineKind.Inpatient) return HospitalResult.Fail("not_found");
				bool doctor = await _access.CanAsync(ctx, HospitalActions.Consult, null, ct);
				if (!doctor && l.CreatedByEmployeeId != employeeId) return HospitalResult.Fail("forbidden");
				if (l.AdmitDecision != null || (l.Kind == HospitalMedLineKind.Home && l.DischargeDecision != null)) return HospitalResult.Fail("A decided medicine stays on the record");
				var stage = l.Kind == HospitalMedLineKind.Home ? HospitalMedReconStage.Admission : HospitalMedReconStage.Discharge;
				if (await DoneAsync(ctx, l.AdmissionId, stage, ct) != null) return HospitalResult.Fail(Signed);
				_db.Remove(l);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(lineId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> DecideAsync(BusinessContext ctx, HospitalMedDecideInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var stage = input.Stage ?? "";
			var adm = await AdmissionAsync(ctx, input.AdmissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			try
			{
				if (await DoneAsync(ctx, adm.ID, stage, ct) != null) return HospitalResult.Fail(Signed);
				HospitalMedReconLine? line = null;
				HospitalMedOrder? order = null;
				if (input.LineId is int lid)
				{
					line = await Lines(ctx, adm.ID).FirstOrDefaultAsync(l => l.ID == lid, ct);
					if (line == null) return HospitalResult.Fail("not_found");
				}
				else if (input.MedOrderId is int oid)
				{
					order = await _db.Set<HospitalMedOrder>().AsNoTracking().FirstOrDefaultAsync(o => o.ID == oid && o.CompanyId == ctx.CompanyId && o.AdmissionId == adm.ID, ct);
					if (order == null) return HospitalResult.Fail("not_found");
					line = await Lines(ctx, adm.ID).FirstOrDefaultAsync(l => l.Kind == HospitalMedLineKind.Inpatient && l.MedOrderId == oid, ct);
				}
				else return HospitalResult.Fail("not_found");
				if (HospitalMedReconRules.DecideRefusal(stage, line, order != null && line == null, input.Decision, input.Reason) is string why) return HospitalResult.Fail(why);
				if (line == null)
				{
					// the first decision on a hospital medicine writes its line, copied from the order
					line = new HospitalMedReconLine
					{
						CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Kind = HospitalMedLineKind.Inpatient, MedOrderId = order!.ID,
						Drug = order.Drug, Dose = order.Dose, Route = order.Route, Frequency = order.AsNeeded ? "PRN" : order.Hours, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
					};
					_db.Add(line);
				}
				if (stage == HospitalMedReconStage.Admission) { line.AdmitDecision = input.Decision; line.AdmitReason = Clip(input.Reason, 300); }
				else { line.DischargeDecision = input.Decision; line.DischargeNote = Clip(input.Reason, 300); }
				line.DecidedAt = nowUtc; line.DecidedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(line.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> CompleteAsync(BusinessContext ctx, int admissionId, string stage, bool noHomeMeds, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			var adm = await AdmissionAsync(ctx, admissionId, ct);
			if (adm == null) return HospitalResult.Fail("not_found");
			if (adm.Status != HospitalAdmissionStatus.Active) return HospitalResult.Fail("This admission is not active");
			try
			{
				if (await DoneAsync(ctx, admissionId, stage, ct) != null) return HospitalResult.Fail(Signed);
				var lines = await Lines(ctx, admissionId).AsNoTracking().ToListAsync(ct);
				var open = await _db.Set<HospitalMedOrder>().AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && o.AdmissionId == admissionId && o.Status == HospitalMedOrderStatus.Active).Select(o => o.ID).ToListAsync(ct);
				if (HospitalMedReconRules.CompleteRefusal(stage, lines, open, noHomeMeds) is string why) return HospitalResult.Fail(why);
				var row = new HospitalMedRecon
				{
					CompanyId = ctx.CompanyId, AdmissionId = admissionId, PatientId = adm.PatientId, Stage = stage, NoHomeMeds = stage == HospitalMedReconStage.Admission && noHomeMeds,
					CompletedAt = nowUtc, CompletedByEmployeeId = employeeId,
				};
				_db.Add(row);
				try { await _db.SaveChangesAsync(ct); }
				catch (DbUpdateException) { _db.Entry(row).State = EntityState.Detached; return HospitalResult.Fail(Signed); }
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		/// Writes the signed home list into the discharge summary still in draft (its medicines field).
		public async Task<HospitalResult> CopyToSummaryAsync(BusinessContext ctx, int admissionId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				if (await DoneAsync(ctx, admissionId, HospitalMedReconStage.Discharge, ct) == null) return HospitalResult.Fail("Sign the discharge reconciliation first");
				var summary = await _db.Set<HospitalDischargeSummary>().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.AdmissionId == admissionId, ct);
				if (summary == null) return HospitalResult.Fail("Start the discharge summary first");
				if (summary.Status != HospitalDischargeSummaryStatus.Draft) return HospitalResult.Fail("The discharge summary is signed");
				var list = HospitalMedReconRules.HomeList(await Lines(ctx, admissionId).AsNoTracking().OrderBy(l => l.CreatedAt).ToListAsync(ct));
				summary.Medications = list.Count == 0 ? "—" : Clip(string.Join("\n", list.Select(x => "• " + x)), 2000);   // the summary holds 2000
				summary.UpdatedAt = nowUtc; summary.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(summary.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];
	}
}
