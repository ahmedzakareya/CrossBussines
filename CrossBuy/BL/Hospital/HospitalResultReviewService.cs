using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H178 — the result follow-up inbox. The rules are HospitalResultReviewRules' (no database).
	//   · TO REVIEW: every verified lab order and signed imaging report of the last 90 days that nobody has reviewed,
	//     in its owner's inbox — the ordering doctor, or whoever it was handed to. Critical first, then abnormal, then
	//     normal; each has a time to be reviewed in (critical 24 h, abnormal 72 h, normal 7 days) and is OVERDUE past it.
	//   · AWAITING RESULT: the owner's orders with no result past their expected turnaround (lab: STAT 2 h, urgent 6 h,
	//     routine 48 h; imaging: STAT 4 h, urgent 24 h, routine 7 days).
	//   · Review: the owner (or the clinical lead, `manage`) records what was done — no action, patient informed,
	//     treatment changed, follow-up booked, repeated, referred — with a note; required to say more than "no action"
	//     for a critical result. Hand over: to another doctor, with a reason (going on leave, not my patient).
	//   · The clinical lead sees every owner's overdue results, and the results nobody owns.
	// ==========================================================================================
	public sealed record HospitalResultItem(string Kind, int OrderId, int PatientId, string Patient, string PatientEn, string Mrn, string Test, string TestEn,
		DateTime ResultedAt, string Severity, int? OwnerEmployeeId, string? Owner, string? Summary, DateTime DueAt, bool Overdue, string? HandReason);

	public sealed record HospitalAwaitingItem(string Kind, int OrderId, int PatientId, string Patient, string PatientEn, string Mrn, string Test, string TestEn,
		DateTime OrderedAt, string Priority, string Status, DateTime ExpectedBy);

	public sealed class HospitalResultReviewInput
	{
		public string Kind { get; set; } = HospitalResultKind.Lab;
		public string? Action { get; set; }
		public string? Note { get; set; }
	}

	public sealed class HospitalResultHandInput
	{
		public string Kind { get; set; } = HospitalResultKind.Lab;
		public int ToEmployeeId { get; set; }
		public string? Reason { get; set; }
	}

	public static class HospitalResultReviewRules
	{
		public const int WindowDays = 90;
		public const string Critical = "Critical", Abnormal = "Abnormal", Normal = "Normal";

		public static TimeSpan ReviewWithin(string severity) => severity switch { Critical => TimeSpan.FromHours(24), Abnormal => TimeSpan.FromHours(72), _ => TimeSpan.FromDays(7) };

		/// The expected turnaround of an order by its kind and priority.
		public static TimeSpan Turnaround(string kind, string priority) => (kind, priority) switch
		{
			(HospitalResultKind.Lab, HospitalLabPriority.Stat) => TimeSpan.FromHours(2),
			(HospitalResultKind.Lab, HospitalLabPriority.Urgent) => TimeSpan.FromHours(6),
			(HospitalResultKind.Lab, _) => TimeSpan.FromHours(48),
			(_, HospitalLabPriority.Stat) => TimeSpan.FromHours(4),
			(_, HospitalLabPriority.Urgent) => TimeSpan.FromHours(24),
			_ => TimeSpan.FromDays(7),
		};

		public static int Rank(string severity) => severity switch { Critical => 0, Abnormal => 1, _ => 2 };

		/// A lab order's severity from its flags; an imaging report's from its impression category.
		public static string LabSeverity(bool isCritical, IEnumerable<string> flags) =>
			isCritical || flags.Any(f => f is HospitalLabFlag.CriticalLow or HospitalLabFlag.CriticalHigh) ? Critical
			: flags.Any(f => f is HospitalLabFlag.Low or HospitalLabFlag.High) ? Abnormal : Normal;

		public static string RadSeverity(string? category) => category switch { "Critical" => Critical, "Normal" => Normal, _ => Abnormal };

		public static string? ReviewRefusal(string severity, HospitalResultReviewInput i)
		{
			if (!HospitalResultAction.All.Contains(i.Action ?? "")) return "Choose what was done about the result";
			if (severity == Critical && i.Action == HospitalResultAction.NoAction) return "A critical result needs more than \"no action\": say what was done";
			if (i.Action != HospitalResultAction.NoAction && string.IsNullOrWhiteSpace(i.Note)) return "Write a note on what was done";
			return null;
		}
	}

	public sealed class HospitalResultReviewService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalResultReviewService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalResultReview> Reviews(BusinessContext ctx) => _db.Set<HospitalResultReview>().Where(r => r.CompanyId == ctx.CompanyId);

		/// Every unreviewed result of the window; filtered to one owner, or the whole hospital (the lead's view).
		public async Task<IReadOnlyList<HospitalResultItem>> ToReviewAsync(BusinessContext ctx, int? ownerEmployeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var since = nowUtc.AddDays(-HospitalResultReviewRules.WindowDays);
			var reviews = await Reviews(ctx).AsNoTracking().ToListAsync(ct);
			var reviewed = reviews.Where(r => r.ReviewedAt != null).Select(r => (r.Kind, r.OrderId)).ToHashSet();
			var handed = reviews.Where(r => r.ReviewedAt == null).ToDictionary(r => (r.Kind, r.OrderId));

			var labs = await (from o in _db.Set<HospitalLabOrder>().AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							  where o.CompanyId == ctx.CompanyId && o.Status == HospitalLabStatus.Verified && o.VerifiedAt >= since
							  select new { o.ID, o.PatientId, p.Name, p.NameEn, p.Mrn, Test = o.Name, TestEn = o.NameEn, At = o.VerifiedAt!.Value, o.IsCritical, o.OrderedByEmployeeId }).ToListAsync(ct);
			var labIds = labs.Select(l => l.ID).ToList();
			var flags = await _db.Set<HospitalLabResult>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && labIds.Contains(r.LabOrderId))
				.Select(r => new { r.LabOrderId, r.Flag, r.NameEn, r.Name, r.Value, r.Unit }).ToListAsync(ct);
			var rads = await (from o in _db.Set<HospitalRadOrder>().AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							  where o.CompanyId == ctx.CompanyId && o.Status == HospitalRadStatus.Signed && o.SignedAt >= since
							  select new { o.ID, o.PatientId, p.Name, p.NameEn, p.Mrn, Test = o.Name, TestEn = o.NameEn, At = o.SignedAt!.Value, o.ImpressionCategory, o.Impression, o.ImpressionEn, o.OrderedByEmployeeId }).ToListAsync(ct);

			var items = new List<HospitalResultItem>();
			foreach (var l in labs.Where(l => !reviewed.Contains((HospitalResultKind.Lab, l.ID))))
			{
				var mine = flags.Where(f => f.LabOrderId == l.ID).ToList();
				var sev = HospitalResultReviewRules.LabSeverity(l.IsCritical, mine.Select(f => f.Flag));
				var off = mine.Where(f => f.Flag != HospitalLabFlag.Normal).Select(f => DisplayName.Of(f.Name, f.NameEn) + " " + f.Value?.ToString("0.##") + (f.Unit == null ? "" : " " + f.Unit) + " (" + f.Flag + ")").ToList();
				handed.TryGetValue((HospitalResultKind.Lab, l.ID), out var h);
				items.Add(Item(HospitalResultKind.Lab, l.ID, l.PatientId, l.Name, l.NameEn ?? l.Name, l.Mrn, l.Test, l.TestEn, l.At, sev, h?.OwnerEmployeeId ?? l.OrderedByEmployeeId, off.Count == 0 ? null : string.Join(" · ", off), h?.HandReason, nowUtc));
			}
			foreach (var r in rads.Where(r => !reviewed.Contains((HospitalResultKind.Rad, r.ID))))
			{
				handed.TryGetValue((HospitalResultKind.Rad, r.ID), out var h);
				var imp = DisplayName.Of(r.Impression ?? "", r.ImpressionEn);
				items.Add(Item(HospitalResultKind.Rad, r.ID, r.PatientId, r.Name, r.NameEn ?? r.Name, r.Mrn, r.Test, r.TestEn, r.At, HospitalResultReviewRules.RadSeverity(r.ImpressionCategory),
					h?.OwnerEmployeeId ?? r.OrderedByEmployeeId, string.IsNullOrWhiteSpace(imp) ? null : imp[..Math.Min(200, imp.Length)], h?.HandReason, nowUtc));
			}
			var filtered = ownerEmployeeId == null ? items : items.Where(i => i.OwnerEmployeeId == ownerEmployeeId).ToList();
			var owners = filtered.Where(i => i.OwnerEmployeeId != null).Select(i => i.OwnerEmployeeId!.Value).Distinct().ToList();
			var names = await _db.Employee.AsNoTracking().Where(e => owners.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
			return filtered.Select(i => i with { Owner = names.FirstOrDefault(n => n.ID == i.OwnerEmployeeId) is { } n ? DisplayName.Of(n.FullName ?? "", n.FullNameEn) : null })
				.OrderBy(i => HospitalResultReviewRules.Rank(i.Severity)).ThenByDescending(i => i.Overdue).ThenBy(i => i.ResultedAt).ToList();
		}

		private static HospitalResultItem Item(string kind, int id, int patientId, string name, string nameEn, string mrn, string test, string testEn, DateTime at, string sev, int? owner, string? summary, string? hand, DateTime nowUtc)
		{
			var due = at + HospitalResultReviewRules.ReviewWithin(sev);
			return new HospitalResultItem(kind, id, patientId, name, nameEn, mrn, test, testEn, at, sev, owner, null, summary, due, nowUtc > due, hand);
		}

		/// This owner's orders still without a result past their expected turnaround.
		public async Task<IReadOnlyList<HospitalAwaitingItem>> AwaitingAsync(BusinessContext ctx, int ownerEmployeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var since = nowUtc.AddDays(-HospitalResultReviewRules.WindowDays);
			var labs = await (from o in _db.Set<HospitalLabOrder>().AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							  where o.CompanyId == ctx.CompanyId && o.OrderedByEmployeeId == ownerEmployeeId && o.OrderedAt >= since && o.Status != HospitalLabStatus.Verified
							  select new { o.ID, o.PatientId, p.Name, p.NameEn, p.Mrn, Test = o.Name, TestEn = o.NameEn, o.OrderedAt, o.Priority, o.Status }).ToListAsync(ct);
			var rads = await (from o in _db.Set<HospitalRadOrder>().AsNoTracking()
							  join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
							  where o.CompanyId == ctx.CompanyId && o.OrderedByEmployeeId == ownerEmployeeId && o.OrderedAt >= since && o.Status != HospitalRadStatus.Signed
							  select new { o.ID, o.PatientId, p.Name, p.NameEn, p.Mrn, Test = o.Name, TestEn = o.NameEn, o.OrderedAt, o.Priority, o.Status }).ToListAsync(ct);
			var all = labs.Select(l => new HospitalAwaitingItem(HospitalResultKind.Lab, l.ID, l.PatientId, l.Name, l.NameEn ?? l.Name, l.Mrn, l.Test, l.TestEn, l.OrderedAt, l.Priority, l.Status, l.OrderedAt + HospitalResultReviewRules.Turnaround(HospitalResultKind.Lab, l.Priority)))
				.Concat(rads.Select(r => new HospitalAwaitingItem(HospitalResultKind.Rad, r.ID, r.PatientId, r.Name, r.NameEn ?? r.Name, r.Mrn, r.Test, r.TestEn, r.OrderedAt, r.Priority, r.Status, r.OrderedAt + HospitalResultReviewRules.Turnaround(HospitalResultKind.Rad, r.Priority))));
			return all.Where(a => nowUtc > a.ExpectedBy).OrderBy(a => a.ExpectedBy).ToList();
		}

		/// The result as it stands: its patient, its severity, and its owner now; null when not a finished result here.
		private async Task<(int PatientId, string Severity, int? Owner)?> ResultAsync(BusinessContext ctx, string kind, int id, CancellationToken ct)
		{
			var handed = await Reviews(ctx).AsNoTracking().Where(r => r.Kind == kind && r.OrderId == id).Select(r => (int?)r.OwnerEmployeeId).FirstOrDefaultAsync(ct);
			if (kind == HospitalResultKind.Lab)
			{
				var o = await _db.Set<HospitalLabOrder>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.Status == HospitalLabStatus.Verified)
					.Select(x => new { x.PatientId, x.IsCritical, x.OrderedByEmployeeId }).FirstOrDefaultAsync(ct);
				if (o == null) return null;
				var f = await _db.Set<HospitalLabResult>().AsNoTracking().Where(r => r.LabOrderId == id && r.CompanyId == ctx.CompanyId).Select(r => r.Flag).ToListAsync(ct);
				return (o.PatientId, HospitalResultReviewRules.LabSeverity(o.IsCritical, f), handed ?? o.OrderedByEmployeeId);
			}
			if (kind == HospitalResultKind.Rad)
			{
				var o = await _db.Set<HospitalRadOrder>().AsNoTracking().Where(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.Status == HospitalRadStatus.Signed)
					.Select(x => new { x.PatientId, x.ImpressionCategory, x.OrderedByEmployeeId }).FirstOrDefaultAsync(ct);
				return o == null ? null : (o.PatientId, HospitalResultReviewRules.RadSeverity(o.ImpressionCategory), handed ?? o.OrderedByEmployeeId);
			}
			return null;
		}

		/// The owner, or the clinical lead, may act on a result.
		private async Task<bool> MayActAsync(BusinessContext ctx, int? owner, int? employeeId, CancellationToken ct) =>
			employeeId is int e && await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) && (owner == e || await _access.CanAsync(ctx, HospitalActions.Manage, null, ct));

		public async Task<HospitalResult> ReviewAsync(BusinessContext ctx, int orderId, HospitalResultReviewInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var r = await ResultAsync(ctx, input.Kind, orderId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (!await MayActAsync(ctx, r.Value.Owner, employeeId, ct)) return HospitalResult.Fail("Only the result's owner or the clinical lead reviews it");
			if (HospitalResultReviewRules.ReviewRefusal(r.Value.Severity, input) is string why) return HospitalResult.Fail(why);
			var row = await Reviews(ctx).FirstOrDefaultAsync(x => x.Kind == input.Kind && x.OrderId == orderId, ct);
			if (row?.ReviewedAt != null) return HospitalResult.Fail("This result is already reviewed");
			if (row == null) _db.Add(row = new HospitalResultReview { CompanyId = ctx.CompanyId, PatientId = r.Value.PatientId, Kind = input.Kind, OrderId = orderId, OwnerEmployeeId = r.Value.Owner ?? employeeId!.Value });
			row.ReviewedAt = nowUtc; row.ReviewedByEmployeeId = employeeId; row.Action = input.Action;
			row.Note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim()[..Math.Min(500, input.Note.Trim().Length)];
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> HandOverAsync(BusinessContext ctx, int orderId, HospitalResultHandInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var r = await ResultAsync(ctx, input.Kind, orderId, ct);
			if (r == null) return HospitalResult.Fail("not_found");
			if (!await MayActAsync(ctx, r.Value.Owner, employeeId, ct)) return HospitalResult.Fail("Only the result's owner or the clinical lead hands it over");
			if (string.IsNullOrWhiteSpace(input.Reason)) return HospitalResult.Fail("Say why it is handed over");
			if (input.ToEmployeeId == r.Value.Owner) return HospitalResult.Fail("It is already theirs");
			if (!await _db.HospitalPractitioners.AnyAsync(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == input.ToEmployeeId && p.IsActive, ct)) return HospitalResult.Fail("Choose a doctor of this hospital");
			var row = await Reviews(ctx).FirstOrDefaultAsync(x => x.Kind == input.Kind && x.OrderId == orderId, ct);
			if (row?.ReviewedAt != null) return HospitalResult.Fail("This result is already reviewed");
			if (row == null) _db.Add(row = new HospitalResultReview { CompanyId = ctx.CompanyId, PatientId = r.Value.PatientId, Kind = input.Kind, OrderId = orderId });
			row.OwnerEmployeeId = input.ToEmployeeId; row.HandedAt = nowUtc; row.HandedByEmployeeId = employeeId;
			row.HandReason = input.Reason.Trim()[..Math.Min(300, input.Reason.Trim().Length)];
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalNursingService.ConcurrentChange); }
			return HospitalResult.Success(row.ID);
		}

		/// The doctors a result can be handed to.
		public async Task<IReadOnlyList<(int EmployeeId, string Name)>> DoctorsAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.IsActive && p.EmployeeId != null).OrderBy(p => p.Name)
				.Select(p => new { p.EmployeeId, p.Name, p.NameEn }).ToListAsync(ct)).Select(p => (p.EmployeeId!.Value, DisplayName.Of(p.Name, p.NameEn))).ToList();
	}
}
