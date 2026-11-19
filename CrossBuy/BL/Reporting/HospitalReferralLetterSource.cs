using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
	// ============================================================================================
	// HOSPITAL H204 — the referral letter as ONE row: the shared header (the facility, the patient, the referring doctor
	// with specialty and licence), then to whom it goes — the department (and doctor) of this hospital, or the outside
	// facility and specialty — the urgency and the time the answer is due, the question and the clinical summary as the
	// referral recorded them, the visit's diagnoses (primary first), the patient's allergies, and the reply once it came.
	// Another company's id is an empty set.
	// ============================================================================================
	public sealed class HospitalReferralLetterSource : IReportDataSource
	{
		private readonly CrossDbContext _db;
		public HospitalReferralLetterSource(CrossDbContext db) { _db = db; }
		public string Key => HospitalDocumentDatasetCodes.ReferralLetter;

		public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
		{
			ArgumentNullException.ThrowIfNull(query);
			var ctx = query.Context; var ct = cancellationToken;
			var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
			if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
			var id = query.Parameters.GetInt("ReferralId");
			if (id is not > 0) return builder.Build(totalRowCount: 0);
			var r = await _db.Set<HospitalReferral>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, ct);
			if (r == null) return builder.Build(totalRowCount: 0);
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == r.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return builder.Build(totalRowCount: 0);
			bool ar = AccountingSourceHelpers.Arabic(query);

			string? toWhom, toSpecialty;
			if (r.Kind == HospitalReferralKind.Internal)
			{
				var dept = await _db.HospitalDepartments.AsNoTracking().Where(d => d.ID == r.ToDepartmentId && d.CompanyId == ctx.CompanyId).Select(d => new { d.Name, d.NameEn }).FirstOrDefaultAsync(ct);
				var doc = r.ToPractitionerId is int tp
					? await _db.HospitalPractitioners.AsNoTracking().Where(p => p.ID == tp && p.CompanyId == ctx.CompanyId).Select(p => new { p.Name, p.NameEn }).FirstOrDefaultAsync(ct)
					: null;
				var d = dept == null ? null : HospitalDocumentRows.P(ar, dept.Name, dept.NameEn);
				toWhom = doc == null ? d : d + " — " + HospitalDocumentRows.P(ar, doc.Name, doc.NameEn);
				toSpecialty = d;
			}
			else { toWhom = r.ToFacility; toSpecialty = r.ToSpecialty; }

			string? diagnoses = null;
			if (r.EncounterId is int enc)
			{
				var dx = await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.EncounterId == enc)
					.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder).ToListAsync(ct);
				if (dx.Count > 0) diagnoses = string.Join(" · ", dx.Select(x => x.Code + " " + HospitalDocumentRows.P(ar, x.Name, x.NameEn)));
			}

			string urgency = r.Urgency switch
			{
				HospitalReferralUrgency.Emergency => ar ? "طارئ" : "Emergency",
				HospitalReferralUrgency.Urgent => ar ? "عاجل" : "Urgent",
				_ => ar ? "روتيني" : "Routine",
			};

			var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, r.FromPractitionerId, r.ReferralNo, r.CreatedAt, r.CreatedAt, ct);
			header["LineNo"] = 1;
			header["ToWhom"] = toWhom;
			header["ToSpecialty"] = toSpecialty;
			header["Urgency"] = urgency;
			header["DueBy"] = r.DueAt.ToLocalTime();
			header["Reason"] = r.Reason;
			header["Summary"] = r.Summary;
			header["Diagnoses"] = diagnoses;
			header["Allergies"] = string.IsNullOrWhiteSpace(patient.Allergies) ? (ar ? "لا توجد حساسية مسجلة" : "No allergy recorded") : patient.Allergies;   // stated either way
			header["Reply"] = r.Reply;
			builder.AddRow(header);
			return builder.Build(totalRowCount: 1);
		}
	}
}
