using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H27 — what the patient portal shows of the record: results, bills, medicines; and the staff side of
	// it (a doctor releases a result to the portal, the clinic answers a refill request).
	//
	//   · A result's VALUES leave the server only once released: an unreleased one is sent with no values and no
	//     report at all, so the page cannot show what it was never given.
	//   · Every row carries the patient (the account's own file or a family member's) and the doctor, each with
	//     their photograph (the owner's rule: a person is shown by their photo).
	//   · The portal's reads take no permission (the portal context has no employee); what they read is limited to
	//     the account's own people (HospitalPortalBookingService.PeopleAsync). The staff side asks `consult`.
	// ==========================================================================================

	/// A doctor as a portal row shows them: the photo is a stored employee path (the portal serves it through its
	/// own endpoint; a staff page uses it directly).
	public sealed record HospitalPortalDoctorRef(int Id, string Name, string NameEn, string? Title, string? TitleEn, string? Photo);

	public sealed record HospitalPortalResult(string Source, int OrderId, HospitalPortalPerson Person, string AccessionNo, string Name, string NameEn, DateTime At,
		HospitalPortalDoctorRef? Doctor, bool Released, string? Note, IReadOnlyList<HospitalLabResult> Values, string? Impression, string? Findings, bool Abnormal);

	public sealed record HospitalPortalBill(HospitalPortalPerson Person, int BillId, string BillNo, DateTime OpenedAt, string Status, decimal Gross, decimal Insurer,
		decimal YourShare, decimal Paid, decimal Due, string? Payer, string? PayerEn, decimal? CopayPct, HospitalPriceCurrency Currency);

	public sealed record HospitalPortalMedicine(int LineId, HospitalPortalPerson Person, string Drug, string? Instructions, int Quantity, DateTime PrescribedAt,
		HospitalPortalDoctorRef? Doctor, HospitalPortalRefill? Refill, string? CannotRefill);

	public sealed record HospitalPortalRefillRow(HospitalPortalRefill Refill, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		string Drug, string? Instructions, int Quantity, DateTime PrescribedAt, HospitalPortalDoctorRef? Doctor);

	public sealed class HospitalPortalRefillInput { public int LineId { get; set; } public string? Reason { get; set; } public string? Note { get; set; } }
	public sealed class HospitalPortalDecisionInput { public bool Approve { get; set; } public string? Answer { get; set; } public string? AnswerEn { get; set; } }
	/// H33: `NoteEn` is the note's English twin; `Edit` corrects the words of a release already made.
	public sealed class HospitalPortalReleaseInput { public string Source { get; set; } = HospitalPortalResultSource.Lab; public int OrderId { get; set; } public string? Note { get; set; } public string? NoteEn { get; set; } public bool Edit { get; set; } }

	/// The decisions, with no database.
	public static class HospitalPortalRecordsRules
	{
		/// A medicine prescribed longer ago than this needs a visit, not a refill.
		public const int RefillWithinDays = 365;

		/// Why this order cannot be released to the portal yet, or null: a lab order once verified, an imaging
		/// order once its report is signed.
		public static string? ReleaseRefusal(string source, string status) => source switch
		{
			HospitalPortalResultSource.Lab => status == HospitalLabStatus.Verified ? null : "A lab result is released once it is verified",
			HospitalPortalResultSource.Radiology => status == HospitalRadStatus.Signed ? null : "An imaging report is released once it is signed",
			_ => "not_found",
		};

		/// Why this medicine cannot be asked for again online, or null.
		public static string? RefillRefusal(DateTime prescribedAt, DateTime now, bool open)
		{
			if (open) return "A refill request for this medicine is already open";
			if ((now.Date - prescribedAt.Date).TotalDays > RefillWithinDays) return "Prescribed more than a year ago: book a visit to renew it";
			return null;
		}

		/// Why the clinic cannot answer this refill so, or null: once only, and a refusal says why.
		public static string? DecisionRefusal(string status, bool approve, string? answer)
		{
			if (status != HospitalPortalRefillStatus.Open) return "This request is already handled";
			if (!approve && string.IsNullOrWhiteSpace(answer)) return "Write the reason the patient will read";
			return null;
		}

		/// A lab result is abnormal when any value is flagged; an imaging report when its impression is not normal.
		public static bool Abnormal(IEnumerable<HospitalLabResult> values) => values.Any(v => v.Flag != HospitalLabFlag.Normal);
		public static bool Abnormal(string? impressionCategory) => impressionCategory is HospitalRadImpression.Abnormal or HospitalRadImpression.Critical;
	}

	public sealed class HospitalPortalRecordsService
	{
		public const string NoTables = "The portal records tables are not created yet (hospital_h27_portal_records.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalPortalBookingService _booking;
		private readonly HospitalBillingService? _billing;
		private readonly HospitalPricingService _pricing;

		public HospitalPortalRecordsService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService? billing = null)
		{ _db = db; _access = access; _booking = new HospitalPortalBookingService(db, access); _billing = billing; _pricing = new HospitalPricingService(db, access); }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		/// H33: a free text in the reader's language — its Arabic and English twins through DisplayName.Of, which falls
		/// back to the other when one is blank (an untranslated row still reads).
		private static string? Words(string? arabic, string? english) =>
			string.IsNullOrWhiteSpace(arabic) && string.IsNullOrWhiteSpace(english) ? null : DisplayName.Of(arabic ?? "", english ?? "");

		/// A prescribed drug's name and directions in the reader's language: a stocked drug by its item's names.
		private async Task<Dictionary<int, (string Name, string NameEn)>> ItemNamesAsync(BusinessContext ctx, IEnumerable<int?> itemIds, CancellationToken ct)
		{
			var ids = itemIds.OfType<int>().Distinct().ToList();
			if (ids.Count == 0) return new();
			return (await _db.Items.AsNoTracking().Where(i => i.CompanyID == ctx.CompanyId && ids.Contains(i.ID)).Select(i => new { i.ID, i.Name, i.NameEn }).ToListAsync(ct))
				.ToDictionary(i => i.ID, i => (i.Name, i.NameEn ?? i.Name));
		}
		private static string DrugName(HospitalPrescriptionLine l, Dictionary<int, (string Name, string NameEn)> items) =>
			l.ItemId is int id && items.TryGetValue(id, out var n) ? DisplayName.Of(n.Name, n.NameEn) : Words(l.Drug, l.DrugEn) ?? l.Drug;

		/// The doctors behind these encounters, with their photographs.
		private async Task<Dictionary<int, HospitalPortalDoctorRef>> DoctorsOfEncountersAsync(BusinessContext ctx, IEnumerable<int?> encounterIds, CancellationToken ct)
		{
			var ids = encounterIds.OfType<int>().Distinct().ToList();
			if (ids.Count == 0) return new();
			var rows = await (from e in _db.HospitalEncounters.AsNoTracking()
							  join p in _db.HospitalPractitioners.AsNoTracking() on e.PractitionerId equals p.ID
							  where e.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId && ids.Contains(e.ID)
							  select new { e.ID, p }).ToListAsync(ct);
			var photos = await EmployeePhotos.ResolveAsync(_db, ctx, rows.Where(r => r.p.EmployeeId != null).Select(r => r.p.EmployeeId!.Value), ct);
			return rows.ToDictionary(r => r.ID, r => new HospitalPortalDoctorRef(r.p.ID, r.p.Name, r.p.NameEn, r.p.Title, r.p.TitleEn,
				r.p.EmployeeId is int emp ? photos.GetValueOrDefault(emp) : null));
		}

		private async Task<List<HospitalPortalRelease>?> ReleasesAsync(BusinessContext ctx, string source, IReadOnlyCollection<int> orderIds, CancellationToken ct)
		{
			try { return await _db.Set<HospitalPortalRelease>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.Source == source && orderIds.Contains(r.OrderId)).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return null; }
		}

		// ====================================================================================
		// PORTAL SIDE — the account's own people only
		// ====================================================================================

		/// The account's verified lab orders and signed imaging reports, newest first. Unreleased ones carry no values.
		public async Task<IReadOnlyList<HospitalPortalResult>> ResultsAsync(BusinessContext ctx, int accountId, CancellationToken ct = default)
		{
			var people = (await _booking.ClinicalPeopleAsync(ctx, accountId, ct)).ToDictionary(p => p.PatientId);   // H251: full-scope people only
			if (people.Count == 0) return Array.Empty<HospitalPortalResult>();
			var ids = people.Keys.ToList();
			var labs = await _db.HospitalLabOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && ids.Contains(o.PatientId) && o.Status == HospitalLabStatus.Verified)
				.OrderByDescending(o => o.VerifiedAt).Take(100).ToListAsync(ct);
			var rads = await _db.HospitalRadOrders.AsNoTracking().Where(o => o.CompanyId == ctx.CompanyId && ids.Contains(o.PatientId) && o.Status == HospitalRadStatus.Signed)
				.OrderByDescending(o => o.SignedAt).Take(100).ToListAsync(ct);
			var labRel = (await ReleasesAsync(ctx, HospitalPortalResultSource.Lab, labs.Select(o => o.ID).ToList(), ct) ?? new()).ToDictionary(r => r.OrderId);
			var radRel = (await ReleasesAsync(ctx, HospitalPortalResultSource.Radiology, rads.Select(o => o.ID).ToList(), ct) ?? new()).ToDictionary(r => r.OrderId);
			var releasedLabIds = labs.Where(o => labRel.ContainsKey(o.ID)).Select(o => o.ID).ToList();
			var values = (await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && releasedLabIds.Contains(r.LabOrderId)).ToListAsync(ct))
				.GroupBy(r => r.LabOrderId).ToDictionary(g => g.Key, g => (IReadOnlyList<HospitalLabResult>)g.OrderBy(r => r.SortOrder).ToList());
			var docs = await DoctorsOfEncountersAsync(ctx, labs.Select(o => o.EncounterId).Concat(rads.Select(o => o.EncounterId)), ct);
			HospitalPortalDoctorRef? Doc(int? enc) => enc is int e ? docs.GetValueOrDefault(e) : null;

			var list = new List<HospitalPortalResult>();
			foreach (var o in labs)
			{
				var rel = labRel.GetValueOrDefault(o.ID);
				var v = rel == null ? Array.Empty<HospitalLabResult>() : values.GetValueOrDefault(o.ID) ?? Array.Empty<HospitalLabResult>();
				list.Add(new HospitalPortalResult(HospitalPortalResultSource.Lab, o.ID, people[o.PatientId], o.AccessionNo, o.Name, o.NameEn, o.VerifiedAt ?? o.OrderedAt, Doc(o.EncounterId),
					rel != null, Words(rel?.Note, rel?.NoteEn), v, null, null, rel != null && HospitalPortalRecordsRules.Abnormal(v)));
			}
			foreach (var o in rads)
			{
				var rel = radRel.GetValueOrDefault(o.ID);
				list.Add(new HospitalPortalResult(HospitalPortalResultSource.Radiology, o.ID, people[o.PatientId], o.AccessionNo, o.Name, o.NameEn, o.SignedAt ?? o.OrderedAt, Doc(o.EncounterId),
					rel != null, Words(rel?.Note, rel?.NoteEn), Array.Empty<HospitalLabResult>(), rel == null ? null : Words(o.Impression, o.ImpressionEn), rel == null ? null : Words(o.Findings, o.FindingsEn),
					rel != null && HospitalPortalRecordsRules.Abnormal(o.ImpressionCategory)));
			}
			return list.OrderByDescending(r => r.At).ToList();
		}

		/// The account's bills, newest first, each with the patient's share as the bill's own split computes it.
		public async Task<IReadOnlyList<HospitalPortalBill>> BillsAsync(BusinessContext ctx, int accountId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (_billing == null) return Array.Empty<HospitalPortalBill>();
			var people = (await _booking.ClinicalPeopleAsync(ctx, accountId, ct)).ToDictionary(p => p.PatientId);   // H251: full-scope people only
			if (people.Count == 0) return Array.Empty<HospitalPortalBill>();
			var ids = people.Keys.ToList();
			var billIds = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && ids.Contains(b.PatientId)).OrderByDescending(b => b.ID).Select(b => b.ID).Take(60).ToListAsync(ct);
			var facility = await _pricing.FacilityCurrencyAsync(ctx, ct);
			var list = new List<HospitalPortalBill>();
			foreach (var id in billIds)
			{
				var v = await _billing.BillAsync(ctx, id, nowUtc, ct);
				if (v == null || v.Lines.Count == 0) continue;
				var t = v.Totals;
				list.Add(new HospitalPortalBill(people[v.Bill.PatientId], v.Bill.ID, v.Bill.BillNo, v.Bill.OpenedAt, v.Bill.Status, t.Gross, t.Insurer, t.Patient + t.Vat, t.Paid, t.Due,
					v.Cover?.Payer, v.Cover?.PayerEn, v.Cover?.CopayPct, v.Currency ?? facility));
			}
			return list;
		}

		/// The medicines prescribed to the account's people in signed visits, newest first, each with its latest refill.
		public async Task<IReadOnlyList<HospitalPortalMedicine>> MedicinesAsync(BusinessContext ctx, int accountId, DateTime now, CancellationToken ct = default)
		{
			var people = (await _booking.ClinicalPeopleAsync(ctx, accountId, ct)).ToDictionary(p => p.PatientId);   // H251: full-scope people only
			if (people.Count == 0) return Array.Empty<HospitalPortalMedicine>();
			var ids = people.Keys.ToList();
			var since = now.Date.AddDays(-2 * HospitalPortalRecordsRules.RefillWithinDays);
			var lines = await (from l in _db.HospitalPrescriptionLines.AsNoTracking()
							   join e in _db.HospitalEncounters.AsNoTracking() on l.EncounterId equals e.ID
							   where l.CompanyId == ctx.CompanyId && e.CompanyId == ctx.CompanyId && ids.Contains(e.PatientId) && e.Status == HospitalEncounterStatus.Signed && e.StartedAt >= since
							   orderby e.StartedAt descending, l.SortOrder
							   select new { l, e.PatientId, EncounterId = e.ID, At = e.SignedAt ?? e.StartedAt }).Take(100).ToListAsync(ct);
			var docs = await DoctorsOfEncountersAsync(ctx, lines.Select(x => (int?)x.EncounterId), ct);
			var lineIds = lines.Select(x => x.l.ID).ToList();
			Dictionary<int, HospitalPortalRefill> last;
			bool ready = true;
			try
			{
				last = (await _db.Set<HospitalPortalRefill>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && lineIds.Contains(r.PrescriptionLineId)).ToListAsync(ct))
					.GroupBy(r => r.PrescriptionLineId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ID).First());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { last = new(); ready = false; }
			var items = await ItemNamesAsync(ctx, lines.Select(x => x.l.ItemId), ct);
			return lines.Select(x =>
			{
				var r = last.GetValueOrDefault(x.l.ID);
				var why = ready ? HospitalPortalRecordsRules.RefillRefusal(x.At, now, r?.Status == HospitalPortalRefillStatus.Open) : NoTables;
				return new HospitalPortalMedicine(x.l.ID, people[x.PatientId], DrugName(x.l, items), Words(x.l.Instructions, x.l.InstructionsEn), x.l.Quantity, x.At, docs.GetValueOrDefault(x.EncounterId), r, why);
			}).ToList();
		}

		/// The patient asks for a medicine again. Only for the account's own people, a verified account, once at a time.
		public async Task<HospitalResult> RefillAsync(BusinessContext ctx, int accountId, HospitalPortalRefillInput input, DateTime nowUtc, CancellationToken ct = default)
		{
			var acc = await _db.Set<HospitalPortalAccount>().AsNoTracking().FirstOrDefaultAsync(a => a.ID == accountId && a.CompanyId == ctx.CompanyId, ct);
			if (acc == null) return HospitalResult.Fail("not_found");
			if (HospitalPortalPolicy.AccountRefusal(acc.Status, isRequest: false) is string no) return HospitalResult.Fail(no);
			var med = (await MedicinesAsync(ctx, accountId, nowUtc.ToLocalTime(), ct)).FirstOrDefault(m => m.LineId == input.LineId);
			if (med == null) return HospitalResult.Fail("not_found");   // not one of this account's people's medicines
			if (med.CannotRefill is string why) return HospitalResult.Fail(why);
			var reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim();
			if (reason != null && !HospitalPortalRefillReason.All.Contains(reason)) return HospitalResult.Fail("Choose why you need it again");
			var row = new HospitalPortalRefill { CompanyId = ctx.CompanyId, AccountId = accountId, PatientId = med.Person.PatientId, PrescriptionLineId = med.LineId, Status = HospitalPortalRefillStatus.Open, Reason = reason, Note = Clip(input.Note, 500), CreatedAt = nowUtc };
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> WithdrawRefillAsync(BusinessContext ctx, int accountId, int refillId, DateTime nowUtc, CancellationToken ct = default)
		{
			try
			{
				int n = await _db.Set<HospitalPortalRefill>().Where(r => r.ID == refillId && r.CompanyId == ctx.CompanyId && r.AccountId == accountId && r.Status == HospitalPortalRefillStatus.Open)
					.ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, HospitalPortalRefillStatus.Withdrawn).SetProperty(r => r.HandledAt, nowUtc), ct);
				return n == 1 ? HospitalResult.Success(refillId) : HospitalResult.Fail("not_found");
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalResult.Fail(NoTables); }
		}

		// ====================================================================================
		// STAFF SIDE (`consult`)
		// ====================================================================================

		/// The release of this order, or null (none, or the tables are not there yet).
		public async Task<HospitalPortalRelease?> ReleaseOfAsync(BusinessContext ctx, string source, int orderId, CancellationToken ct = default) =>
			(await ReleasesAsync(ctx, source, new[] { orderId }, ct))?.FirstOrDefault();

		/// The doctor lets the patient see this result on the portal, with a word for them. Once per order.
		public async Task<HospitalResult> ReleaseAsync(BusinessContext ctx, HospitalPortalReleaseInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			string? status = input.Source switch
			{
				HospitalPortalResultSource.Lab => await _db.HospitalLabOrders.AsNoTracking().Where(o => o.ID == input.OrderId && o.CompanyId == ctx.CompanyId).Select(o => o.Status).FirstOrDefaultAsync(ct),
				HospitalPortalResultSource.Radiology => await _db.HospitalRadOrders.AsNoTracking().Where(o => o.ID == input.OrderId && o.CompanyId == ctx.CompanyId).Select(o => o.Status).FirstOrDefaultAsync(ct),
				_ => null,
			};
			if (status == null) return HospitalResult.Fail("not_found");
			if (HospitalPortalRecordsRules.ReleaseRefusal(input.Source, status) is string why) return HospitalResult.Fail(why);
			try
			{
				var existing = await _db.Set<HospitalPortalRelease>().FirstOrDefaultAsync(r => r.CompanyId == ctx.CompanyId && r.Source == input.Source && r.OrderId == input.OrderId, ct);
				if (existing != null && !input.Edit) return HospitalResult.Fail("This result is already released to the patient");
				if (existing != null)
				{
					// the words are corrected; the release itself (who and when) stays as it was
					existing.Note = Clip(input.Note, 500); existing.NoteEn = Clip(input.NoteEn, 500);
					await _db.SaveChangesAsync(ct);
					return HospitalResult.Success(existing.ID);
				}
				if (input.Edit) return HospitalResult.Fail("not_found");
				var row = new HospitalPortalRelease { CompanyId = ctx.CompanyId, Source = input.Source, OrderId = input.OrderId, ReleasedAt = nowUtc, ReleasedByEmployeeId = employeeId, Note = Clip(input.Note, 500), NoteEn = Clip(input.NoteEn, 500) };
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail("This result is already released to the patient"); }   // the unique index: a second doctor at the same moment
		}

		/// The refill requests for the clinic, open first, each with the patient and the prescribing doctor.
		public async Task<IReadOnlyList<HospitalPortalRefillRow>> RefillsAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Read, ct)) return Array.Empty<HospitalPortalRefillRow>();
			List<HospitalPortalRefill> rows;
			try { rows = await _db.Set<HospitalPortalRefill>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.Status != HospitalPortalRefillStatus.Withdrawn).OrderByDescending(r => r.ID).Take(200).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalPortalRefillRow>(); }
			var lineIds = rows.Select(r => r.PrescriptionLineId).Distinct().ToList();
			var lines = await (from l in _db.HospitalPrescriptionLines.AsNoTracking()
							   join e in _db.HospitalEncounters.AsNoTracking() on l.EncounterId equals e.ID
							   where l.CompanyId == ctx.CompanyId && e.CompanyId == ctx.CompanyId && lineIds.Contains(l.ID)
							   select new { l, EncounterId = e.ID, At = e.SignedAt ?? e.StartedAt }).ToDictionaryAsync(x => x.l.ID, ct);
			var patIds = rows.Select(r => r.PatientId).Distinct().ToList();
			var pats = await _db.HospitalPatients.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && patIds.Contains(p.ID)).ToDictionaryAsync(p => p.ID, ct);
			var docs = await DoctorsOfEncountersAsync(ctx, lines.Values.Select(x => (int?)x.EncounterId), ct);
			var items = await ItemNamesAsync(ctx, lines.Values.Select(x => x.l.ItemId), ct);
			return rows.Where(r => lines.ContainsKey(r.PrescriptionLineId) && pats.ContainsKey(r.PatientId))
				.OrderBy(r => r.Status == HospitalPortalRefillStatus.Open ? 0 : 1).ThenByDescending(r => r.ID)
				.Select(r =>
				{
					var x = lines[r.PrescriptionLineId]; var p = pats[r.PatientId];
					return new HospitalPortalRefillRow(r, p.ID, p.Name, p.NameEn, p.Mrn, p.PhotoPath, DrugName(x.l, items), Words(x.l.Instructions, x.l.InstructionsEn), x.l.Quantity, x.At, docs.GetValueOrDefault(x.EncounterId));
				}).ToList();
		}

		/// The clinic approves (the patient collects at the pharmacy) or declines, with the answer the patient reads.
		public async Task<HospitalResult> DecideRefillAsync(BusinessContext ctx, int refillId, HospitalPortalDecisionInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Consult, ct)) return HospitalResult.Fail("forbidden");
			HospitalPortalRefill? r;
			try { r = await _db.Set<HospitalPortalRefill>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == refillId && x.CompanyId == ctx.CompanyId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return HospitalResult.Fail(NoTables); }
			if (r == null) return HospitalResult.Fail("not_found");
			if (HospitalPortalRecordsRules.DecisionRefusal(r.Status, input.Approve, input.Answer) is string why) return HospitalResult.Fail(why);
			string status = input.Approve ? HospitalPortalRefillStatus.Approved : HospitalPortalRefillStatus.Declined;
			var answer = Clip(input.Answer, 500); var answerEn = Clip(input.AnswerEn, 500);
			// only while still open: a patient withdrawing at the same moment wins, and nothing is answered twice
			int n = await _db.Set<HospitalPortalRefill>().Where(x => x.ID == refillId && x.CompanyId == ctx.CompanyId && x.Status == HospitalPortalRefillStatus.Open)
				.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status).SetProperty(x => x.Answer, answer).SetProperty(x => x.AnswerEn, answerEn).SetProperty(x => x.HandledAt, nowUtc).SetProperty(x => x.HandledByEmployeeId, employeeId), ct);
			return n == 1 ? HospitalResult.Success(refillId) : HospitalResult.Fail("This request is already handled");
		}
	}
}
