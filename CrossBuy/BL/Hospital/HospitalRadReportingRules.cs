using System.Text;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	public sealed record HospitalRadSection(string Heading, string HeadingEn, bool Required);
	public sealed record HospitalRadStandardTemplate(string Code, string Name, string NameEn, string? Modality, IReadOnlyList<HospitalRadSection> Sections);

	// ==========================================================================================
	// HOSPITAL H259 — the rules of structured reports and peer review, free of the database.
	// A template's sections are kept one per line, "Heading|HeadingEn|1". In a report a section is its heading line
	// ("Heading:") and the text under it, up to the next heading of the same template.
	// ==========================================================================================
	public static class HospitalRadReportingRules
	{
		public static IReadOnlyList<HospitalRadSection> Parse(string? sections) =>
			(sections ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|'))
				.Where(p => p.Length >= 2 && p[0].Trim().Length > 0 && p[1].Trim().Length > 0)
				.Select(p => new HospitalRadSection(p[0].Trim(), p[1].Trim(), p.Length > 2 && p[2].Trim() == "1")).ToList();

		public static string Serialize(IEnumerable<HospitalRadSection> sections) =>
			string.Join("\n", sections.Select(s => s.Heading.Replace("|", "/").Trim() + "|" + s.HeadingEn.Replace("|", "/").Trim() + "|" + (s.Required ? "1" : "0")));

		public static string? Refusal(string? code, string? name, string? nameEn, string? modality, IReadOnlyList<HospitalRadSection> sections)
		{
			if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 20) return "Enter a code of up to 20 characters";
			if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(nameEn)) return "Enter the name in Arabic and in English";
			if (modality != null && !HospitalRadModality.All.Contains(modality)) return "Unknown modality";
			if (sections.Count == 0) return "Add at least one section";
			if (sections.Count > 15) return "A template has 15 sections at most";
			if (!sections.Any(s => s.Required)) return "Mark at least one section as required";
			if (sections.GroupBy(s => s.Heading).Any(g => g.Count() > 1) || sections.GroupBy(s => s.HeadingEn, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1)) return "Two sections have the same heading";
			return Serialize(sections).Length > 2000 ? "The sections are too long" : null;
		}

		/// The headings to write into an empty report (Arabic or English).
		public static string Skeleton(IReadOnlyList<HospitalRadSection> sections, bool english)
		{
			var sb = new StringBuilder();
			foreach (var s in sections) sb.Append(english ? s.HeadingEn : s.Heading).Append(":\n\n");
			return sb.ToString().TrimEnd('\n') + "\n";
		}

		/// The text written under each heading of the template (empty when the heading is missing).
		public static Dictionary<string, string> Read(string? text, IReadOnlyList<string> headings)
		{
			var res = headings.ToDictionary(h => h, _ => "");
			string? cur = null; var sb = new StringBuilder();
			void Flush() { if (cur != null) res[cur] = sb.ToString().Trim(); sb.Clear(); }
			foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
			{
				var line = raw.Trim();
				var h = headings.FirstOrDefault(x => line.StartsWith(x + ":", StringComparison.OrdinalIgnoreCase) || line.Equals(x, StringComparison.OrdinalIgnoreCase));
				if (h != null) { Flush(); cur = h; sb.Append(line.Length > h.Length + 1 ? line[(h.Length + 1)..] : ""); continue; }
				if (cur != null) sb.Append(' ').Append(line);
			}
			Flush();
			return res;
		}

		/// The required sections left empty in BOTH languages (a section written in either one counts).
		public static IReadOnlyList<HospitalRadSection> Missing(IReadOnlyList<HospitalRadSection> sections, string? findings, string? findingsEn)
		{
			var ar = Read(findings, sections.Select(s => s.Heading).ToList());
			var en = Read(findingsEn, sections.Select(s => s.HeadingEn).ToList());
			return sections.Where(s => s.Required && ar[s.Heading].Length == 0 && en[s.HeadingEn].Length == 0).ToList();
		}

		// ---------------- peer review (RADPEER) ----------------

		public static string? ReviewRefusal(int score, bool significant, string? note, int? radiologist, int? reviewer)
		{
			if (score is < 1 or > 3) return "Choose the score: 1, 2 or 3";
			if (radiologist == null) return "This report is not signed";
			if (reviewer == null || reviewer == radiologist) return "A report is reviewed by another radiologist";
			if (score > 1 && string.IsNullOrWhiteSpace(note)) return "Describe the discrepancy";
			return significant && score == 1 ? "Agreement is not clinically significant" : null;
		}

		/// Discrepancy (score 2 or 3) and significant-discrepancy rates of a radiologist's reviewed reports.
		public static (int Reviewed, decimal Discrepancy, decimal Significant) Rates(IEnumerable<(int Score, bool Significant)> reviews)
		{
			var l = reviews.ToList();
			if (l.Count == 0) return (0, 0, 0);
			return (l.Count, Math.Round(100m * l.Count(r => r.Score >= 2) / l.Count, 1, MidpointRounding.AwayFromZero), Math.Round(100m * l.Count(r => r.Significant) / l.Count, 1, MidpointRounding.AwayFromZero));
		}

		/// A stable pseudo-random order for the review queue (the same study list every time on one day).
		public static int SampleKey(int orderId, DateTime day) => (int)((uint)(orderId * 2654435761u) ^ (uint)(day.Year * 1000 + day.DayOfYear));

		private static HospitalRadSection S(string ar, string en, bool req = true) => new(ar, en, req);
		public static readonly IReadOnlyList<HospitalRadStandardTemplate> Standard = new[]
		{
			new HospitalRadStandardTemplate("CXR", "أشعة الصدر", "Chest X-ray", HospitalRadModality.XRay, new[]
				{ S("دواعي الفحص", "Indication", false), S("التقنية", "Technique", false), S("الرئتان", "Lungs"), S("الجنبة", "Pleura"), S("القلب والمنصف", "Heart and mediastinum"), S("العظام والأنسجة الرخوة", "Bones and soft tissues", false) }),
			new HospitalRadStandardTemplate("CT-HEAD", "أشعة مقطعية للرأس", "CT head", HospitalRadModality.CT, new[]
				{ S("دواعي الفحص", "Indication", false), S("التقنية", "Technique"), S("نسيج الدماغ", "Brain parenchyma"), S("النزف", "Haemorrhage"), S("البطينات والأحيزة", "Ventricles and extra-axial spaces"), S("العظام", "Bones", false) }),
			new HospitalRadStandardTemplate("US-ABD", "موجات صوتية على البطن", "Abdominal ultrasound", HospitalRadModality.Ultrasound, new[]
				{ S("دواعي الفحص", "Indication", false), S("الكبد", "Liver"), S("المرارة والقنوات الصفراوية", "Gallbladder and bile ducts"), S("البنكرياس", "Pancreas", false), S("الطحال", "Spleen"), S("الكليتان", "Kidneys"), S("سوائل حرة", "Free fluid", false) }),
			new HospitalRadStandardTemplate("MR-BRAIN", "رنين مغناطيسي للدماغ", "MRI brain", HospitalRadModality.MRI, new[]
				{ S("دواعي الفحص", "Indication", false), S("التقنية", "Technique"), S("نسيج الدماغ", "Brain parenchyma"), S("الانتشار", "Diffusion"), S("البطينات", "Ventricles"), S("الأوعية", "Vessels", false) }),
		};
	}
}
