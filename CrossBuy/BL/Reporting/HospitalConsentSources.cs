using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ---- Hospital.Consent (H60): one signed consent as ONE row — the text exactly as the signer read it (frozen in the
    // row, in the language they read), who signed and why, who explained, the witness, and the SHA-256 of the
    // electronic signature (the picture itself stays on the document platform; Report Studio prints no images). ----
    public sealed class HospitalConsentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalConsentSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.Consent;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("ConsentId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var c = await _db.Set<HospitalConsent>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId && x.SignatureDocumentId != null, cancellationToken);
            if (c == null) return builder.Build(totalRowCount: 0);
            var p = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == c.PatientId && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (p == null) return builder.Build(totalRowCount: 0);
            bool ar = AccountingSourceHelpers.Arabic(query);
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, p, c.PractitionerId, c.ConsentNo, c.SignedAt, c.SignedAt, cancellationToken);
            var ids = new[] { c.ExplainedByEmployeeId, c.WitnessEmployeeId };
            var people = await _db.Employee.AsNoTracking().Where(e => ids.Contains(e.ID) && e.EmpCompanyID == ctx.CompanyId)
                .Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToDictionaryAsync(e => e.ID, cancellationToken);
            string? Who(int eid) => people.TryGetValue(eid, out var e) ? (ar ? e.FullName : e.FullNameEn ?? e.FullName) : null;
            header["LineNo"] = 1;
            header["Title"] = c.Title;
            header["ConsentText"] = c.Text;
            header["ProcedureName"] = c.ProcedureName;
            header["Risks"] = c.Risks;
            header["Decision"] = c.Decision == HospitalConsentDecision.Given ? (ar ? "موافق" : "Consent given") : (ar ? "رافض" : "Consent refused");
            header["SignerName"] = c.SignerName;
            header["SignerRelation"] = c.SignerRelation;
            header["ProxyReason"] = c.ProxyReason switch
            {
                HospitalConsentProxyReason.Minor => ar ? "المريض قاصر" : "The patient is a minor",
                HospitalConsentProxyReason.Unconscious => ar ? "المريض فاقد الوعي" : "The patient is unconscious",
                HospitalConsentProxyReason.NoCapacity => ar ? "المريض غير قادر على اتخاذ القرار" : "The patient lacks capacity to decide",
                HospitalConsentProxyReason.Other => ar ? "سبب آخر" : "Another reason",
                _ => null,
            };
            header["Interpreter"] = c.Interpreter;
            header["ExplainedBy"] = header["DoctorName"] ?? Who(c.ExplainedByEmployeeId);
            header["Witness"] = Who(c.WitnessEmployeeId);
            header["SignedAt"] = c.SignedAt.ToLocalTime();
            header["CaseNo"] = c.SurgeryId is int sid
                ? await _db.Set<HospitalSurgery>().AsNoTracking().Where(s => s.ID == sid && s.CompanyId == ctx.CompanyId).Select(s => s.CaseNo).FirstOrDefaultAsync(cancellationToken)
                : null;
            header["SignatureFingerprint"] = c.SignatureSha256;
            header["Withdrawn"] = c.WithdrawnAt == null ? null : (ar ? "سُحبت: " : "WITHDRAWN: ") + c.WithdrawReason;
            builder.AddRow(header);
            return builder.Build(totalRowCount: 1);
        }
    }
}
