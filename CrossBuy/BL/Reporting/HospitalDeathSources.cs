using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ---- Hospital.DeathCertificate (H59): one certificate as ONE row — the WHO form's Part I chain, Part II, manner ----
    public sealed class HospitalDeathCertificateSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalDeathCertificateSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.DeathCertificate;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("DeathId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var d = await _db.Set<HospitalDeath>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (d == null) return builder.Build(totalRowCount: 0);
            var p = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == d.PatientId && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (p == null) return builder.Build(totalRowCount: 0);
            bool ar = AccountingSourceHelpers.Arabic(query);
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, p, d.PractitionerId, d.DeathNo, d.CertifiedAt, d.DiedAt, cancellationToken);
            header["LineNo"] = 1;
            header["DiedAt"] = d.DiedAt.ToLocalTime();
            header["Place"] = d.Place switch
            {
                HospitalDeathPlace.Ward => ar ? "جناح التنويم" : "Inpatient ward", HospitalDeathPlace.Icu => ar ? "العناية المركزة" : "Intensive care",
                HospitalDeathPlace.Er => ar ? "الطوارئ" : "Emergency department", HospitalDeathPlace.Theatre => ar ? "غرفة العمليات" : "Operating theatre",
                HospitalDeathPlace.OnArrival => ar ? "متوفى عند الوصول" : "Dead on arrival", _ => ar ? "أخرى" : "Other",
            };
            header["CauseA"] = d.ImmediateCause; header["CauseB"] = d.DueTo1; header["CauseC"] = d.DueTo2; header["CauseD"] = d.UnderlyingCause;
            header["UnderlyingCause"] = HospitalDeathRules.Underlying(d);
            header["OtherConditions"] = d.OtherConditions;
            header["Manner"] = d.Manner switch
            {
                HospitalDeathManner.Natural => ar ? "طبيعية" : "Natural", HospitalDeathManner.Accident => ar ? "حادث" : "Accident", HospitalDeathManner.Suicide => ar ? "انتحار" : "Suicide",
                HospitalDeathManner.Homicide => ar ? "قتل" : "Homicide", HospitalDeathManner.Pending => ar ? "قيد التحقيق" : "Pending investigation", _ => ar ? "غير محددة" : "Could not be determined",
            };
            header["Autopsy"] = d.AutopsyRequested ? (ar ? "نعم" : "Yes") : (ar ? "لا" : "No");
            header["Void"] = d.VoidedAt == null ? null : (ar ? "ملغاة: " : "VOID: ") + d.VoidReason;
            builder.AddRow(header);
            return builder.Build(totalRowCount: 1);
        }
    }
}
