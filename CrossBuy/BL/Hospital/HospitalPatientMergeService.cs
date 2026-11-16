using System.Data.Common;
using System.Text;
using System.Text.Json;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H57 — merging duplicate patient files (owner ruling 2026-11-08: the hospital ADMINISTRATOR merges —
	// `manage`; the front desk sees the suspected duplicates — `register`).
	//
	//   · Suspected pairs: the same mobile and birth date, or the same name (Arabic or English, normalised) and birth date.
	//   · The merge moves EVERY record of the duplicate to the surviving file, in ONE transaction: the 45 tables that
	//     carry a patient (PatientTables — a test holds it to every entity with a PatientId) and the patient's documents.
	//     The access log is NOT moved: who opened which file is history and stays true.
	//   · The surviving file keeps its own details and takes what it lacks from the duplicate; allergies and chronic
	//     conditions are the UNION of both (a lost allergy is a danger, a repeated one is not).
	//   · The duplicate is not deleted: it is closed (IsActive = false) and points at the survivor; opening it opens
	//     the survivor. HospitalPatientMerges records who, when, why and what moved.
	//   · Refused when the two files each hold something only one patient can hold: an open stay, an open ER visit, a
	//     portal account, a dental chart, a dental recall, a pending approval for the same service.
	//   · The communication threads on the duplicate stay on it (the communication platform's own records).
	// ==========================================================================================

	public sealed record HospitalDuplicatePair(HospitalPatient A, HospitalPatient B, IReadOnlyList<string> Reasons);
	public sealed record HospitalMergeSide(HospitalPatient Patient, IReadOnlyDictionary<string, int> Records, int Total, bool HasPortal, bool InNow);
	public sealed record HospitalMergeRow(HospitalPatientMerge Merge, string? SurvivorName, string? SurvivorNameEn, string? SurvivorMrn, string? By, string? ByEn);

	public static class HospitalPatientMergeRules
	{
		/// Every table that carries a patient, with the entity it maps. HospitalPatientAccessLogs is deliberately absent.
		public static readonly IReadOnlyList<(Type Entity, string Table)> PatientTables = new (Type, string)[]
		{
			(typeof(HospitalAdmission), "HospitalAdmissions"), (typeof(HospitalAppointment), "HospitalAppointments"), (typeof(HospitalBedStay), "HospitalBedStays"),
			(typeof(HospitalBill), "HospitalBills"), (typeof(HospitalClaim), "HospitalClaims"), (typeof(HospitalConsignment), "HospitalConsignments"),
			(typeof(HospitalControlledEntry), "HospitalControlledEntries"), (typeof(HospitalDentalChart), "HospitalDentalCharts"), (typeof(HospitalDentalFinding), "HospitalDentalFindings"),
			(typeof(HospitalDentalLabCase), "HospitalDentalLabCases"), (typeof(HospitalDentalPlan), "HospitalDentalPlans"), (typeof(HospitalDentalRecall), "HospitalDentalRecalls"),
			(typeof(HospitalDietOrder), "HospitalDietOrders"), (typeof(HospitalDischargeSummary), "HospitalDischargeSummaries"), (typeof(HospitalDispense), "HospitalDispenses"),
			(typeof(HospitalEncounter), "HospitalEncounters"), (typeof(HospitalErVisit), "HospitalErVisits"), (typeof(HospitalEstimate), "HospitalEstimates"),
			(typeof(HospitalFeeStatementLine), "HospitalFeeStatementLines"), (typeof(HospitalIcuAssessment), "HospitalIcuAssessments"), (typeof(HospitalIcuObservation), "HospitalIcuObservations"),
			(typeof(HospitalImplant), "HospitalImplants"), (typeof(HospitalIncident), "HospitalIncidents"), (typeof(HospitalLabOrder), "HospitalLabOrders"),
			(typeof(HospitalMarEntry), "HospitalMarEntries"), (typeof(HospitalMealOrder), "HospitalMealOrders"), (typeof(HospitalMedDelivery), "HospitalMedDeliveries"),
			(typeof(HospitalMedOrder), "HospitalMedOrders"), (typeof(HospitalNursingTask), "HospitalNursingTasks"), (typeof(HospitalOrthoCase), "HospitalOrthoCases"),
			(typeof(HospitalPatientPolicy), "HospitalPatientPolicies"), (typeof(HospitalPerioExam), "HospitalPerioExams"), (typeof(HospitalPortalAccount), "HospitalPortalAccounts"),
			(typeof(HospitalPortalBooking), "HospitalPortalBookings"), (typeof(HospitalPortalFamilyLink), "HospitalPortalFamilyLinks"), (typeof(HospitalPortalOffer), "HospitalPortalOffers"),
			(typeof(HospitalPortalRefill), "HospitalPortalRefills"), (typeof(HospitalPortalRequest), "HospitalPortalRequests"), (typeof(HospitalPreauth), "HospitalPreauths"),
			(typeof(HospitalRadOrder), "HospitalRadOrders"), (typeof(HospitalSickLeave), "HospitalSickLeaves"), (typeof(HospitalSterilUse), "HospitalSterilUses"),
			(typeof(HospitalSurgery), "HospitalSurgeries"), (typeof(HospitalTransfusionRequest), "HospitalTransfusionRequests"),
			(typeof(HospitalDeath), "HospitalDeaths"),   // H59
			(typeof(HospitalPharmacyIntervention), "HospitalPharmacyInterventions"),   // H136: the pharmacist's interventions follow the patient
			(typeof(HospitalPatientDeposit), "HospitalPatientDeposits"),   // H143: what the hospital holds for the patient follows the patient
			(typeof(HospitalConsent), "HospitalConsents"),   // H60: the signature stays on the consent, which moves whole
			(typeof(HospitalCharityGrant), "HospitalCharityGrants"),   // H63: a waiver follows its patient (its bill moves too)
			(typeof(HospitalRestrictedFile), "HospitalRestrictedFiles"),   // H64: a restriction (live: one per patient — see the conflict)
			(typeof(HospitalBreakGlass), "HospitalBreakGlass"),             // H64: every emergency opening stays with the file
			(typeof(HospitalSurveyResponse), "HospitalSurveyResponses"),   // H65: the patient's surveys
			(typeof(HospitalSupportJob), "HospitalSupportJobs"),           // H67: a porter job names the patient it moves
			(typeof(HospitalSmsMessage), "HospitalSmsMessages"),           // H70: the messages sent to the patient
			(typeof(HospitalSmsOptOut), "HospitalSmsOptOuts"),             // H70: "no messages" goes with the file
			(typeof(HospitalObservation), "HospitalObservations"),         // H71: the ward observations
			(typeof(HospitalRiskAssessment), "HospitalRiskAssessments"),   // H71: the falls and pressure-injury assessments
			(typeof(HospitalMedReconLine), "HospitalMedReconLines"),       // H72: the medicines reconciled
			(typeof(HospitalMedRecon), "HospitalMedRecons"),               // H72: the signed stages
			(typeof(HospitalAlertOverride), "HospitalAlertOverrides"),     // H73: the alerts a doctor went past
			(typeof(HospitalOrderSetUse), "HospitalOrderSetUses"),         // H74: the sets placed for the patient
			(typeof(HospitalInpatientOrder), "HospitalInpatientOrders"),   // H75: the stay's lab tests and imaging
			(typeof(HospitalIsolation), "HospitalIsolations"),             // H76: the precautions follow the patient
			(typeof(HospitalInvasiveDevice), "HospitalInvasiveDevices"),   // H76: the devices of the stay
			(typeof(HospitalInfectionCase), "HospitalInfectionCases"),     // H76: the infections
			(typeof(HospitalConsultRequest), "HospitalConsultRequests"),   // H77: the consultations asked for the patient
			(typeof(HospitalFluidEntry), "HospitalFluidEntries"),         // H78: the fluid chart
			(typeof(HospitalProblem), "HospitalProblems"),                 // H79: the problem list follows the patient
			(typeof(HospitalCarePlanItem), "HospitalCarePlanItems"),       // H79: the care plans
			(typeof(HospitalChartReview), "HospitalChartReviews"),         // H80: the chart sign-offs
			(typeof(HospitalImmunization), "HospitalImmunizations"),       // H84: the doses given follow the patient
			(typeof(HospitalPregnancy), "HospitalPregnancies"),             // H85: the pregnancies
			(typeof(HospitalAntenatalVisit), "HospitalAntenatalVisits"),   // H85: the antenatal visits
			(typeof(HospitalLabour), "HospitalLabours"),                     // H86: the labours
			(typeof(HospitalLabourObservation), "HospitalLabourObservations"),   // H86: the partograph
			(typeof(HospitalDialysisPlan), "HospitalDialysisPlans"),           // H87: the dialysis prescription
			(typeof(HospitalDialysisSession), "HospitalDialysisSessions"),     // H87: the sessions
			(typeof(HospitalDialysisObservation), "HospitalDialysisObservations"),   // H87: the readings during them
			(typeof(HospitalChemoCourse), "HospitalChemoCourses"),             // H88: the chemotherapy courses
			(typeof(HospitalChemoCycle), "HospitalChemoCycles"),               // H88: the cycles given and held
			(typeof(HospitalChemoDose), "HospitalChemoDoses"),                 // H88: the doses (the lifetime limits follow the patient)
			(typeof(HospitalRehabEpisode), "HospitalRehabEpisodes"),           // H89: the physiotherapy episodes
			(typeof(HospitalRehabGoal), "HospitalRehabGoals"),                 // H89: their goals
			(typeof(HospitalRehabSession), "HospitalRehabSessions"),           // H89: their sessions
			(typeof(HospitalMorgueCase), "HospitalMorgueCases"),               // H90: the mortuary register
			(typeof(HospitalSelfIntake), "HospitalSelfIntakes"),               // H91: the reception intakes linked to the file
			(typeof(HospitalUnidentified), "HospitalUnidentifiedPatients"),    // H92: the temporary identity follows the file it was merged into
			(typeof(HospitalClinicalForm), "HospitalClinicalForms"),           // H93: the filled specialty templates
			(typeof(HospitalClinicalObservation), "HospitalClinicalObservations"), // H93: every signed value
			(typeof(HospitalImmunoCourse), "HospitalImmunoCourses"),           // H97: immunotherapy courses
			(typeof(HospitalImmunoDose), "HospitalImmunoDoses"),               // H97: and their doses
			(typeof(HospitalCanvasSheet), "HospitalCanvasSheets"),             // H99: the visit drawings
			(typeof(HospitalCanvasMark), "HospitalCanvasMarks"),               // H99: and their marks (a lesion number may repeat after a merge; the place tells them apart)
		};

		/// Patient columns NOT named PatientId (a test holds this list to every such column too). The merge history and the
		/// patient's own MergedIntoPatientId are not here: they record merges, they are not the patient's care.
		public static readonly IReadOnlyList<(Type Entity, string Table, string Column)> OtherPatientColumns = new (Type, string, string)[]
		{
			(typeof(HospitalBirth), "HospitalBirths", "MotherPatientId"), (typeof(HospitalBirth), "HospitalBirths", "NewbornPatientId"),   // H58
		};

		/// A name as two spellings of it compare: no diacritics, one form of alef, yaa and taa marbuta, no spaces or case.
		public static string Normalise(string? name)
		{
			if (string.IsNullOrWhiteSpace(name)) return "";
			var sb = new StringBuilder();
			foreach (var ch in name.Trim().ToLowerInvariant())
			{
				if (ch is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ') continue;   // tashkeel, superscript alef, tatweel
				if (char.IsWhiteSpace(ch) || ch is '-' or '.' or '\'') continue;
				sb.Append(ch switch { 'أ' or 'إ' or 'آ' or 'ٱ' => 'ا', 'ى' => 'ي', 'ة' => 'ه', 'ؤ' => 'و', 'ئ' => 'ي', _ => ch });
			}
			return sb.ToString();
		}

		/// Why two files look like one patient (empty = they do not).
		public static IReadOnlyList<string> Reasons(HospitalPatient a, HospitalPatient b)
		{
			var r = new List<string>();
			bool sameBirth = a.BirthDate != null && a.BirthDate?.Date == b.BirthDate?.Date;
			string Digits(string? p) => new string((p ?? "").Where(char.IsDigit).ToArray());
			var pa = Digits(a.Phone); var pb = Digits(b.Phone);
			bool samePhone = pa.Length >= 7 && pa.Length == pb.Length ? pa == pb : pa.Length >= 7 && pb.Length >= 7 && (pa.EndsWith(pb[^7..]) && pb.EndsWith(pa[^7..]));
			if (samePhone && sameBirth) r.Add("SamePhoneAndBirth");
			if (sameBirth && Normalise(a.Name).Length > 0 && Normalise(a.Name) == Normalise(b.Name)) r.Add("SameNameAndBirth");
			if (sameBirth && Normalise(a.NameEn).Length > 0 && Normalise(a.NameEn) == Normalise(b.NameEn)) r.Add("SameEnglishNameAndBirth");
			if (samePhone && !sameBirth && Normalise(a.Name).Length > 0 && Normalise(a.Name) == Normalise(b.Name)) r.Add("SamePhoneAndName");
			return r;
		}

		/// The union of two line lists (allergies, chronic conditions), each line once, the survivor's first.
		public static string? Union(string? keep, string? add)
		{
			var lines = (keep ?? "").Split('\n').Concat((add ?? "").Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var outp = new List<string>();
			foreach (var l in lines) if (seen.Add(Normalise(l))) outp.Add(l);
			return outp.Count == 0 ? null : string.Join("\n", outp);
		}

		public static string? MergeRefusal(string? reason, int survivorId, int mergedId)
		{
			if (survivorId == mergedId) return "Choose two different files";
			if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) return "Write why these two files are one patient";
			if (reason.Length > 500) return "A text is too long";
			return null;
		}
	}

	public sealed class HospitalPatientMergeService
	{
		public const string NoTables = "Merging files is not installed yet (hospital_h57_patient_merge.sql).";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPatientMergeService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		/// The suspected duplicate pairs among the company's open files (at most `max` files compared).
		public async Task<IReadOnlyList<HospitalDuplicatePair>> CandidatesAsync(BusinessContext ctx, int max = 5000, CancellationToken ct = default)
		{
			var all = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive).OrderBy(p => p.ID).Take(max).ToListAsync(ct);
			var pairs = new Dictionary<(int, int), List<string>>();
			void Add(IEnumerable<IGrouping<string, HospitalPatient>> groups)
			{
				foreach (var g in groups.Where(g => g.Key.Length > 0 && g.Count() > 1))
				{
					var list = g.ToList();
					for (int i = 0; i < list.Count; i++)
						for (int j = i + 1; j < list.Count; j++)
						{
							var why = HospitalPatientMergeRules.Reasons(list[i], list[j]);
							if (why.Count == 0) continue;
							var key = (Math.Min(list[i].ID, list[j].ID), Math.Max(list[i].ID, list[j].ID));
							if (!pairs.TryGetValue(key, out var r)) pairs[key] = r = new();
							foreach (var w in why) if (!r.Contains(w)) r.Add(w);
						}
				}
			}
			// only files that share a birth date or a phone are ever compared
			Add(all.Where(p => p.BirthDate != null).GroupBy(p => p.BirthDate!.Value.ToString("yyyyMMdd")));
			Add(all.GroupBy(p => new string((p.Phone ?? "").Where(char.IsDigit).ToArray()) is { Length: >= 7 } d ? d[^7..] : ""));
			var byId = all.ToDictionary(p => p.ID);
			return pairs.Select(kv => new HospitalDuplicatePair(byId[kv.Key.Item1], byId[kv.Key.Item2], kv.Value)).OrderBy(x => x.A.ID).ToList();
		}

		/// The past merges, newest first. `Ready` false = no H57 table.
		public async Task<(IReadOnlyList<HospitalMergeRow> Rows, bool Ready)> HistoryAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var rows = await _db.Set<HospitalPatientMerge>().AsNoTracking().Where(m => m.CompanyId == ctx.CompanyId).OrderByDescending(m => m.ID).Take(50).ToListAsync(ct);
				var pids = rows.Select(r => r.SurvivorPatientId).Distinct().ToList(); var eids = rows.Select(r => r.MergedByEmployeeId).Distinct().ToList();
				var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && pids.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
				var people = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == ctx.CompanyId && eids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, ct);
				return (rows.Select(r => { var p = pats.GetValueOrDefault(r.SurvivorPatientId); var e = people.GetValueOrDefault(r.MergedByEmployeeId);
					return new HospitalMergeRow(r, p?.Name, p?.NameEn, p?.Mrn, e?.FullName, e?.FullNameEn ?? e?.FullName); }).ToList(), true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalMergeRow>(), false); }
		}

		/// How many records of each kind a file holds (for the side-by-side comparison).
		private async Task<Dictionary<string, int>> CountsAsync(int companyId, int patientId, CancellationToken ct)
		{
			var sql = string.Join(" UNION ALL ", HospitalPatientMergeRules.PatientTables.Select(t => $"SELECT N'{t.Table}' AS T, COUNT(*) AS N FROM dbo.[{t.Table}] WHERE CompanyId = @c AND PatientId = @p"));
			var conn = _db.Database.GetDbConnection(); bool opened = false;
			if (conn.State != System.Data.ConnectionState.Open) { await conn.OpenAsync(ct); opened = true; }
			try
			{
				await using var cmd = conn.CreateCommand(); cmd.CommandText = sql;
				if (_db.Database.CurrentTransaction is { } tx) cmd.Transaction = tx.GetDbTransaction();
				Param(cmd, "@c", companyId); Param(cmd, "@p", patientId);
				var r = new Dictionary<string, int>();
				await using var rd = await cmd.ExecuteReaderAsync(ct);
				while (await rd.ReadAsync(ct)) { var n = rd.GetInt32(1); if (n > 0) r[rd.GetString(0)] = n; }
				return r;
			}
			finally { if (opened) await conn.CloseAsync(); }
		}

		private static void Param(DbCommand cmd, string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }

		/// The two files side by side; null when either is not this company's open file.
		public async Task<(HospitalMergeSide A, HospitalMergeSide B)?> CompareAsync(BusinessContext ctx, int aId, int bId, CancellationToken ct = default)
		{
			var ps = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && (p.ID == aId || p.ID == bId)).ToListAsync(ct);
			if (ps.Count != 2) return null;
			async Task<HospitalMergeSide> Side(HospitalPatient p)
			{
				var c = await CountsAsync(ctx.CompanyId, p.ID, ct);
				return new HospitalMergeSide(p, c, c.Values.Sum(), c.ContainsKey("HospitalPortalAccounts"),
					await _db.HospitalAdmissions.AnyAsync(a => a.CompanyId == ctx.CompanyId && a.PatientId == p.ID && a.Status == HospitalAdmissionStatus.Active, ct));
			}
			return (await Side(ps.First(p => p.ID == aId)), await Side(ps.First(p => p.ID == bId)));
		}

		/// What both files hold that only one patient may hold (null = none: the merge may go ahead).
		private async Task<string?> ConflictAsync(int c, int s, int d, CancellationToken ct)
		{
			async Task<bool> Both(Func<int, Task<bool>> has) => await has(s) && await has(d);
			if (await Both(p => _db.HospitalAdmissions.AnyAsync(a => a.CompanyId == c && a.PatientId == p && a.Status == HospitalAdmissionStatus.Active, ct))) return "Both files have an open stay: discharge or transfer one first";
			if (await Both(p => _db.Set<HospitalErVisit>().AnyAsync(v => v.CompanyId == c && v.PatientId == p && v.Stage != HospitalErStage.Closed, ct))) return "Both files have an open ER visit: close one first";
			if (await Both(p => _db.Set<HospitalPortalAccount>().AnyAsync(a => a.CompanyId == c && a.PatientId == p, ct))) return "Both files have a patient portal account: keep one account first";
			if (await Both(p => _db.Set<HospitalDentalChart>().AnyAsync(x => x.CompanyId == c && x.PatientId == p, ct))) return "Both files have a dental chart: the dentist must reconcile them first";
			if (await Both(p => _db.Set<HospitalDentalRecall>().AnyAsync(x => x.CompanyId == c && x.PatientId == p, ct))) return "Both files have a dental recall: keep one first";
			if (await Both(p => _db.Set<HospitalDeath>().AnyAsync(x => x.CompanyId == c && x.PatientId == p && x.VoidedAt == null, ct))) return "Both files have a death certificate: void one first";   // H59
			if (await Both(p => _db.Set<HospitalRestrictedFile>().AnyAsync(x => x.CompanyId == c && x.PatientId == p && x.LiftedAt == null, ct))) return "Both files are restricted: lift one restriction first";   // H64
			// H87: one standing dialysis plan per patient, and one session per patient per day
			if (await Both(p => _db.Set<HospitalDialysisPlan>().AnyAsync(x => x.CompanyId == c && x.PatientId == p && x.Status == HospitalDialysisPlanStatus.Active, ct))) return "Both files have an active dialysis plan: stop one first";
			var dDays = await _db.Set<HospitalDialysisSession>().AsNoTracking().Where(x => x.CompanyId == c && x.PatientId == d).Select(x => x.SessionDate).ToListAsync(ct);
			if (dDays.Count > 0 && await _db.Set<HospitalDialysisSession>().AnyAsync(x => x.CompanyId == c && x.PatientId == s && dDays.Contains(x.SessionDate), ct)) return "Both files have a dialysis session on the same day: the unit must reconcile them first";
			var pend = await _db.Set<HospitalPreauth>().AsNoTracking().Where(x => x.CompanyId == c && (x.PatientId == s || x.PatientId == d) && x.Status == HospitalPreauthStatus.Pending)
				.Select(x => new { x.PatientId, x.ServiceId }).ToListAsync(ct);
			if (pend.GroupBy(x => x.ServiceId).Any(g => g.Select(x => x.PatientId).Distinct().Count() == 2)) return "Both files wait for an approval of the same service: answer one first";
			return null;
		}

		/// Merges `mergedId` into `survivorId` (the administrator). One transaction; the duplicate is closed, not deleted.
		public async Task<HospitalResult> MergeAsync(BusinessContext ctx, int survivorId, int mergedId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			if (HospitalPatientMergeRules.MergeRefusal(reason, survivorId, mergedId) is string why) return HospitalResult.Fail(why);
			int c = ctx.CompanyId;
			var s = await _db.HospitalPatients.FirstOrDefaultAsync(p => p.ID == survivorId && p.CompanyId == c && p.IsActive, ct);
			var d = await _db.HospitalPatients.FirstOrDefaultAsync(p => p.ID == mergedId && p.CompanyId == c && p.IsActive, ct);
			if (s == null || d == null) return HospitalResult.Fail("not_found");
			if (await ConflictAsync(c, s.ID, d.ID, ct) is string conflict) return HospitalResult.Fail(conflict);
			try
			{
				await using var tx = await _db.Database.BeginTransactionAsync(ct);
				var moved = new Dictionary<string, int>();
				// a family link that would repeat (the same account already books for the survivor) or point an account at its own patient goes
				await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE l FROM dbo.HospitalPortalFamilyLinks l WHERE l.CompanyId = {c} AND l.PatientId = {d.ID}
					AND (EXISTS (SELECT 1 FROM dbo.HospitalPortalFamilyLinks x WHERE x.AccountId = l.AccountId AND x.PatientId = {s.ID})
					  OR EXISTS (SELECT 1 FROM dbo.HospitalPortalAccounts a WHERE a.ID = l.AccountId AND a.PatientId = {s.ID}))", ct);
				// H70: one "no messages" per patient — the duplicate's goes when the survivor already asked
				await _db.Database.ExecuteSqlInterpolatedAsync($@"IF OBJECT_ID('dbo.HospitalSmsOptOuts','U') IS NOT NULL
					DELETE o FROM dbo.HospitalSmsOptOuts o WHERE o.CompanyId = {c} AND o.PatientId = {d.ID}
					AND EXISTS (SELECT 1 FROM dbo.HospitalSmsOptOuts x WHERE x.CompanyId = {c} AND x.PatientId = {s.ID})", ct);
				foreach (var (_, table) in HospitalPatientMergeRules.PatientTables)
				{
					// the table name is from the constant list above, never from a request
#pragma warning disable EF1002
					int n = await _db.Database.ExecuteSqlRawAsync($"UPDATE dbo.[{table}] SET PatientId = {{0}} WHERE CompanyId = {{1}} AND PatientId = {{2}}", new object[] { s.ID, c, d.ID }, ct);
#pragma warning restore EF1002
					if (n > 0) moved[table] = n;
				}
				foreach (var (_, table, column) in HospitalPatientMergeRules.OtherPatientColumns)
				{
					// table and column from the constant list above
#pragma warning disable EF1002
					int n = await _db.Database.ExecuteSqlRawAsync($"UPDATE dbo.[{table}] SET [{column}] = {{0}} WHERE CompanyId = {{1}} AND [{column}] = {{2}}", new object[] { s.ID, c, d.ID }, ct);
#pragma warning restore EF1002
					if (n > 0) moved[table + "." + column] = n;
				}
				// the documents (attachments, photos); the duplicate's photo stays behind when the survivor has its own
				bool survivorPhoto = await _db.Set<PlatformDocument>().AnyAsync(x => x.CompanyID == c && x.EntityType == EntityRegistry.HospitalPatient && x.EntityId == s.ID && x.Metadata == HospitalPatientPhotoRules.Metadata, ct);
				int docs = await _db.Set<PlatformDocument>().Where(x => x.CompanyID == c && x.EntityType == EntityRegistry.HospitalPatient && x.EntityId == d.ID && !(survivorPhoto && x.Metadata == HospitalPatientPhotoRules.Metadata))
					.ExecuteUpdateAsync(u => u.SetProperty(x => x.EntityId, s.ID), ct);
				if (docs > 0) moved["PlatformDocuments"] = docs;

				// the survivor's details: its own first, the duplicate's where it has none; allergies and conditions both
				string? nid = s.NationalId == null ? d.NationalId : null;
				if (nid != null) { d.NationalId = null; await _db.SaveChangesAsync(ct); }   // one file per national ID: free it first
				s.NationalId ??= nid; s.NameEn ??= d.NameEn; s.BirthDate ??= d.BirthDate; s.Phone ??= d.Phone; s.BloodGroup ??= d.BloodGroup; s.NationalityId ??= d.NationalityId;
				s.EmergencyName ??= d.EmergencyName; s.EmergencyRelation ??= d.EmergencyRelation; s.EmergencyPhone ??= d.EmergencyPhone; s.PhotoPath ??= d.PhotoPath;
				s.Allergies = HospitalPatientMergeRules.Union(s.Allergies, d.Allergies); s.ChronicConditions = HospitalPatientMergeRules.Union(s.ChronicConditions, d.ChronicConditions);
				s.ConsentSigned = s.ConsentSigned || d.ConsentSigned; s.UpdatedAt = nowUtc; s.UpdatedByEmployeeId = me;
				s.DeceasedAt ??= d.DeceasedAt;   // H59: the certificate moved with the records
				d.IsActive = false; d.MergedIntoPatientId = s.ID; d.MergedAt = nowUtc; d.UpdatedAt = nowUtc; d.UpdatedByEmployeeId = me;
				_db.Add(new HospitalPatientMerge { CompanyId = c, SurvivorPatientId = s.ID, MergedPatientId = d.ID, MergedMrn = d.Mrn, Reason = reason!.Trim(),
					Moved = JsonSerializer.Serialize(moved), MergedAt = nowUtc, MergedByEmployeeId = me });
				await _db.SaveChangesAsync(ct);
				await tx.CommitAsync(ct);
				return HospitalResult.Success(s.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
		}
	}
}
