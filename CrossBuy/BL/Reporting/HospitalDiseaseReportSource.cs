using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H209 — the notifiable disease case form as ONE row: the shared header (the facility, the patient, the
	// diagnosing doctor with licence), the patient's civil id and phone (the Ministry traces contacts), the disease and its
	// group, the diagnosis as the visit recorded it and when, the time the report is due by, and the Ministry's reference
	// once it is reported. Another company's id is an empty set.
	// ============================================================================================
	public sealed class HospitalDiseaseReportSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalDiseaseReportSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.DiseaseReport;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("DiseaseReportId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var r = await _db.Set<HospitalDiseaseReport>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (r == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == r.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			var disease = await _db.Set<HospitalNotifiableDisease>().AsNoTracking().FirstOrDefaultAsync(d => d.ID == r.DiseaseId && d.CompanyId == ctx.CompanyId, ct);
			bool ar = AccountingSourceHelpers.Arabic(query);

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, r.PractitionerId, r.ReportNo, r.CreatedAt, r.DiagnosedAt, ct);
			header["LineNo"] = 1;
			header["CivilId"] = patient.NationalId;
			header["Disease"] = disease == null ? null : HospitalDocumentRows.P(ar, disease.Name, disease.NameEn);
			header["Group"] = disease?.Urgency == HospitalNotifiableUrgency.Weekly
				? (ar ? "المجموعة (ب) — خلال 7 أيام" : "Group B — within 7 days")
				: (ar ? "المجموعة (أ) — خلال 24 ساعة" : "Group A — within 24 hours");
			header["Diagnosis"] = r.Code + " " + HospitalDocumentRows.P(ar, r.DiagnosisName, r.DiagnosisNameEn);
			header["DiagnosedAt"] = r.DiagnosedAt.ToLocalTime();
			header["DueBy"] = r.DueAt.ToLocalTime();
			header["Status"] = r.Status switch
			{
				HospitalDiseaseReportStatus.Reported => (ar ? "أُبلغت الوزارة" : "Reported to the Ministry"),
				HospitalDiseaseReportStatus.Dismissed => (ar ? "ليست حالة واجبة الإبلاغ: " : "Not a case to report: ") + r.DismissReason,
				_ => ar ? "بانتظار الإبلاغ" : "To be reported",
			};
			header["MohRef"] = r.MohRef;
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
