using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H177 — addenda and corrections on locked clinical records (from the review: "signing and correction —
	// addenda, not silent edits"). The rules are HospitalAddendumRules' (no database).
	//   · Only a LOCKED record takes one: a signed visit, a signed drawing, a closed labour. An open record is edited
	//     itself.
	//   · Writing one needs `consult`; its author is the signed-in person; a correction states its reason.
	//   · Nothing here edits or deletes: an addendum to an addendum is another addendum.
	// ==========================================================================================
	public sealed class HospitalAddendumInput
	{
		public string Kind { get; set; } = HospitalAddendumKind.Addendum;
		public string? Section { get; set; }
		public string? Text { get; set; }
		public string? Reason { get; set; }
	}

	public sealed record HospitalAddendumRow(HospitalClinicalAddendum Addendum, string Author, string? AuthorPhoto);

	/// What the partial needs: the record, its addenda, and whether this person may add one now.
	public sealed record HospitalAddendaBlock(string Subject, int SubjectId, IReadOnlyList<HospitalAddendumRow> Rows, bool CanAdd, bool Locked);

	public static class HospitalAddendumRules
	{
		public const int MaxText = 2000;

		public static string? Refusal(bool locked, HospitalAddendumInput i)
		{
			if (!locked) return "This record is not signed yet: edit it itself";
			if (!HospitalAddendumKind.All.Contains(i.Kind)) return "Choose an addendum or a correction";
			if (!string.IsNullOrEmpty(i.Section) && !HospitalAddendumSection.All.Contains(i.Section)) return "Choose the part of the record";
			if (string.IsNullOrWhiteSpace(i.Text)) return "Write the addendum";
			if (i.Text.Trim().Length > MaxText) return "An addendum is 2000 characters at most";
			if (i.Kind == HospitalAddendumKind.Correction && string.IsNullOrWhiteSpace(i.Reason)) return "A correction says why the record was wrong";
			return null;
		}

		/// The paper's lines: "Correction · 2026-11-18 10:30 · Dr X — text (reason)".
		public static string PaperLine(HospitalAddendumRow r, bool ar, Func<DateTime, string> local)
		{
			var a = r.Addendum;
			string kind = a.Kind == HospitalAddendumKind.Correction ? (ar ? "تصحيح" : "Correction") : (ar ? "إضافة" : "Addendum");
			string reason = a.Reason == null ? "" : (ar ? " (السبب: " : " (reason: ") + a.Reason + ")";
			return kind + " · " + local(a.CreatedAt) + " · " + r.Author + " — " + a.Text + reason;
		}
	}

	public sealed class HospitalAddendumService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalAddendumService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		/// The record's patient and whether it is locked; null when it is not this company's.
		private async Task<(int PatientId, bool Locked)?> RecordAsync(BusinessContext ctx, string subject, int id, CancellationToken ct)
		{
			switch (subject)
			{
				case HospitalAddendumSubject.Encounter:
				{
					var e = await _db.Set<HospitalEncounter>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId).Select(x => new { x.PatientId, x.Status }).FirstOrDefaultAsync(ct);
					return e == null ? null : (e.PatientId, e.Status == HospitalEncounterStatus.Signed);
				}
				case HospitalAddendumSubject.Canvas:
				{
					var s = await _db.Set<HospitalCanvasSheet>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId).Select(x => new { x.PatientId, x.Status }).FirstOrDefaultAsync(ct);
					return s == null ? null : (s.PatientId, s.Status == HospitalCanvasStatus.Signed);
				}
				case HospitalAddendumSubject.Labour:
				{
					var l = await _db.Set<HospitalLabour>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId).Select(x => new { x.PatientId, x.Status }).FirstOrDefaultAsync(ct);
					return l == null ? null : (l.PatientId, l.Status == HospitalLabourStatus.Closed);
				}
				default: return null;
			}
		}

		public async Task<HospitalResult> AddAsync(BusinessContext ctx, string subject, int id, HospitalAddendumInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || employeeId is not int emp) return HospitalResult.Fail("forbidden");
			var rec = await RecordAsync(ctx, subject, id, ct);
			if (rec == null) return HospitalResult.Fail("not_found");
			if (HospitalAddendumRules.Refusal(rec.Value.Locked, input) is string why) return HospitalResult.Fail(why);
			var row = new HospitalClinicalAddendum
			{
				CompanyId = ctx.CompanyId, PatientId = rec.Value.PatientId, Subject = subject, SubjectId = id, Kind = input.Kind,
				Section = string.IsNullOrEmpty(input.Section) ? null : input.Section, Text = input.Text!.Trim(),
				Reason = string.IsNullOrWhiteSpace(input.Reason) ? null : input.Reason.Trim()[..Math.Min(300, input.Reason.Trim().Length)],
				CreatedAt = nowUtc, CreatedByEmployeeId = emp,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		public async Task<IReadOnlyList<HospitalAddendumRow>> ListAsync(BusinessContext ctx, string subject, int id, CancellationToken ct = default)
		{
			try
			{
				var rows = await _db.Set<HospitalClinicalAddendum>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.Subject == subject && a.SubjectId == id)
					.OrderBy(a => a.CreatedAt).ThenBy(a => a.ID).ToListAsync(ct);
				var ids = rows.Select(r => r.CreatedByEmployeeId).Distinct().ToList();
				var names = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				var photos = await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(_db, ctx, ids, ct);
				return rows.Select(r => new HospitalAddendumRow(r, names.FirstOrDefault(n => n.ID == r.CreatedByEmployeeId) is { } n ? DisplayName.Of(n.FullName ?? "", n.FullNameEn) : "—",
					photos.GetValueOrDefault(r.CreatedByEmployeeId))).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalAddendumRow>(); }   // the table not created yet
		}

		/// The block a record's page renders.
		public async Task<HospitalAddendaBlock> BlockAsync(BusinessContext ctx, string subject, int id, CancellationToken ct = default)
		{
			var rec = await RecordAsync(ctx, subject, id, ct);
			bool locked = rec?.Locked == true;
			return new HospitalAddendaBlock(subject, id, await ListAsync(ctx, subject, id, ct), locked && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct), locked);
		}
	}
}
