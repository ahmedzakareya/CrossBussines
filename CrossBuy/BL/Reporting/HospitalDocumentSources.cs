using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // THE HOSPITAL'S DOCUMENTS as rows a template lays out (see HospitalDocumentDatasets).
    //
    // TENANCY IS CHECKED ON THE HEADER: the id arrives from a URL, so the bill, visit or plan is loaded with
    // CompanyId = the resolved context and a miss is an EMPTY set. Every other read is reached through
    // that header and filtered by company as well.
    //
    // THE BILL IS THE BILLING SERVICE'S ANSWER, not a second sum: the shares, the VAT on the patient's part,
    // the contract discount and the running accommodation are HospitalBillingService.BillAsync's, so the
    // printed bill is the bill on the screen.
    //
    // EACH TEXT IN THE PRINT'S LANGUAGE, the other one when that is empty (the same fall-back as the screens).
    // ============================================================================================
    internal static class HospitalDocumentRows
    {
        public static string P(bool arabic, string? ar, string? en) =>
            arabic ? (string.IsNullOrWhiteSpace(ar) ? en ?? "" : ar) : (string.IsNullOrWhiteSpace(en) ? ar ?? "" : en);

        public static string? Age(DateTime? birth, bool arabic)
        {
            if (birth is not { } b) return null;
            var today = DateTime.Today;
            int years = today.Year - b.Year - (today < b.AddYears(today.Year - b.Year) ? 1 : 0);
            return years.ToString() + (arabic ? " سنة" : " y");
        }

        public static string Sex(string sex, bool arabic) =>
            sex == HospitalPatientSex.Female ? (arabic ? "أنثى" : "Female") : (arabic ? "ذكر" : "Male");

        public static async Task<Dictionary<string, object?>> HeaderAsync(CrossDbContext db, BusinessContext ctx, bool arabic,
            HospitalPatient patient, int? practitionerId, string documentNo, DateTime documentDateUtc, DateTime atUtc, CancellationToken ct)
        {
            var org = await TradeDocumentOrg.LoadAsync(db, ctx, arabic, ct);
            var doctor = practitionerId is int pid
                ? await db.Set<HospitalPractitioner>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == pid && p.CompanyId == ctx.CompanyId, ct)
                : null;
            // The patient's cover on the day of the document: the policy valid then, and its payer.
            var policy = await db.Set<HospitalPatientPolicy>().AsNoTracking()
                .Where(p => p.CompanyId == ctx.CompanyId && p.PatientId == patient.ID && p.IsActive && p.ValidFrom <= atUtc && (p.ValidTo == null || p.ValidTo >= atUtc))
                .OrderByDescending(p => p.ValidFrom).FirstOrDefaultAsync(ct);
            var payer = policy == null ? null
                : await db.Set<HospitalPayer>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == policy.PayerId && p.CompanyId == ctx.CompanyId, ct);
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["CompanyName"] = org.CompanyName,
                ["CompanyTaxNo"] = org.CompanyTaxNo,
                ["CompanyAddress"] = org.CompanyAddress,
                ["CompanyPhone"] = org.CompanyPhone,
                ["BranchName"] = org.BranchName,
                ["DocumentNo"] = documentNo,
                ["DocumentDate"] = documentDateUtc.ToLocalTime(),
                ["PatientName"] = P(arabic, patient.Name, patient.NameEn),
                ["PatientMrn"] = patient.Mrn,
                ["PatientAge"] = Age(patient.BirthDate, arabic),
                ["PatientSex"] = Sex(patient.Sex, arabic),
                ["PatientPhone"] = patient.Phone,
                ["DoctorName"] = doctor == null ? null : P(arabic, doctor.Name, doctor.NameEn),
                ["DoctorTitle"] = doctor == null ? null : P(arabic, doctor.Title, doctor.TitleEn),
                ["DoctorLicense"] = doctor?.LicenseNumber,
                ["Payer"] = payer == null ? null : P(arabic, payer.Name, payer.NameEn),
                ["PolicyNo"] = policy?.PolicyNo,
            };
        }

        public static Dictionary<string, object?> Merge(Dictionary<string, object?> header, Dictionary<string, object?> row)
        {
            foreach (var kv in header) row[kv.Key] = kv.Value;
            return row;
        }
    }

    // ---- Hospital.BillDocument ----------------------------------------------------------------------
    public sealed class HospitalBillDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IServiceProvider _services;
        public HospitalBillDocumentSource(CrossDbContext db, IServiceProvider services) { _db = db; _services = services; }

        // The billing service is RESOLVED ON USE, not injected. HospitalBillingService is registered by the
        // application (Program.cs), never by AddCrossBusinessReporting, so taking it as a constructor parameter
        // made the reporting pack unbuildable on its own: ValidateOnBuild failed and took 15 DI tests with it.
        // Resolving through the scope's own provider returns the SAME scoped instance the constructor would have
        // received, so nothing changes at runtime. This is the idiom HospitalDischargeSummarySource already uses.
        private HospitalBillingService Billing =>
            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<HospitalBillingService>(_services);

        public string Key => HospitalDocumentDatasetCodes.Bill;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("BillId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var v = await Billing.BillAsync(ctx, id.Value, DateTime.UtcNow, cancellationToken);
            if (v == null) return builder.Build(totalRowCount: 0);
            bool arabic = AccountingSourceHelpers.Arabic(query);

            int? doctorId = v.Bill.EncounterId is int eid
                ? await _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.ID == eid && e.CompanyId == ctx.CompanyId).Select(e => (int?)e.PractitionerId).FirstOrDefaultAsync(cancellationToken)
                : null;
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, arabic, v.Patient, doctorId, v.Bill.BillNo, v.Bill.OpenedAt, v.Bill.OpenedAt, cancellationToken);
            // The payer that split THIS bill (frozen at closing, or live while open) beats the patient's policy today.
            if (v.Cover != null) header["Payer"] = HospitalDocumentRows.P(arabic, v.Cover.Payer, v.Cover.PayerEn);

            var t = v.Totals;
            var last = v.Payments.LastOrDefault();
            header["Status"] = AccountingSourceHelpers.StatusLabel(v.Bill.Status, arabic);
            header["Currency"] = v.Currency?.Label(arabic);
            header["TotalGross"] = t.Gross;
            header["Discount"] = v.Cover?.Discount ?? 0m;
            header["TotalInsurer"] = t.Insurer;
            header["TotalPatient"] = t.Patient;
            header["TotalVat"] = t.Vat;
            header["NetDue"] = t.Patient + t.Vat;
            header["Paid"] = t.Paid;
            header["Waived"] = t.Waived;   // H63
            header["Remaining"] = t.Due;
            header["PaymentMethod"] = last?.Method;
            header["ReceiptNo"] = last?.ReceiptNo;

            int cap = query.MaxRows > 0 ? query.MaxRows : HospitalDocumentDatasets.MaxRows;
            var lines = v.Lines.Take(cap + 1).ToList();
            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();
            int n = 0;
            foreach (var l in lines)
                builder.AddRow(HospitalDocumentRows.Merge(header, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["LineNo"] = ++n,
                    ["ServiceCode"] = l.Code,
                    ["Description"] = HospitalDocumentRows.P(arabic, l.Description, l.DescriptionEn),
                    ["Category"] = l.Category,
                    ["Qty"] = l.Quantity,
                    ["UnitPrice"] = l.UnitPrice,
                    ["Gross"] = l.Gross,
                    ["InsurerShare"] = l.Insurer,
                    ["PatientShare"] = l.Patient,
                    ["Vat"] = l.Vat,
                    ["Notes"] = l.Running ? (arabic ? "إقامة جارية" : "Stay in progress") : null,
                }));
            return builder.Build(truncated, truncated ? null : lines.Count, Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    // ---- Hospital.PrescriptionDocument --------------------------------------------------------------
    public sealed class HospitalPrescriptionDocumentSource : HospitalPrescriptionSourceBase
    {
        public HospitalPrescriptionDocumentSource(CrossDbContext db) : base(db) { }
        public override string Key => HospitalDocumentDatasetCodes.Prescription;
    }

    // ---- Hospital.PrescriptionForm: the same visit as ONE row, the medicines joined into one field -------------
    public sealed class HospitalPrescriptionFormSource : HospitalPrescriptionSourceBase
    {
        public HospitalPrescriptionFormSource(CrossDbContext db) : base(db) { }
        public override string Key => HospitalDocumentDatasetCodes.PrescriptionForm;
        protected override bool OneRow => true;
    }

    public abstract class HospitalPrescriptionSourceBase : IReportDataSource
    {
        private readonly CrossDbContext _db;
        protected HospitalPrescriptionSourceBase(CrossDbContext db) { _db = db; }

        public abstract string Key { get; }
        protected virtual bool OneRow => false;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("EncounterId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var enc = await _db.Set<HospitalEncounter>().AsNoTracking().FirstOrDefaultAsync(e => e.ID == id.Value && e.CompanyId == ctx.CompanyId, cancellationToken);
            if (enc == null) return builder.Build(totalRowCount: 0);
            var patient = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == enc.PatientId && p.CompanyId == ctx.CompanyId, cancellationToken);
            if (patient == null) return builder.Build(totalRowCount: 0);
            bool arabic = AccountingSourceHelpers.Arabic(query);

            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, arabic, patient, enc.PractitionerId,
                "V-" + enc.ID.ToString("D6"), enc.SignedAt ?? enc.StartedAt, enc.StartedAt, cancellationToken);
            var dx = await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking()
                .Where(d => d.CompanyId == ctx.CompanyId && d.EncounterId == enc.ID)
                .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.SortOrder).ToListAsync(cancellationToken);
            header["ChiefComplaint"] = enc.ChiefComplaint;
            header["MedicalCondition"] = patient.ChronicConditions;
            header["Allergies"] = patient.Allergies;
            header["Diagnosis"] = string.Join(" · ", dx.Select(d => d.Code + " " + HospitalDocumentRows.P(arabic, d.Name, d.NameEn)));
            header["TreatmentPlan"] = enc.PlanNote;
            header["FollowUp"] = enc.FollowUpDays is int days ? (arabic ? $"بعد {days} يوم" : $"In {days} days") : null;

            int cap = query.MaxRows > 0 ? query.MaxRows : HospitalDocumentDatasets.MaxRows;
            var lines = await _db.Set<HospitalPrescriptionLine>().AsNoTracking()
                .Where(l => l.CompanyId == ctx.CompanyId && l.EncounterId == enc.ID)
                .OrderBy(l => l.SortOrder).ThenBy(l => l.ID).Take(cap + 1).ToListAsync(cancellationToken);
            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();
            int n = 0;
            if (OneRow)
            {
                header["LineNo"] = 1;
                // one medicine per line: the paper's dotted lines
                header["Diagnosis"] = string.Join("\n", dx.Select(d => d.Code + " " + HospitalDocumentRows.P(arabic, d.Name, d.NameEn)));
                header["Medicines"] = string.Join("\n", lines.Select(l =>
                {
                    var how = HospitalDocumentRows.P(arabic, l.Instructions, l.InstructionsEn);
                    return (++n) + ") " + HospitalDocumentRows.P(arabic, l.Drug, l.DrugEn) + (string.IsNullOrWhiteSpace(how) ? "" : " — " + how) + " ×" + l.Quantity;
                }));
                builder.AddRow(header);
                return builder.Build(false, 1, Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
            }
            foreach (var l in lines)
                builder.AddRow(HospitalDocumentRows.Merge(header, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["LineNo"] = ++n,
                    ["Drug"] = HospitalDocumentRows.P(arabic, l.Drug, l.DrugEn),
                    ["Instructions"] = HospitalDocumentRows.P(arabic, l.Instructions, l.InstructionsEn),
                    ["Quantity"] = l.Quantity,
                }));
            return builder.Build(truncated, truncated ? null : lines.Count, Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    // ---- Hospital.DentalClaimDocument ---------------------------------------------------------------
    public sealed class HospitalDentalClaimDocumentSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly HospitalPricingService? _pricing;
        public HospitalDentalClaimDocumentSource(CrossDbContext db, HospitalPricingService? pricing = null) { _db = db; _pricing = pricing; }

        public string Key => HospitalDocumentDatasetCodes.DentalClaim;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("PlanId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);

            var ct = cancellationToken;
            var plan = await _db.Set<HospitalDentalPlan>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == id.Value && p.CompanyId == ctx.CompanyId, ct);
            if (plan == null) return builder.Build(totalRowCount: 0);
            var patient = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == plan.PatientId && p.CompanyId == ctx.CompanyId, ct);
            if (patient == null) return builder.Build(totalRowCount: 0);
            bool arabic = AccountingSourceHelpers.Arabic(query);

            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, arabic, patient, plan.PractitionerId, plan.PlanNo, plan.CreatedAt, plan.CreatedAt, ct);

            // The diagnosis is the chart's open conditions, tooth by tooth, in the dentist's words.
            var conditions = await _db.Set<HospitalDentalFinding>().AsNoTracking()
                .Where(f => f.CompanyId == ctx.CompanyId && f.PatientId == patient.ID && f.VoidedAt == null && f.Status == HospitalDentalMark.Condition)
                .OrderBy(f => f.Tooth).ToListAsync(ct);
            header["Diagnosis"] = string.Join("\n", conditions.Select(f =>
            {
                var text = HospitalDocumentRows.P(arabic, f.Note, f.NoteEn);
                if (string.IsNullOrWhiteSpace(text)) text = f.Work ?? f.Surfaces ?? "";
                return "#" + f.Tooth + " " + text;
            }));

            // The medicines are the latest visit's prescription — with the plan's dentist when there is one.
            var visits = _db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.CompanyId == ctx.CompanyId && e.PatientId == patient.ID);
            if (plan.PractitionerId is int pid) visits = visits.Where(e => e.PractitionerId == pid);
            var visitId = await visits.Where(e => _db.Set<HospitalPrescriptionLine>().Any(l => l.EncounterId == e.ID && l.CompanyId == ctx.CompanyId))
                .OrderByDescending(e => e.StartedAt).Select(e => (int?)e.ID).FirstOrDefaultAsync(ct);
            if (visitId is int vid)
            {
                var rx = await _db.Set<HospitalPrescriptionLine>().AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.EncounterId == vid)
                    .OrderBy(l => l.SortOrder).ToListAsync(ct);
                header["Medications"] = string.Join("\n", rx.Select(l =>
                {
                    var how = HospitalDocumentRows.P(arabic, l.Instructions, l.InstructionsEn);
                    return HospitalDocumentRows.P(arabic, l.Drug, l.DrugEn) + (string.IsNullOrWhiteSpace(how) ? "" : " (" + how + ")");
                }));
            }
            else header["Medications"] = null;

            // A panoramic film on the patient's imaging orders (OPG / panoramic), whatever its status.
            bool panoramic = await _db.Set<HospitalRadOrder>().AsNoTracking().AnyAsync(o => o.CompanyId == ctx.CompanyId && o.PatientId == patient.ID
                && (o.Code.Contains("OPG") || o.NameEn.Contains("anoram") || o.Name.Contains("بانورام")), ct);
            header["Panoramic"] = panoramic ? (arabic ? "نعم" : "Yes") : (arabic ? "لا" : "No");
            header["PanoramicMark"] = panoramic ? "X" : null;   // the form's tick box
            header["Currency"] = _pricing == null ? null : (await _pricing.CurrencyOfAsync(ctx, plan.CurrencyId, ct)).Label(arabic);

            var lines = await (from l in _db.Set<HospitalDentalPlanLine>().AsNoTracking()
                               join ph in _db.Set<HospitalDentalPlanPhase>().AsNoTracking() on l.PhaseId equals ph.ID
                               join s in _db.Set<HospitalService>().AsNoTracking() on l.ServiceId equals s.ID
                               where l.CompanyId == ctx.CompanyId && l.PlanId == plan.ID && ph.CompanyId == ctx.CompanyId && s.CompanyId == ctx.CompanyId
                                     && l.Status != HospitalDentalLineStatus.Cancelled && ph.Decision != HospitalDentalDecision.Declined
                               orderby ph.Seq, l.ID
                               select new { l, ph.Title, ph.TitleEn, s.Code, s.Name, s.NameEn }).ToListAsync(ct);
            header["PlanTotal"] = lines.Sum(x => x.l.Fee);

            int cap = query.MaxRows > 0 ? query.MaxRows : HospitalDocumentDatasets.MaxRows;
            bool truncated = lines.Count > cap;
            if (truncated) lines = lines.Take(cap).ToList();
            int n = 0;
            foreach (var x in lines)
                builder.AddRow(HospitalDocumentRows.Merge(header, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["LineNo"] = ++n,
                    ["Procedure"] = HospitalDocumentRows.P(arabic, x.Name, x.NameEn),
                    ["ProcedureCode"] = x.Code,
                    ["Tooth"] = x.l.Tooth?.ToString(),
                    ["Surfaces"] = x.l.Surfaces,
                    ["Phase"] = HospitalDocumentRows.P(arabic, x.Title, x.TitleEn),
                    // A plan line is one visit's work; the fee is that session's.
                    ["Sessions"] = 1,
                    ["FeePerSession"] = x.l.Fee,
                    ["TotalFee"] = x.l.Fee,
                    ["LineStatus"] = x.l.Status == HospitalDentalLineStatus.Completed ? (arabic ? "تم" : "Done") : (arabic ? "مخطط" : "Planned"),
                }));
            return builder.Build(truncated, truncated ? null : lines.Count, Array.Empty<ReportFilter>(), new[] { ReportSort.By("LineNo") });
        }
    }

    // ---- the permission ------------------------------------------------------------------------------
    // hospital.reports.view is the hospital module's `read`, asked of the HOSPITAL access service — found by
    // its scope among the registered modules, so a host without the hospital denies instead of failing.
    // ---- Hospital.DischargeSummary (H55): the stay and the doctor's summary as ONE row ----------------------------
    // The stay's facts come from HospitalDischargeService.GetAsync — the same answer the page shows.
    public sealed class HospitalDischargeSummarySource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        private readonly IServiceProvider _services;
        public HospitalDischargeSummarySource(CrossDbContext db, IServiceProvider services) { _db = db; _services = services; }
        public string Key => HospitalDocumentDatasetCodes.DischargeSummary;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("AdmissionId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var svc = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<HospitalDischargeService>(_services);
            var (v, _) = await svc.GetAsync(ctx, id.Value, DateTime.UtcNow, cancellationToken);   // company-checked: another company's stay is null
            if (v == null) return builder.Build(totalRowCount: 0);
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var s = v.Summary;
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, arabic, v.Patient, null,
                "DS-" + v.Admission.ID.ToString("D6"), s?.SignedAt ?? s?.UpdatedAt ?? DateTime.UtcNow, v.Admission.AdmittedAt, cancellationToken);
            string P(string? ar, string? en) => HospitalDocumentRows.P(arabic, ar, en);
            header["DoctorName"] = s?.SignedByEmployeeId != null ? P(v.SignedBy, v.SignedByEn) : P(v.Attending, v.AttendingEn);
            header["LineNo"] = 1;
            header["AdmittedAt"] = v.Admission.AdmittedAt.ToLocalTime();
            header["DischargedAt"] = v.Admission.DischargedAt?.ToLocalTime();
            header["StayDays"] = v.StayDays;
            header["Ward"] = v.Ward == null ? null : P(v.Ward, v.WardEn) + (v.Room != null ? " · " + v.Room : "") + (v.Bed != null ? " · " + v.Bed : "");
            header["Attending"] = P(v.Attending, v.AttendingEn);
            header["AdmissionReason"] = v.Admission.Diagnosis;
            header["FinalDiagnosis"] = s?.FinalDiagnosis;
            header["Course"] = s?.Course;
            header["Procedures"] = s?.Procedures ?? (v.Operations.Count > 0 ? HospitalDischargeRules.ProceduresText(v.Operations, arabic) : null);
            header["Condition"] = s?.Condition == null ? null : HospitalDischargeLabels.Condition(s.Condition, arabic);
            header["Medications"] = s?.Medications;
            header["Instructions"] = s?.Instructions;
            header["FollowUpOn"] = s?.FollowUpOn;
            header["FollowUpWith"] = s?.FollowUpWith;
            header["Status"] = s?.Status == HospitalDischargeSummaryStatus.Signed ? (arabic ? "موقّع" : "Signed") : (arabic ? "مسودة" : "Draft");
            header["SignedBy"] = s?.SignedByEmployeeId != null ? P(v.SignedBy, v.SignedByEn) : null;
            header["SignedAt"] = s?.SignedAt?.ToLocalTime();
            builder.AddRow(header);
            return builder.Build(totalRowCount: 1);
        }
    }

    // ---- Hospital.SickLeave (H56): one certificate as ONE row; the diagnosis only when the doctor chose to print it --
    public sealed class HospitalSickLeaveSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalSickLeaveSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.SickLeave;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var columns = query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns;
            var builder = new ReportDataSetBuilder(columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("SickLeaveId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var l = await _db.Set<HospitalSickLeave>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (l == null) return builder.Build(totalRowCount: 0);
            var patient = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == l.PatientId && p.CompanyId == ctx.CompanyId, cancellationToken);
            if (patient == null) return builder.Build(totalRowCount: 0);
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var header = await HospitalDocumentRows.HeaderAsync(_db, ctx, arabic, patient, l.PractitionerId, l.LeaveNo, l.IssuedAt, l.IssuedAt, cancellationToken);
            header["LineNo"] = 1;
            header["FromDate"] = l.FromDate; header["ToDate"] = l.ToDate; header["Days"] = l.Days;
            header["Kind"] = l.Kind switch
            {
                HospitalSickLeaveKind.Rest => arabic ? "راحة تامة من العمل" : "Off work, at rest",
                HospitalSickLeaveKind.LightDuty => arabic ? "عمل خفيف" : "Light duties",
                _ => arabic ? "منوّم بالمستشفى" : "In hospital",
            };
            header["Diagnosis"] = l.ShowDiagnosis ? l.Diagnosis : null;   // the employer is owed the days, not the illness
            header["Note"] = l.Note;
            header["Void"] = l.VoidedAt == null ? null : (arabic ? "ملغاة: " : "VOID: ") + l.VoidReason;
            builder.AddRow(header);
            return builder.Build(totalRowCount: 1);
        }
    }

    /// H55: the condition at discharge in the print's language.
    public static class HospitalDischargeLabels
    {
        public static string Condition(string code, bool arabic) => code switch
        {
            HospitalDischargeCondition.Improved => arabic ? "تحسّن" : "Improved",
            HospitalDischargeCondition.Stable => arabic ? "مستقر" : "Stable",
            HospitalDischargeCondition.Unchanged => arabic ? "دون تغيير" : "Unchanged",
            HospitalDischargeCondition.Referred => arabic ? "محوّل لمنشأة أخرى" : "Referred",
            HospitalDischargeCondition.AgainstAdvice => arabic ? "خروج على مسؤوليته" : "Against medical advice",
            HospitalDischargeCondition.Deceased => arabic ? "وفاة" : "Deceased",
            _ => code,
        };
    }

    public sealed class HospitalReportModuleAuthority : IReportModuleAuthority
    {
        private readonly CrossBuy.BL.Platform.IModuleAccessService? _hospital;

        public HospitalReportModuleAuthority(IEnumerable<CrossBuy.BL.Platform.IModuleAccessService> modules)
        {
            _hospital = modules.FirstOrDefault(m => string.Equals(m.Scope, CrossBuy.BL.Platform.EntityRegistry.ScopeHospital, StringComparison.OrdinalIgnoreCase));
        }

        public string KeyPrefix => HospitalReportPermissions.Prefix;

        public Task<bool?> EvaluateAsync(string permissionKey, BusinessContext context, CancellationToken cancellationToken = default)
        {
            var action = permissionKey switch
            {
                HospitalReportPermissions.View => HospitalActions.Read,
                HospitalReportPermissions.Clinical => HospitalActions.ClinicalRead,   // H156
                HospitalReportPermissions.Finance => HospitalActions.Bill,
                _ => null,
            };
            if (action is null) return Task.FromResult<bool?>(null);
            if (_hospital is null) return Task.FromResult<bool?>(false);
            return WorkReportModuleAuthority.Decide(_hospital, CrossBuy.BL.Platform.EntityRegistry.ScopeHospital, action, context, cancellationToken);
        }
    }
}
