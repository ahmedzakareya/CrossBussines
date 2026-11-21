using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H222 — the pathology case's labels, one ROW per label: a cassette label for every block cut at grossing
	// (PATH-000001-A1 …) and a slide label for each (PATH-000001-A1-S1 …), each with the patient, the case and a QR of
	// its own code, so a block or a slide is scanned rather than read. Another company's id is an empty set.
	// ============================================================================================
	public sealed class HospitalPathLabelSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalPathLabelSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.PathLabels;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("PathCaseId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var c = await _db.Set<HospitalPathCase>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId && x.Blocks > 0, ct);
			if (c == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, null, c.CaseNo, c.GrossedAt ?? c.RequestedAt, c.RequestedAt, ct);
			int n = 0;
			foreach (var (code, kind) in CrossBuy.BL.Hospital.HospitalPathRules.Labels(c.CaseNo, c.Blocks ?? 0))
			{
				var row = new Dictionary<string, object?>(header, StringComparer.Ordinal)
				{
					["LineNo"] = ++n,
					["LabelCode"] = code,
					["CaseNo"] = c.CaseNo,
					["LabelKind"] = kind == "Block" ? (ar ? "قالب" : "Block") : (ar ? "شريحة" : "Slide"),
					["Specimen"] = c.Specimen,
				};
				builder.AddRow(row);
			}
			return builder.Build(totalRowCount: n);
		}
	}
}
