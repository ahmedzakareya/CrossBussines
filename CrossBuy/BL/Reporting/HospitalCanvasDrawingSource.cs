using System.Globalization;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Reporting
{
    // ---- Hospital.CanvasDrawing (H111): one SIGNED clinical drawing as ONE row — the picture the doctor saw, its coded
    // marks in words, and its results read again by the server's own rules from the state it stored ----------------
    public sealed class HospitalCanvasDrawingSource : IReportDataSource
    {
        private readonly CrossDbContext _db;
        public HospitalCanvasDrawingSource(CrossDbContext db) { _db = db; }
        public string Key => HospitalDocumentDatasetCodes.CanvasDrawing;

        public async Task<ReportDataSet> FetchAsync(ReportDataQuery query, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            var ctx = query.Context;
            var builder = new ReportDataSetBuilder(query.RequestedColumns.Count > 0 ? query.RequestedColumns : query.Definition.Columns);
            if (ctx.CompanyId <= 0) return builder.Build(totalRowCount: 0);
            var id = query.Parameters.GetInt("SheetId");
            if (id is not > 0) return builder.Build(totalRowCount: 0);
            var s = await _db.Set<HospitalCanvasSheet>().AsNoTracking().FirstOrDefaultAsync(x => x.ID == id.Value && x.CompanyId == ctx.CompanyId, cancellationToken);
            if (s == null || s.Status != HospitalCanvasStatus.Signed) return builder.Build(totalRowCount: 0);   // a paper says what was signed
            var enc = await _db.Set<HospitalEncounter>().AsNoTracking().FirstOrDefaultAsync(e => e.ID == s.EncounterId && e.CompanyId == ctx.CompanyId, cancellationToken);
            var patient = await _db.Set<HospitalPatient>().AsNoTracking().FirstOrDefaultAsync(p => p.ID == s.PatientId && p.CompanyId == ctx.CompanyId, cancellationToken);
            if (enc == null || patient == null) return builder.Build(totalRowCount: 0);
            bool ar = AccountingSourceHelpers.Arabic(query);
            var h = await HospitalDocumentRows.HeaderAsync(_db, ctx, ar, patient, enc.PractitionerId, "V-" + enc.ID.ToString("D6"), s.SignedAt ?? enc.StartedAt, enc.StartedAt, cancellationToken);
            h["LineNo"] = 1;
            h["DrawingTitle"] = HospitalCanvasPaper.Title(s.Kind, ar);
            h["Drawing"] = string.IsNullOrEmpty(s.SnapshotSvg) ? null : ReportImageData.SvgUri(s.SnapshotSvg);
            var marks = await _db.Set<HospitalCanvasMark>().AsNoTracking().Where(m => m.SheetId == s.ID && m.CompanyId == ctx.CompanyId).OrderBy(m => m.LesionNo).ToListAsync(cancellationToken);
            h["Findings"] = HospitalCanvasPaper.Findings(s.Kind, marks, ar);
            h["Results"] = HospitalCanvasPaper.Results(s.Kind, s.StateJson, patient, enc.StartedAt, ar);
            h["SignedAt"] = s.SignedAt?.ToLocalTime();
            builder.AddRow(h);
            return builder.Build(totalRowCount: 1);
        }
    }

    /// The words of a drawing's paper, in the print's language.
    public static class HospitalCanvasPaper
    {
        public static string Title(string kind, bool ar) => kind switch
        {
            HospitalCanvasKind.Body => ar ? "خريطة الجسم" : "Body map",
            HospitalCanvasKind.Fundus => ar ? "رسم قاع العين" : "Fundus drawing",
            HospitalCanvasKind.Burn => ar ? "خريطة الحروق" : "Burn chart",
            HospitalCanvasKind.Audio => ar ? "مخطط السمع" : "Audiogram",
            HospitalCanvasKind.Joints => ar ? "مفاصل الروماتويد — DAS28" : "Rheumatoid joints — DAS28",
            HospitalCanvasKind.Pedigree => ar ? "شجرة العائلة" : "Pedigree",
            _ => kind,
        };

        private static string N(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string Ltr(string s) => "‎" + s + "‎";

        /// Each coded mark as a line: the lesion, the finding, the size and where.
        public static string? Findings(string kind, IReadOnlyList<HospitalCanvasMark> marks, bool ar)
        {
            if (marks.Count == 0) return null;
            var list = HospitalCanvasRules.FindingsOf(kind);
            return string.Join("\n", marks.Select(m =>
            {
                var f = list.FirstOrDefault(x => x.Key == m.Finding);
                string what = f == null ? m.Finding : ar ? f.Ar : f.En;
                string where = kind == HospitalCanvasKind.Fundus
                    ? (m.Side == "R" ? "OD" : "OS") + " · " + HospitalEyeLetterLabels.Where(m.Region, ar)
                    : (HospitalCanvasRules.BodyRegions.FirstOrDefault(r => r.Key == m.Region) is { } r ? (ar ? r.Ar : r.En) : m.Region ?? "")
                      + " — " + (m.Side == "B" ? (ar ? "خلفي" : "back") : (ar ? "أمامي" : "front"));
                string size = m.SizeMm is decimal sz ? " · " + Ltr(N(sz) + " mm") : "";
                string code = string.IsNullOrEmpty(m.FindingCode) ? "" : " · " + Ltr(m.FindingCode!);
                return (kind == HospitalCanvasKind.Body ? m.LesionNo + " · " : "") + what + size + " · " + where + code;
            }));
        }

        /// The drawing's results, read again by the server's rules from the state it stored.
        public static string? Results(string kind, string? state, HospitalPatient p, DateTime at, bool ar)
        {
            switch (kind)
            {
                case HospitalCanvasKind.Burn:
                {
                    var r = HospitalBurnRules.Read(state, HospitalPatientService.AgeOn(p.BirthDate, at)).Result;
                    if (r == null || r.Tbsa == 0) return null;
                    var s = (ar ? "نسبة الحرق: " : "TBSA: ") + Ltr(N(r.Tbsa) + "%");
                    if (r.FluidsMl is int fl) s += " · " + (ar ? "سوائل باركلاند ٢٤ ساعة: " : "Parkland 24 h: ") + Ltr(fl.ToString("N0", CultureInfo.InvariantCulture) + " mL")
                        + (ar ? " (نصفها في أول ٨ ساعات من وقت الحرق)" : " (half in the first 8 h from the burn)");
                    if (r.Refer) s += "\n" + (ar ? "تحويل لمركز حروق: النسبة ٢٠٪ أو أكثر" : "Refer to a burn centre: 20% or more");
                    return s;
                }
                case HospitalCanvasKind.Audio:
                {
                    var r = HospitalAudioRules.Read(state).Result;
                    if (r == null || (r.PtaR == null && r.PtaL == null)) return null;
                    string G(string? g) => g == null ? "" : " (" + (ar ? g switch { "Normal" => "طبيعي", "Mild" => "خفيف", "Moderate" => "متوسط", "Moderately severe" => "متوسط إلى شديد", "Severe" => "شديد", "Profound" => "عميق", _ => "كامل" } : g) + ")";
                    var lines = new List<string>
                    {
                        (ar ? "متوسط السمع — يمين: " : "Pure-tone average — right: ") + (r.PtaR is decimal a ? Ltr(N(a) + " dB") + G(r.GradeR) : "—")
                            + (ar ? " · يسار: " : " · left: ") + (r.PtaL is decimal b ? Ltr(N(b) + " dB") + G(r.GradeL) : "—"),
                    };
                    if (r.GapR) lines.Add(ar ? "فجوة هواء-عظم يمين ≥ ١٥ — فقد توصيلي" : "Right air-bone gap ≥ 15 — conductive loss");
                    if (r.GapL) lines.Add(ar ? "فجوة هواء-عظم يسار ≥ ١٥ — فقد توصيلي" : "Left air-bone gap ≥ 15 — conductive loss");
                    if (r.Asymmetry) lines.Add(ar ? "فرق بين الأذنين ≥ ١٥ — يستبعد السبب العصبي" : "Ear difference ≥ 15 — rule out a retrocochlear cause");
                    return string.Join("\n", lines);
                }
                case HospitalCanvasKind.Joints:
                {
                    var r = HospitalDas28Rules.Read(state).Result;
                    if (r == null) return null;
                    string band = r.Band switch { "High" => ar ? "نشاط مرتفع" : "High activity", "Moderate" => ar ? "نشاط متوسط" : "Moderate activity", "Low" => ar ? "نشاط منخفض" : "Low activity", "Remission" => ar ? "هدأة" : "Remission", _ => "" };
                    return (ar ? "مؤلم: " : "Tender: ") + Ltr(r.Tender + "/28") + (ar ? " · متورم: " : " · swollen: ") + Ltr(r.Swollen + "/28")
                        + (r.Das28 is decimal d ? " · DAS28-ESR: " + Ltr(N(d)) + " — " + band : "");
                }
                case HospitalCanvasKind.Pedigree:
                {
                    var r = HospitalPedigreeRules.Read(state, p.Sex).Result;
                    if (r == null) return null;
                    var lines = new List<string>();
                    if (r.Yield.Consanguinity) lines.Add(ar ? "زواج أقارب في العائلة" : "Consanguinity in the family");
                    lines.AddRange(r.Yield.Conditions.Select(c => c.Cond + (ar ? " في " + c.Generations + " أجيال" : " across " + c.Generations + " generations")));
                    return lines.Count == 0 ? null : string.Join("\n", lines);
                }
                default: return null;
            }
        }
    }
}
