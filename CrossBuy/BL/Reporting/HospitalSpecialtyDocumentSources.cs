using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ============================================================================================
    // HOSPITAL H109 — THE SPECIALTY VISIT'S PAPERS (the approved prototype: sc-eye's outputs, sc-allergy's emergency plan),
    // printed only through Report Studio. Each is ONE row read from what the SIGNED visit stored — the observations, the
    // fundus drawing's marks, the prescription — never from a form still being typed: a paper says what was signed.
    //   Hospital.GlassesRx       the refraction of each eye, the near add, the corrected vision
    //   Hospital.EyeLetter       to the diabetes doctor: vision, pressure, retinopathy grade, disc, the fundus findings, plan
    //   Hospital.AllergyPlan     the anaphylaxis emergency action plan: triggers, the adrenaline device, the steps
    // ============================================================================================
    internal static class HospitalVisitPaper
    {
        /// The signed visit, its patient and the header — or null when it cannot be printed (not found, not signed).
        public static async Task<(HospitalEncounter Visit, HospitalPatient Patient, Dictionary<string, object?> Header)?> LoadAsync(
            CrossDbContext db, ReportDataQuery query, bool arabic, CancellationToken ct)
        {
            var ctx = query.Context;
            if (ctx.CompanyId <= 0) return null;
            var id = query.Parameters.GetInt("EncounterId");
            if (id is not > 0) return null;
            var enc = await db.Set<HospitalEncounter>().AsNoTracking().FirstOrDefaultAsync(e => e.ID == id.Value && e.CompanyId == ctx.CompanyId, ct);
            if (enc == null || enc.Status != HospitalEncounterStatus.Signed) return null;
            var patient = await db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == enc.PatientId && p.CompanyId == ctx.CompanyId, ct);
            if (patient == null) return null;
            var header = await HospitalDocumentRows.HeaderAsync(db, ctx, arabic, patient, enc.PractitionerId, "V-" + enc.ID.ToString("D6"), enc.SignedAt ?? enc.StartedAt, enc.StartedAt, ct);
            header["LineNo"] = 1;
            return (enc, patient, header);
        }

        /// The visit's stored values by concept code and side ("" for none): the number, or a choice's label.
        public static async Task<Dictionary<(string Code, string Side), string>> ValuesAsync(CrossDbContext db, int companyId, int encounterId, bool arabic, CancellationToken ct)
        {
            var rows = await (from o in db.Set<HospitalClinicalObservation>().AsNoTracking().Where(o => o.CompanyId == companyId && o.EncounterId == encounterId)
                              join c in db.Set<HospitalConcept>().AsNoTracking() on o.ConceptId equals c.ID
                              select new { c.Code, c.Unit, c.Decimals, c.Choices, o.Side, o.Value, o.ValueCode }).ToListAsync(ct);
            var d = new Dictionary<(string, string), string>();
            foreach (var r in rows)
            {
                string? v = r.ValueCode != null
                    ? HospitalConceptRules.Choices(r.Choices).Where(x => x.Code == r.ValueCode).Select(x => arabic ? x.Ar : x.En).FirstOrDefault() ?? r.ValueCode
                    : r.Value is decimal n ? (r.Unit == "logMAR" ? HospitalVision.Snellen6(n) : HospitalConceptRules.Num(n, r.Decimals)) : null;
                if (v != null) d[(r.Code, r.Side ?? "")] = v;
            }
            return d;
        }

        /// A lens power as an optician reads it: always signed, two decimals (+1.25, −0.75); 0 is plano.
        public static string? Power(string? v)
        {
            if (v == null || !decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return v;
            return Ltr(d == 0 ? "plano" : (d > 0 ? "+" : "−") + Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture));
        }

        /// A left-to-right run inside a right-to-left paper ("−1.75", "24 mmHg", "175°"): bracketed by left-to-right marks.
        public static string Ltr(string s) => "\u200E" + s + "\u200E";
    }

    // ---- Hospital.GlassesRx (H109) -----------------------------------------------------------------
    public sealed class HospitalGlassesRxSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalGlassesRxSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.GlassesRx;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var paper = await HospitalVisitPaper.LoadAsync(_db, query, arabic, cancellationToken);
            if (paper is not { } p) return builder.Build(totalRowCount: 0);
            var v = await HospitalVisitPaper.ValuesAsync(_db, p.Visit.CompanyId, p.Visit.ID, arabic, cancellationToken);
            if (!v.Keys.Any(k => k.Code.StartsWith("REF-"))) return builder.Build(totalRowCount: 0);   // no refraction on this visit: no prescription
            string? G(string code, string side) => v.TryGetValue((code, side), out var x) ? x : null;
            var h = p.Header;
            foreach (var (side, pre) in new[] { ("R", "Od"), ("L", "Os") })
            {
                h[pre + "Sph"] = HospitalVisitPaper.Power(G("REF-SPH", side));
                h[pre + "Cyl"] = HospitalVisitPaper.Power(G("REF-CYL", side));
                h[pre + "Axis"] = G("REF-AXIS", side) is string ax ? HospitalVisitPaper.Ltr(ax + "°") : null;
                h[pre + "Add"] = HospitalVisitPaper.Power(G("REF-ADD", side));
                h[pre + "Va"] = G("79880-1", side);
            }
            h["Notes"] = p.Visit.PlanNote;
            builder.AddRow(h);
            return builder.Build(totalRowCount: 1);
        }
    }

    // ---- Hospital.EyeLetter (H109) -----------------------------------------------------------------
    public sealed class HospitalEyeLetterSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalEyeLetterSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.EyeLetter;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var paper = await HospitalVisitPaper.LoadAsync(_db, query, arabic, cancellationToken);
            if (paper is not { } p) return builder.Build(totalRowCount: 0);
            var v = await HospitalVisitPaper.ValuesAsync(_db, p.Visit.CompanyId, p.Visit.ID, arabic, cancellationToken);
            string? G(string code, string side) => v.TryGetValue((code, side), out var x) ? x : null;
            var h = p.Header;
            foreach (var (side, pre) in new[] { ("R", "Od"), ("L", "Os") })
            {
                h[pre + "Va"] = G("79880-1", side);
                h[pre + "Iop"] = G("79892-6", side) is string iop ? HospitalVisitPaper.Ltr(iop + " mmHg") : null;
                h[pre + "Dr"] = G("DR-GRADE", side);
                h[pre + "Cdr"] = G("CDR", side);
            }
            // the fundus drawing's findings, as the canvas wrote them (eye · finding · where)
            var marks = await (from m in _db.Set<HospitalCanvasMark>().AsNoTracking()
                               join s in _db.Set<HospitalCanvasSheet>().AsNoTracking() on m.SheetId equals s.ID
                               where s.EncounterId == p.Visit.ID && s.CompanyId == p.Visit.CompanyId && s.Kind == HospitalCanvasKind.Fundus
                               orderby m.Side, m.ID
                               select new { m.Side, m.Finding, m.Region }).ToListAsync(cancellationToken);
            h["FundusFindings"] = marks.Count == 0 ? null : string.Join("\n", marks.Select(m =>
            {
                var f = HospitalCanvasRules.FundusFindings.FirstOrDefault(x => x.Key == m.Finding);
                return (m.Side == "R" ? "OD" : "OS") + " · " + (f == null ? m.Finding : arabic ? f.Ar : f.En) + " · " + HospitalEyeLetterLabels.Where(m.Region, arabic);
            }));
            var dx = await (from d in _db.Set<HospitalEncounterDiagnosis>().AsNoTracking().Where(d => d.EncounterId == p.Visit.ID)
                            join c in _db.Set<HospitalDiagnosisCode>().AsNoTracking().Where(c => c.CompanyId == p.Visit.CompanyId) on d.Code equals c.Code into cs
                            from c in cs.DefaultIfEmpty()
                            orderby d.IsPrimary descending
                            select new { d.Code, Name = c == null ? null : arabic ? c.Name : c.NameEn ?? c.Name }).ToListAsync(cancellationToken);
            h["Assessment"] = dx.Count == 0 ? null : string.Join("\n", dx.Select(d => d.Code + (d.Name == null ? "" : " — " + d.Name)));
            h["Plan"] = p.Visit.PlanNote;
            builder.AddRow(h);
            return builder.Build(totalRowCount: 1);
        }
    }

    public static class HospitalEyeLetterLabels
    {
        /// "MP@3" → "mid-periphery · 3 o'clock", in the print's language.
        public static string Where(string? region, bool arabic)
        {
            if (string.IsNullOrEmpty(region) || region.IndexOf('@') < 0) return region ?? "";
            var parts = region.Split('@');
            string zone = parts[0] switch
            {
                "PP" => arabic ? "القطب الخلفي" : "posterior pole",
                "MP" => arabic ? "المحيط المتوسط" : "mid-periphery",
                _ => arabic ? "المحيط البعيد" : "far periphery",
            };
            return zone + " · " + (arabic ? "الساعة " + parts[1] : parts[1] + " o'clock");
        }
    }

    // ---- Hospital.AllergyPlan (H109) ---------------------------------------------------------------
    public sealed class HospitalAllergyPlanSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalAllergyPlanSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.AllergyPlan;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
            bool arabic = AccountingSourceHelpers.Arabic(query);
            var paper = await HospitalVisitPaper.LoadAsync(_db, query, arabic, cancellationToken);
            if (paper is not { } p) return builder.Build(totalRowCount: 0);
            var h = p.Header;
            h["Triggers"] = string.IsNullOrWhiteSpace(p.Patient.Allergies) ? null : p.Patient.Allergies;
            bool asthma = (p.Patient.ChronicConditions ?? "").Contains("ربو") || (p.Patient.ChronicConditions ?? "").Contains("asthma", StringComparison.OrdinalIgnoreCase)
                || await _db.Set<HospitalEncounterDiagnosis>().AsNoTracking().Join(_db.Set<HospitalEncounter>().AsNoTracking().Where(e => e.PatientId == p.Patient.ID && e.CompanyId == p.Visit.CompanyId), d => d.EncounterId, e => e.ID, (d, e) => d.Code).AnyAsync(c => c.StartsWith("J45"), cancellationToken);
            h["Asthma"] = asthma ? (arabic ? "نعم — خطر أعلى: الأدرينالين أولاً عند أي ضيق تنفس" : "Yes — higher risk: adrenaline first for any breathing trouble") : (arabic ? "لا" : "No");
            // the adrenaline auto-injector prescribed in the last year, by either name of the medicine
            var since = DateTime.UtcNow.AddDays(-365);
            var rx = await (from l in _db.Set<HospitalPrescriptionLine>().AsNoTracking()
                            join e in _db.Set<HospitalEncounter>().AsNoTracking() on l.EncounterId equals e.ID
                            where e.PatientId == p.Patient.ID && e.CompanyId == p.Visit.CompanyId && e.StartedAt >= since
                            orderby e.StartedAt descending
                            select new { l.Drug, l.DrugEn, l.Instructions, l.InstructionsEn }).ToListAsync(cancellationToken);
            var epi = rx.FirstOrDefault(x => HospitalAdrenalineRules.Mentions(x.Drug + " " + x.DrugEn, HospitalAdrenalineRules.AdrenalineWords));
            h["AdrenalineDevice"] = epi == null ? (arabic ? "غير موصوفة — اطلب من طبيبك" : "Not prescribed — ask your doctor") : (arabic ? epi.Drug : epi.DrugEn ?? epi.Drug);
            h["AdrenalineUse"] = epi == null ? null : (arabic ? epi.Instructions : epi.InstructionsEn ?? epi.Instructions);
            builder.AddRow(h);
            return builder.Build(totalRowCount: 1);
        }
    }
}
