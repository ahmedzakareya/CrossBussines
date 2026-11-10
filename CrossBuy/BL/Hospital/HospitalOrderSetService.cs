using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H74 — order sets. The administrator writes the hospital's sets; a doctor places one for an inpatient.
	// Placing checks every chosen line first (a line the order screen would refuse stops the set before anything is
	// written), then runs each medicine through the drug-safety check against what the patient already takes AND the
	// set's earlier medicines; a stopping alert comes back for one reason; then the medicines are ordered, the tasks put on
	// the ward board, each gone-past alert recorded, and the use kept.
	// ==========================================================================================
	public sealed class HospitalOrderSetInput
	{
		public int? Id { get; set; }
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? NameEn { get; set; }
		public int? DepartmentId { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalOrderSetItemInput
	{
		public int? Id { get; set; }
		public int SetId { get; set; }
		public string? Kind { get; set; }
		public string? Drug { get; set; }
		public string? Dose { get; set; }
		public string? Route { get; set; }
		public string? Hours { get; set; }
		public bool AsNeeded { get; set; }
		public string? Note { get; set; }
		public string? TitleAr { get; set; }
		public string? TitleEn { get; set; }
		public int? DueInHours { get; set; }
		public bool Preselected { get; set; } = true;
		public int SortOrder { get; set; }
		public int? ServiceId { get; set; }
	}

	public sealed class HospitalOrderSetApplyInput
	{
		public int AdmissionId { get; set; }
		public List<int> ItemIds { get; set; } = new();
		public string? OverrideReason { get; set; }
	}

	public sealed record HospitalOrderSetRow(HospitalOrderSet Set, int Items, int Uses, string? Department, string? DepartmentEn);
	public sealed record HospitalOrderSetView(HospitalOrderSet Set, IReadOnlyList<HospitalOrderSetItem> Items);
	/// An alert met while placing a set: which line, and the alert.
	public sealed record HospitalSetAlert(int ItemId, string Drug, HospitalDrugAlert Alert);

	public static class HospitalOrderSetRules
	{
		private static readonly Regex CodeShape = new(@"^[A-Za-z0-9][A-Za-z0-9\-]{1,19}$", RegexOptions.Compiled);

		public static string? SetRefusal(HospitalOrderSetInput i)
		{
			if (!CodeShape.IsMatch((i.Code ?? "").Trim())) return "The code is 2 to 20 letters, digits or dashes";
			if (string.IsNullOrWhiteSpace(i.Name) || string.IsNullOrWhiteSpace(i.NameEn)) return "Write the name in Arabic and in English";
			if (i.Name.Trim().Length > 150 || i.NameEn.Trim().Length > 150) return "The name is too long";
			return null;
		}

		public static string? ItemRefusal(HospitalOrderSetItemInput i)
		{
			if (i.Kind == HospitalOrderSetItemKind.Medication)
				return HospitalNursingRules.OrderRefusal(i.Drug ?? "", i.Dose ?? "", i.Route ?? "", i.Hours, i.AsNeeded);
			if (i.Kind is HospitalOrderSetItemKind.Lab or HospitalOrderSetItemKind.Imaging) return i.ServiceId is > 0 ? null : "Choose the test or exam";
			if (i.Kind != HospitalOrderSetItemKind.Task) return "Choose medicine or nursing task";
			if (string.IsNullOrWhiteSpace(i.TitleAr) && string.IsNullOrWhiteSpace(i.TitleEn)) return "Write the task";
			if ((i.TitleAr?.Trim().Length ?? 0) > 90 || (i.TitleEn?.Trim().Length ?? 0) > 90) return "A task is at most 90 characters";
			if ((i.TitleAr ?? "").Contains('|') || (i.TitleEn ?? "").Contains('|')) return "A task cannot contain the | sign";
			if (i.DueInHours is not (>= 0 and <= 72)) return "A task is due 0 to 72 hours after the set is placed";
			return null;
		}

		/// The board's task title: both languages, shown in the reader's (HospitalNews2Text.TaskText).
		public static string TaskCode(string? ar, string? en) => "OS|" + (ar ?? "").Trim() + "|" + (en ?? "").Trim();

		/// A stored line as the order screen would take it.
		public static HospitalOrderSetItemInput AsInput(HospitalOrderSetItem x) => new()
		{
			Id = x.ID, SetId = x.SetId, Kind = x.Kind, Drug = x.Drug, Dose = x.Dose, Route = x.Route, Hours = x.Hours, AsNeeded = x.AsNeeded, Note = x.Note,
			TitleAr = x.TitleAr, TitleEn = x.TitleEn, DueInHours = x.DueInHours, Preselected = x.Preselected, SortOrder = x.SortOrder,
			ServiceId = x.ServiceId,
		};
	}

	public sealed class HospitalOrderSetService
	{
		public const string NoTables = "Order sets are not installed yet (hospital_h74_order_sets.sql).";
		public const string MeetsAlert = "This order set meets a safety alert";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalOrderSetService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }
		private static bool Missing(Microsoft.Data.SqlClient.SqlException ex) => ex.Number is 207 or 208;
		private static string? Clip(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim()[..Math.Min(max, s.Trim().Length)];

		// ---------------- writing the sets (the administrator) ----------------

		public async Task<HospitalResult> SaveSetAsync(BusinessContext ctx, HospitalOrderSetInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalOrderSetRules.SetRefusal(input) is string why) return HospitalResult.Fail(why);
			if (input.DepartmentId is int d && !await _db.HospitalDepartments.AnyAsync(x => x.ID == d && x.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			try
			{
				var set = _db.Set<HospitalOrderSet>();
				var code = input.Code!.Trim().ToUpperInvariant();
				if (await set.AnyAsync(x => x.CompanyId == ctx.CompanyId && x.Code == code && x.ID != (input.Id ?? 0), ct)) return HospitalResult.Fail("This code is already used");
				HospitalOrderSet? row;
				if (input.Id is int id) { row = await set.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else { row = new HospitalOrderSet { CompanyId = ctx.CompanyId }; set.Add(row); }
				row.Code = code; row.Name = Clip(input.Name, 150)!; row.NameEn = Clip(input.NameEn, 150)!; row.DepartmentId = input.DepartmentId; row.IsActive = input.IsActive;
				row.UpdatedAt = nowUtc; row.UpdatedByEmployeeId = employeeId;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> SaveItemAsync(BusinessContext ctx, HospitalOrderSetItemInput input, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			if (HospitalOrderSetRules.ItemRefusal(input) is string why) return HospitalResult.Fail(why);
			try
			{
				if (!await _db.Set<HospitalOrderSet>().AnyAsync(s => s.ID == input.SetId && s.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
				var items = _db.Set<HospitalOrderSetItem>();
				HospitalOrderSetItem? row;
				if (input.Id is int id) { row = await items.FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId && x.SetId == input.SetId, ct); if (row == null) return HospitalResult.Fail("not_found"); }
				else
				{
					int next = (await items.Where(x => x.CompanyId == ctx.CompanyId && x.SetId == input.SetId).Select(x => (int?)x.SortOrder).MaxAsync(ct) ?? 0) + 1;
					row = new HospitalOrderSetItem { CompanyId = ctx.CompanyId, SetId = input.SetId, SortOrder = next }; items.Add(row);
				}
				bool med = input.Kind == HospitalOrderSetItemKind.Medication;
				row.Kind = input.Kind!;
				row.Drug = med ? Clip(input.Drug, 200) : null; row.Dose = med ? Clip(input.Dose, 60) : null; row.Route = med ? input.Route : null;
				row.Hours = med && !input.AsNeeded ? HospitalNursingRules.FormatHours(HospitalNursingRules.ParseHours(input.Hours)!) : null; row.AsNeeded = med && input.AsNeeded;
				row.Note = Clip(input.Note, 300);
				row.TitleAr = med ? null : Clip(input.TitleAr, 90); row.TitleEn = med ? null : Clip(input.TitleEn, 90); row.DueInHours = med ? null : input.DueInHours;
				row.Preselected = input.Preselected;
				bool diag = input.Kind is HospitalOrderSetItemKind.Lab or HospitalOrderSetItemKind.Imaging;
				if (diag)
				{
					var cat = input.Kind == HospitalOrderSetItemKind.Lab ? HospitalServiceCategories.Lab : HospitalServiceCategories.Radiology;
					if (!await _db.HospitalServices.AnyAsync(s => s.ID == input.ServiceId && s.CompanyId == ctx.CompanyId && s.Category == cat, ct)) return HospitalResult.Fail("not_found");
				}
				row.ServiceId = diag ? input.ServiceId : null;
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(row.ID);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		public async Task<HospitalResult> RemoveItemAsync(BusinessContext ctx, int itemId, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			try
			{
				var row = await _db.Set<HospitalOrderSetItem>().FirstOrDefaultAsync(x => x.ID == itemId && x.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				_db.Remove(row);
				await _db.SaveChangesAsync(ct);
				return HospitalResult.Success(itemId);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return HospitalResult.Fail(NoTables); }
		}

		// ---------------- reading ----------------

		public async Task<(List<HospitalOrderSetRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, bool activeOnly, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var sets = _db.Set<HospitalOrderSet>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId);
				if (activeOnly) sets = sets.Where(s => s.IsActive);
				foreach (var t in (q ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)) { var term = t.Trim(); sets = sets.Where(s => s.Code.Contains(term) || s.Name.Contains(term) || s.NameEn.Contains(term)); }
				int total = await sets.CountAsync(ct);
				var page1 = await sets.OrderBy(s => s.Code).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				var ids = page1.Select(s => s.ID).ToList();
				var counts = await _db.Set<HospitalOrderSetItem>().AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId && ids.Contains(i.SetId)).GroupBy(i => i.SetId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var uses = await _db.Set<HospitalOrderSetUse>().AsNoTracking().Where(u => u.CompanyId == ctx.CompanyId && ids.Contains(u.SetId)).GroupBy(u => u.SetId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
				var depIds = page1.Where(s => s.DepartmentId != null).Select(s => s.DepartmentId!.Value).Distinct().ToList();
				var deps = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && depIds.Contains(d.ID)).ToDictionaryAsync(d => d.ID, ct);
				return (page1.Select(s => new HospitalOrderSetRow(s, counts.GetValueOrDefault(s.ID), uses.GetValueOrDefault(s.ID),
					s.DepartmentId is int d && deps.TryGetValue(d, out var dd) ? dd.Name : null, s.DepartmentId is int d2 && deps.TryGetValue(d2, out var de) ? de.NameEn : null)).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (new(), 0, false); }
		}

		public async Task<HospitalOrderSetView?> SetAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			try
			{
				var s = await _db.Set<HospitalOrderSet>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyId == ctx.CompanyId, ct);
				if (s == null) return null;
				var items = await _db.Set<HospitalOrderSetItem>().AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId && i.SetId == id).OrderBy(i => i.SortOrder).ThenBy(i => i.ID).ToListAsync(ct);
				return new(s, items);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return null; }
		}

		/// The active sets with their lines (the nursing station's "order set" window).
		public async Task<IReadOnlyList<HospitalOrderSetView>> ActiveAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var sets = await _db.Set<HospitalOrderSet>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && s.IsActive).OrderBy(s => s.Code).ToListAsync(ct);
				var ids = sets.Select(s => s.ID).ToList();
				var items = await _db.Set<HospitalOrderSetItem>().AsNoTracking().Where(i => i.CompanyId == ctx.CompanyId && ids.Contains(i.SetId)).OrderBy(i => i.SortOrder).ThenBy(i => i.ID).ToListAsync(ct);
				return sets.Select(s => new HospitalOrderSetView(s, items.Where(i => i.SetId == s.ID).ToList())).Where(v => v.Items.Count > 0).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return Array.Empty<HospitalOrderSetView>(); }
		}

		// ---------------- placing a set (the doctor) ----------------

		/// Places the chosen lines of a set for an admission. Alerts that stop come back unless a reason is given; the
		/// result's id is how many lines were placed.
		public async Task<(HospitalResult Result, IReadOnlyList<HospitalSetAlert> Alerts)> ApplyAsync(BusinessContext ctx, int setId, HospitalOrderSetApplyInput input,
			HospitalNursingService nursing, HospitalDrugSafetyService safety, int? employeeId, DateTime nowUtc, CancellationToken ct = default
			, HospitalInpatientOrderService? diagnostics = null
			)
		{
			var none = Array.Empty<HospitalSetAlert>();
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct)) return (HospitalResult.Fail("forbidden"), none);
			var view = await SetAsync(ctx, setId, ct);
			if (view == null || !view.Set.IsActive) return (HospitalResult.Fail("not_found"), none);
			var adm = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(a => a.ID == input.AdmissionId && a.CompanyId == ctx.CompanyId, ct);
			if (adm == null) return (HospitalResult.Fail("not_found"), none);
			if (adm.Status != HospitalAdmissionStatus.Active) return (HospitalResult.Fail("This admission is not active"), none);
			var chosen = view.Items.Where(i => input.ItemIds.Contains(i.ID)).ToList();
			if (chosen.Count == 0) return (HospitalResult.Fail("Choose at least one line"), none);
			// a line the order screen would refuse stops the whole set before anything is written
			foreach (var i in chosen)
				if (HospitalOrderSetRules.ItemRefusal(HospitalOrderSetRules.AsInput(i)) is string bad) return (HospitalResult.Fail(bad), none);

			var meds = chosen.Where(i => i.Kind == HospitalOrderSetItemKind.Medication).ToList();
			var alerts = new List<HospitalSetAlert>();
			for (int k = 0; k < meds.Count; k++)
			{
				var m = meds[k];
				foreach (var a in await safety.CheckOrderAsync(ctx, adm.ID, m.Drug!, m.Dose, m.Route!, m.Hours, m.AsNeeded, ct, meds.Take(k).Select(x => x.Drug!)))
					alerts.Add(new(m.ID, m.Drug!, a));
			}
			if (alerts.Any(a => a.Alert.Blocks) && string.IsNullOrWhiteSpace(input.OverrideReason)) return (HospitalResult.Fail(MeetsAlert), alerts);

			int placed = 0;
			foreach (var m in meds)
			{
				var r = await nursing.AddOrderAsync(ctx, new HospitalMedOrderInput { AdmissionId = adm.ID, Drug = m.Drug!, Dose = m.Dose!, Route = m.Route!, Hours = m.Hours, AsNeeded = m.AsNeeded, Note = m.Note }, employeeId, nowUtc, ct);
				if (!r.Ok) return (HospitalResult.Fail(r.Error ?? "The change could not be saved"), alerts);
				placed++;
				var mine = alerts.Where(a => a.ItemId == m.ID && a.Alert.Blocks).Select(a => a.Alert).ToList();
				if (mine.Count > 0) await safety.RecordOverridesAsync(ctx, adm.PatientId, adm.ID, "MedOrder", r.Id!.Value, mine, input.OverrideReason!, employeeId, nowUtc, ct);
			}
			try
			{
				foreach (var t in chosen.Where(i => i.Kind == HospitalOrderSetItemKind.Task))
				{
					_db.Add(new HospitalNursingTask
					{
						CompanyId = ctx.CompanyId, AdmissionId = adm.ID, PatientId = adm.PatientId, Title = HospitalOrderSetRules.TaskCode(t.TitleAr, t.TitleEn),
						DueAt = nowUtc.AddHours(t.DueInHours ?? 0), CreatedAt = nowUtc, CreatedByEmployeeId = employeeId,
					});
					placed++;
				}
				var diagIds = chosen.Where(i => i.Kind is HospitalOrderSetItemKind.Lab or HospitalOrderSetItemKind.Imaging && i.ServiceId != null).Select(i => i.ServiceId!.Value).ToList();
				if (diagIds.Count > 0 && diagnostics != null)
				{
					await _db.SaveChangesAsync(ct);   // the tasks first: the diagnostics service saves on its own
					var (dr, _) = await diagnostics.OrderAsync(ctx, new HospitalInpatientOrderInput { AdmissionId = adm.ID, ServiceIds = diagIds }, employeeId, nowUtc, ct);
					if (!dr.Ok) return (HospitalResult.Fail(dr.Error ?? "The change could not be saved"), alerts);
					placed += dr.Id ?? 0;
				}
				_db.Add(new HospitalOrderSetUse { CompanyId = ctx.CompanyId, SetId = setId, AdmissionId = adm.ID, PatientId = adm.PatientId, ItemsPlaced = placed, ItemsInSet = view.Items.Count, PlacedAt = nowUtc, ByEmployeeId = employeeId });
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (Missing(ex)) { return (HospitalResult.Fail(NoTables), alerts); }
			return (HospitalResult.Success(placed), alerts);
		}
	}
}
