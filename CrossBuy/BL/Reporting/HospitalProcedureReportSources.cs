using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL — the printed reports of two diagnostic units, each ONE row:
	//   · the ENDOSCOPY report (H217): the procedure, the scope and the cycle that cleared it, sedation, indication,
	//     findings, interventions, complications, recommendations, the colonoscopy quality figures, the biopsies and
	//     the pathology case they opened; the endoscopist from the shared header.
	//   · the CARDIAC STUDY report (H218): ECG intervals with the calculated QTc, or the echo's ejection fraction,
	//     chambers, valves, wall motion and pericardium; the interpretation; a critical finding stated; the reader.
	// Only a completed / reported study prints; another company's id is an empty set.
	// ============================================================================================
	internal static class HospitalProcedureRows
	{
		/// The practitioner record of an employee (the header's doctor block reads practitioners).
		public static async Task<int?> PractitionerOfAsync(CrossDbContext db, int companyId, int? employeeId, CancellationToken ct) =>
			employeeId is int e ? await db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == companyId && p.EmployeeId == e).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct) : null;
	}

	public sealed class HospitalEndoReportSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalEndoReportSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.EndoReport;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("EndoscopyId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var p = await _db.Set<HospitalEndoscopy>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId && x.Status == HospitalEndoStatus.Completed, ct);
			if (p == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == p.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var scope = await _db.Set<HospitalScope>().AsNoTracking().Where(s => s.ID == p.ScopeId && s.CompanyId == ctx.CompanyId).Select(s => new { s.Serial, s.Model }).FirstOrDefaultAsync(ct);
			var cycle = await _db.Set<HospitalScopeCycle>().AsNoTracking().Where(c => c.ID == p.CycleId && c.CompanyId == ctx.CompanyId).Select(c => new { c.At, c.Agent }).FirstOrDefaultAsync(ct);
			var caseNo = p.PathCaseId is int pc ? await _db.Set<HospitalPathCase>().AsNoTracking().Where(c => c.ID == pc && c.CompanyId == ctx.CompanyId).Select(c => c.CaseNo).FirstOrDefaultAsync(ct) : null;
			var doc = await HospitalProcedureRows.PractitionerOfAsync(_db, ctx.CompanyId, p.EndoscopistEmployeeId, ct);

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, doc, p.ProcNo, p.CompletedAt ?? p.StartedAt, p.StartedAt, ct);
			header["LineNo"] = 1;
			header["Procedure"] = p.Kind switch
			{
				HospitalEndoKind.Gastroscopy => ar ? "منظار المعدة" : "Gastroscopy",
				HospitalEndoKind.Colonoscopy => ar ? "منظار القولون" : "Colonoscopy",
				HospitalEndoKind.Bronchoscopy => ar ? "منظار القصبات" : "Bronchoscopy",
				_ => ar ? "تصوير القنوات الصفراوية بالمنظار" : "ERCP",
			};
			header["Sedation"] = p.Sedation switch
			{
				HospitalEndoSedation.Deep => ar ? "تهدئة عميقة" : "Deep sedation",
				HospitalEndoSedation.Moderate => ar ? "تهدئة متوسطة" : "Moderate sedation",
				_ => ar ? "بلا تهدئة" : "No sedation",
			};
			header["Scope"] = scope == null ? null : scope.Serial + (string.IsNullOrWhiteSpace(scope.Model) ? "" : " · " + scope.Model)
				+ (cycle == null ? "" : (ar ? " — عُقّم " : " — reprocessed ") + cycle.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " · " + cycle.Agent);
			header["Indication"] = p.Indication;
			header["Findings"] = p.Findings;
			header["Interventions"] = p.Interventions;
			header["Complications"] = string.IsNullOrWhiteSpace(p.Complications) ? (ar ? "لا مضاعفات" : "None") : p.Complications;
			header["Recommendations"] = p.Recommendations;
			header["Quality"] = p.Kind != HospitalEndoKind.Colonoscopy ? null
				: (ar ? (p.CecumReached == true ? "بلغ الأعور" : "لم يبلغ الأعور") : (p.CecumReached == true ? "Caecum reached" : "Caecum not reached"))
				  + (p.WithdrawalMinutes is int w ? (ar ? $" · زمن السحب {w} دقيقة" : $" · withdrawal {w} min") : "")
				  + (p.BowelPrepScore is int b ? (ar ? $" · بوسطن {b}" : $" · Boston {b}") : "");
			header["Biopsies"] = p.Biopsies == 0 ? (ar ? "لم تؤخذ خزعات" : "No biopsy taken")
				: (ar ? $"{p.Biopsies} أوعية خزعات" : $"{p.Biopsies} biopsy jars") + (caseNo == null ? "" : (ar ? " — حالة الأنسجة " : " — pathology case ") + caseNo);
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}

	public sealed class HospitalCardioReportSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalCardioReportSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.CardioReport;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("CardioStudyId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var s = await _db.Set<HospitalCardioStudy>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId && x.Status == HospitalCardioStatus.Reported, ct);
			if (s == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == s.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var doc = await HospitalProcedureRows.PractitionerOfAsync(_db, ctx.CompanyId, s.ReportedByEmployeeId, ct);
			bool ecg = s.Kind == HospitalCardioKind.Ecg;

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, doc, s.StudyNo, s.ReportedAt ?? s.RequestedAt, s.RequestedAt, ct);
			header["LineNo"] = 1;
			header["Study"] = ecg ? (ar ? "تخطيط القلب الكهربائي (12 قطبًا)" : "12-lead electrocardiogram") : (ar ? "صدى القلب عبر الصدر" : "Transthoracic echocardiogram");
			header["Indication"] = s.Indication;
			header["Measurements"] = ecg
				? (ar ? $"النبض {s.HeartRate} · النظم {s.Rhythm}" : $"Rate {s.HeartRate} · rhythm {s.Rhythm}") + $"\nPR {s.PrMs} ms · QRS {s.QrsMs} ms · QT {s.QtMs} ms · QTc {s.QtcMs} ms (Bazett) · " + (ar ? "المحور " : "axis ") + s.Axis + "°"
				: (ar ? $"الكسر القذفي {s.EfPercent}%" : $"Ejection fraction {s.EfPercent}%")
				  + (string.IsNullOrWhiteSpace(s.Chambers) ? "" : "\n" + (ar ? "حجرات القلب: " : "Chambers: ") + s.Chambers)
				  + (string.IsNullOrWhiteSpace(s.Valves) ? "" : "\n" + (ar ? "الصمامات: " : "Valves: ") + s.Valves)
				  + (string.IsNullOrWhiteSpace(s.WallMotion) ? "" : "\n" + (ar ? "حركة الجدار: " : "Wall motion: ") + s.WallMotion)
				  + (string.IsNullOrWhiteSpace(s.Pericardium) ? "" : "\n" + (ar ? "التامور: " : "Pericardium: ") + s.Pericardium);
			header["Interpretation"] = s.Interpretation;
			header["Critical"] = s.Critical ? (ar ? "نتيجة حرجة: " : "CRITICAL: ") + s.CriticalWhy?.Replace(";", " · ") : null;
			header["SignedAt"] = s.ReportedAt?.ToLocalTime();
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
