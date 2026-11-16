using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H147 — the pharmacy's dispensing labels as rows a Report Studio template lays out (owner rule: nothing
	// prints except through Report Studio). ONE row per medicine that left the pharmacy for the patient, so the designed
	// layout (a Detail band the size of one label page) prints a label per medicine. What a label states is what is in
	// the patient's hands:
	//   · the quantity is what was given less what the patient brought back (H146), and a medicine fully returned has
	//     no label; nothing is labelled before it is dispensed;
	//   · the batch and the expiry are the ones the store moved for this line, never the shelf's next batch;
	//   · the medicine and the instructions are the prescription's own words, in the paper's language;
	//   · the storage and safety warnings come from the drug master (cold chain, high-alert, controlled, hazardous),
	//     and every label tells the patient to keep it from children.
	// Tenancy is checked on the dispense the id names: another company's id is an empty set.
	// ============================================================================================
	public static class HospitalRxLabelText
	{
		public static string? Form(string? form, bool ar) => form switch
		{
			null or "" => null,
			HospitalDrugForm.Tablet => ar ? "أقراص" : "Tablets",
			HospitalDrugForm.Capsule => ar ? "كبسولات" : "Capsules",
			HospitalDrugForm.Syrup => ar ? "شراب" : "Syrup",
			HospitalDrugForm.Suspension => ar ? "معلّق" : "Suspension",
			HospitalDrugForm.Solution => ar ? "محلول" : "Solution",
			HospitalDrugForm.Drops => ar ? "نقط" : "Drops",
			HospitalDrugForm.Injection => ar ? "حقن" : "Injection",
			HospitalDrugForm.Infusion => ar ? "محلول وريدي" : "Infusion",
			HospitalDrugForm.Cream => ar ? "كريم" : "Cream",
			HospitalDrugForm.Ointment => ar ? "مرهم" : "Ointment",
			HospitalDrugForm.Gel => ar ? "جل" : "Gel",
			HospitalDrugForm.Inhaler => ar ? "بخاخ استنشاق" : "Inhaler",
			HospitalDrugForm.Nebule => ar ? "أمبول نيبولايزر" : "Nebules",
			HospitalDrugForm.Suppository => ar ? "لبوس" : "Suppositories",
			HospitalDrugForm.Patch => ar ? "لصقة" : "Patch",
			HospitalDrugForm.Powder => ar ? "بودرة" : "Powder",
			HospitalDrugForm.Spray => ar ? "بخاخ" : "Spray",
			HospitalDrugForm.Lozenge => ar ? "أقراص استحلاب" : "Lozenges",
			_ => form,
		};

		/// The strength unit in the label's language: an Arabic label carries no Latin unit.
		public static string? Unit(string? unit, bool ar) => !ar || unit == null ? unit : unit switch
		{
			"mg" => "مجم", "mcg" => "ميكروجرام", "g" => "جم", "IU" => "وحدة دولية", "mL" => "مل",
			"mg/mL" => "مجم/مل", "mcg/mL" => "ميكروجرام/مل", "mg/5mL" => "مجم/5 مل", "%" => "٪", "mmol" => "ملي مول", "mEq" => "ملي مكافئ",
			_ => unit,
		};

		/// What the medicine is, from the drug master: the generic (the Tall Man spelling in English, so a look-alike
		/// name reads differently), the strength and the form. Null without a master record.
		public static string? Detail(HospitalDrug? m, bool ar)
		{
			if (m == null) return null;
			var generic = ar ? (string.IsNullOrWhiteSpace(m.GenericName) ? m.GenericNameEn : m.GenericName)
				: (m.TallMan ?? m.GenericNameEn ?? m.GenericName);
			var strength = m.Strength is decimal s ? s.ToString("0.###", CultureInfo.InvariantCulture) + (m.StrengthUnit == null ? "" : " " + Unit(m.StrengthUnit, ar)) : null;
			var parts = new[] { generic, strength, Form(m.Form, ar) }.Where(x => !string.IsNullOrWhiteSpace(x));
			var text = string.Join(" · ", parts);
			return text.Length == 0 ? null : text;
		}

		/// The storage and safety warnings, most important first; "keep out of the reach of children" closes every label.
		public static string Warnings(HospitalDrug? m, bool ar)
		{
			var w = new List<string>();
			if (m?.HighAlert == true) w.Add(ar ? "دواء عالي الخطورة" : "High-alert medicine");
			if (m?.ControlledSchedule != null) w.Add(ar ? "دواء خاضع للرقابة" : "Controlled medicine");
			if (m?.ColdChain == true) w.Add(ar ? "يُحفظ في الثلاجة 2–8 °م" : "Keep in a fridge, 2–8 °C");
			if (m?.Hazardous == true) w.Add(ar ? "يُتعامل معه بحذر" : "Hazardous: handle with care");
			w.Add(ar ? "يُحفظ بعيدًا عن متناول الأطفال" : "Keep out of the reach of children");
			return string.Join(" · ", w);
		}

		public static string Quantity(decimal q) => q.ToString("0.###", CultureInfo.InvariantCulture);
	}

	public sealed class HospitalRxLabelSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalRxLabelSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.RxLabel;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("DispenseId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var onlyLine = query.Parameters.GetInt("LineId");
			var d = await _db.HospitalDispenses.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (d == null || (d.Status != HospitalDispenseStatus.Dispensed && d.Status != HospitalDispenseStatus.Partial)) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == d.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);

			var lines = await _db.HospitalDispenseLines.AsNoTracking()
				.Where(l => l.CompanyId == ctx.CompanyId && l.DispenseId == d.ID && l.DispensedQty > 0 && (onlyLine == null || l.ID == onlyLine))
				.OrderBy(l => l.SortOrder).ThenBy(l => l.ID).ToListAsync(ct);
			var lineIds = lines.Select(l => l.ID).ToList();
			var returned = await _db.Set<HospitalDispenseReturn>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.DispenseId == d.ID && lineIds.Contains(r.LineId))
				.GroupBy(r => r.LineId).Select(g => new { g.Key, Qty = g.Sum(r => r.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Qty, ct);
			var writtenIds = lines.Where(l => l.PrescriptionLineId != null).Select(l => l.PrescriptionLineId!.Value).ToList();
			var written = await _db.HospitalPrescriptionLines.AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && writtenIds.Contains(x.ID))
				.Select(x => new { x.ID, x.DrugEn, x.InstructionsEn }).ToDictionaryAsync(x => x.ID, ct);
			var itemIds = lines.Where(l => l.ItemId != null).Select(l => l.ItemId!.Value).Distinct().ToList();
			var masters = await HospitalDrugMasterService.LookupAsync(_db, ctx.CompanyId, itemIds, ct);
			var pharmacist = d.DispensedByEmployeeId is int eid
				? await _db.Employee.AsNoTracking().Where(e => e.ID == eid && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct)
				: null;

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, d.PractitionerId, d.RxNo, d.DispensedAt ?? d.PrescribedAt, d.DispensedAt ?? d.PrescribedAt, ct);
			int n = 0;
			foreach (var l in lines)
			{
				var inHand = (l.DispensedQty ?? 0m) - returned.GetValueOrDefault(l.ID);
				if (inHand <= 0) continue;   // all of it came back: nothing in the patient's hands to label
				var pl = l.PrescriptionLineId is int plid ? written.GetValueOrDefault(plid) : null;
				var m = l.ItemId is int iid ? masters.GetValueOrDefault(iid) : null;
				var row = new Dictionary<string, object?>(StringComparer.Ordinal)
				{
					["LineNo"] = ++n,
					["Drug"] = HospitalDocumentRows.P(ar, l.Drug, pl?.DrugEn),
					["DrugDetail"] = HospitalRxLabelText.Detail(m, ar),
					["Instructions"] = string.IsNullOrWhiteSpace(l.Instructions) && string.IsNullOrWhiteSpace(pl?.InstructionsEn) ? null : HospitalDocumentRows.P(ar, l.Instructions, pl?.InstructionsEn),
					["Quantity"] = HospitalRxLabelText.Quantity(inHand),
					["Batch"] = l.Batches,
					["Expiry"] = l.NearestExpiry,
					["Warnings"] = HospitalRxLabelText.Warnings(m, ar),
					["DispensedOn"] = d.DispensedAt?.ToLocalTime(),
					["Pharmacist"] = pharmacist == null ? null : HospitalDocumentRows.P(ar, pharmacist.FullName, pharmacist.FullNameEn),
				};
				builder.AddRow(HospitalDocumentRows.Merge(header, row));
			}
			return builder.Build(totalRowCount: n);
		}
	}
}
