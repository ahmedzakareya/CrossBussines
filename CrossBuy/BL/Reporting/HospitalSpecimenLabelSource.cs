using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H206 — a specimen tube's label as ONE row: the accession number (DocumentNo — the label prints it as text and
	// as the QR the bedside scan reads), the patient, the test, its specimen and section, the priority, when it was
	// ordered (DocumentDate) and collected. Another company's id is an empty set.
	// ============================================================================================
	public sealed class HospitalSpecimenLabelSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalSpecimenLabelSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.SpecimenLabel;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("LabOrderId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var o = await _db.HospitalLabOrders.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (o == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == o.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);
			var setup = await _db.Set<HospitalLabTest>().AsNoTracking().FirstOrDefaultAsync(t => t.ServiceId == o.ServiceId && t.CompanyId == ctx.CompanyId, ct);

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, null, o.AccessionNo, o.OrderedAt, o.OrderedAt, ct);
			header["LineNo"] = 1;
			header["Test"] = HospitalDocumentRows.P(ar, o.Name, o.NameEn);
			header["Specimen"] = setup == null ? null : HospitalDocumentRows.P(ar, setup.Specimen, setup.SpecimenEn);
			header["Section"] = setup == null ? null : HospitalDocumentRows.P(ar, setup.Section, setup.SectionEn);
			header["Priority"] = o.Priority switch { HospitalLabPriority.Stat => "STAT", HospitalLabPriority.Urgent => ar ? "عاجل" : "Urgent", _ => ar ? "روتيني" : "Routine" };
			header["CollectedAt"] = o.CollectedAt?.ToLocalTime();
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
