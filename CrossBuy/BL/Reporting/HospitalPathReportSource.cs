using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H216 — the pathology report as ONE row: the shared header (the facility, the patient, the requesting
	// doctor), the case, the specimen and its site, the clinical history, the gross and microscopic descriptions, the
	// diagnosis (and whether malignant), the pathologist who signed it and when, and every amendment with its reason.
	// Only a signed report prints; another company's id is an empty set.
	// ============================================================================================
	public sealed class HospitalPathReportSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalPathReportSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.PathReport;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("PathCaseId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var c = await _db.Set<HospitalPathCase>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId && x.Status == HospitalPathStatus.Reported, ct);
			if (c == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == c.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var requester = c.RequestedByEmployeeId is int rq ? await _db.HospitalPractitioners.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.EmployeeId == rq).Select(p => (int?)p.ID).FirstOrDefaultAsync(ct) : null;
			var signer = c.ReportedByEmployeeId is int sg ? await _db.Employee.AsNoTracking().Where(e => e.ID == sg && e.EmpCompanyID == ctx.CompanyId).Select(e => new { e.FullName, e.FullNameEn }).FirstOrDefaultAsync(ct) : null;
			var amends = await _db.Set<HospitalPathAmendment>().AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.CaseId == c.ID).OrderBy(a => a.At).ToListAsync(ct);

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, requester, c.CaseNo, c.ReportedAt ?? c.RequestedAt, c.RequestedAt, ct);
			header["LineNo"] = 1;
			header["Specimen"] = c.Specimen + (string.IsNullOrWhiteSpace(c.Site) ? "" : " — " + c.Site);
			header["Source"] = c.Source switch
			{
				HospitalPathSource.Theatre => ar ? "العمليات" : "Theatre",
				HospitalPathSource.Endoscopy => ar ? "المناظير" : "Endoscopy",
				HospitalPathSource.Ward => ar ? "القسم الداخلي" : "Ward",
				_ => ar ? "العيادة" : "Clinic",
			};
			header["Received"] = c.ReceivedAt?.ToLocalTime();
			header["ClinicalHistory"] = c.ClinicalHistory;
			header["Gross"] = c.Gross + (c.Blocks is int b ? (ar ? $" ({b} قوالب)" : $" ({b} blocks)") : "");
			header["Microscopic"] = c.Microscopic;
			header["Diagnosis"] = c.Diagnosis;
			header["Malignant"] = c.Malignant ? (ar ? "ورم خبيث" : "MALIGNANT") : null;
			header["Pathologist"] = signer == null ? null : HospitalDocumentRows.P(ar, signer.FullName, signer.FullNameEn);
			header["SignedAt"] = c.ReportedAt?.ToLocalTime();
			header["Amendments"] = amends.Count == 0 ? null : string.Join("\n", amends.Select(a => a.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + " — " + a.Text + " (" + a.Reason + ")"));
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
