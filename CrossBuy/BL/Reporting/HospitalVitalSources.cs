using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ---- Hospital.BirthNotice (H58): one birth as ONE row — the mother's header, the baby's facts --------------------
    public sealed class HospitalBirthNoticeSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalBirthNoticeSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.BirthNotice;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("BirthId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var b = await _db.Set<HospitalBirth>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (b == null) return builder.Build(totalRowCount: 0);
            var mother = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == b.MotherPatientId && p.CompanyId == ctx.CompanyId, cancellationToken);
            if (mother == null) return builder.Build(totalRowCount: 0);
            var baby = b.NewbornPatientId is int nid ? await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == nid && p.CompanyId == ctx.CompanyId, cancellationToken) : null;
            bool ar = AccountingSourceHelpers.Arabic(query);
            string P(string? a, string? e) => HospitalDocumentRows.P(ar, a, e);
            // the header's "patient" is the mother; the document is the birth
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, mother, b.AttendantPractitionerId, b.BirthNo, b.RegisteredAt, b.BornAt, cancellationToken);
            header["LineNo"] = 1;
            header["BornAt"] = b.BornAt.ToLocalTime();
            header["BabySex"] = HospitalDocumentRows.Sex(b.Sex, ar);
            header["Outcome"] = b.Outcome == HospitalBirthOutcome.Live ? (ar ? "مولود حي" : "Live birth") : (ar ? "مولود ميت" : "Stillbirth");
            header["DeliveryMode"] = b.DeliveryMode switch { HospitalDeliveryMode.Normal => ar ? "ولادة طبيعية" : "Normal delivery", HospitalDeliveryMode.Assisted => ar ? "ولادة مساعدة" : "Assisted delivery", _ => ar ? "ولادة قيصرية" : "Caesarean section" };
            header["GestationWeeks"] = b.GestationWeeks;
            header["BirthOrder"] = b.BirthOrder;
            header["WeightGrams"] = b.WeightGrams;
            header["LengthCm"] = b.LengthCm;
            header["HeadCm"] = b.HeadCm;
            header["Apgar"] = b.Apgar1 == null && b.Apgar5 == null ? null : (b.Apgar1?.ToString() ?? "—") + " / " + (b.Apgar5?.ToString() ?? "—");
            header["BabyName"] = baby == null ? null : P(baby.Name, baby.NameEn);
            header["BabyMrn"] = baby?.Mrn;
            header["Note"] = b.Note;
            header["Void"] = b.VoidedAt == null ? null : (ar ? "ملغى: " : "VOID: ") + b.VoidReason;
            builder.AddRow(header);
            return builder.Build(totalRowCount: 1);
        }
    }
}
