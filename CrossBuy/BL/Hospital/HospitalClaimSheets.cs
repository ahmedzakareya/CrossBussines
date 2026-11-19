using System.Globalization;
using System.Text;
using CrossBuy.Models.Context.Hospital;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H210 — the claim SHEET a third-party administrator (TPA) takes a batch in. Kuwait's TPAs each take their own
	// spreadsheet layout, so the sheet is the payer's: which columns, in which order, under which headers, with which
	// separator and date format. One row per claim line; the claim's own facts (the bill, the member, the patient, the
	// visit, the diagnoses, the approvals) repeat on each of its lines, as these sheets expect.
	//   · The file is UTF-8 with its mark, so a spreadsheet opens Arabic names as Arabic.
	//   · A text cell that starts with = + - @ is written with a leading apostrophe: a name is never run as a formula.
	//   · Amounts are written in the invariant culture with the currency's decimals (3 for KWD).
	// ==========================================================================================
	public sealed record HospitalClaimSheetField(string Key, string Ar, string En, bool Numeric);
	public sealed record HospitalClaimSheetColumn(string Key, string Header);
	public sealed record HospitalClaimSheetSetup(IReadOnlyList<HospitalClaimSheetColumn> Columns, string Delimiter, string DateFormat, bool Saved);

	public sealed class HospitalClaimSheetInput
	{
		public List<HospitalClaimSheetColumn>? Columns { get; set; }
		public string? Delimiter { get; set; }
		public string? DateFormat { get; set; }
	}

	public static class HospitalClaimSheets
	{
		/// The exchange's format for a sheet (the FHIR files are "FHIR-R4").
		public const string Format = "CSV";

		public static readonly IReadOnlyList<HospitalClaimSheetField> Fields = new HospitalClaimSheetField[]
		{
			new("BatchNo", "رقم الدفعة", "Batch no.", false),
			new("ClaimNo", "رقم المطالبة", "Claim no.", false),
			new("MemberNo", "رقم العضوية", "Member no.", false),
			new("MemberClass", "فئة التغطية", "Class", false),
			new("PatientName", "اسم المريض", "Patient name", false),
			new("PatientNameAr", "اسم المريض بالعربية", "Patient name (Arabic)", false),
			new("CivilId", "الرقم المدني", "Civil ID", false),
			new("FileNo", "رقم الملف", "File no.", false),
			new("BirthDate", "تاريخ الميلاد", "Date of birth", false),
			new("Sex", "الجنس", "Sex", false),
			new("VisitDate", "تاريخ الخدمة", "Service date", false),
			new("VisitType", "نوع الزيارة", "Visit type", false),
			new("Diagnoses", "رموز التشخيص", "Diagnosis codes", false),
			new("PrimaryDiagnosis", "التشخيص الرئيسي", "Primary diagnosis", false),
			new("ApprovalNo", "رقم الموافقة", "Approval no.", false),
			new("LineNo", "رقم البند", "Line no.", true),
			new("ServiceCode", "رمز الخدمة", "Service code", false),
			new("ServiceName", "الخدمة", "Service", false),
			new("Category", "الفئة", "Category", false),
			new("Gross", "الإجمالي", "Gross", true),
			new("PatientShare", "حصة المريض", "Patient share", true),
			new("Claimed", "المطالَب به", "Claimed", true),
			new("Currency", "العملة", "Currency", false),
		};

		public static readonly IReadOnlyList<string> Delimiters = new[] { ",", ";", "TAB" };
		public static readonly IReadOnlyList<string> DateFormats = new[] { "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "dd-MM-yyyy" };

		/// The sheet a payer gets until it has its own: every column, English headers, comma, ISO dates.
		public static HospitalClaimSheetSetup Standard() => new(Fields.Where(f => f.Key != "PatientNameAr").Select(f => new HospitalClaimSheetColumn(f.Key, f.En)).ToList(), ",", "yyyy-MM-dd", false);

		public static string Pack(IEnumerable<HospitalClaimSheetColumn> cols) => string.Join("|", cols.Select(c => c.Key + "=" + c.Header.Replace("|", "/").Replace("=", "-")));

		public static IReadOnlyList<HospitalClaimSheetColumn> Unpack(string? packed) =>
			(packed ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p => { int i = p.IndexOf('='); return i < 0 ? new HospitalClaimSheetColumn(p, p) : new HospitalClaimSheetColumn(p[..i], p[(i + 1)..]); })
				.Where(c => Fields.Any(f => f.Key == c.Key)).ToList();

		public static string? Refusal(HospitalClaimSheetInput i)
		{
			var cols = i.Columns ?? new();
			if (cols.Count == 0) return "Choose at least one column";
			if (cols.Any(c => c == null || !Fields.Any(f => f.Key == c.Key))) return "not_found";
			if (cols.Select(c => c.Key).Distinct().Count() != cols.Count) return "A column is chosen twice";
			if (cols.Any(c => string.IsNullOrWhiteSpace(c.Header) || c.Header.Trim().Length > 60)) return "Every column needs a header of at most 60 characters";
			if (Pack(cols.Select(c => c with { Header = c.Header.Trim() })).Length > 2000) return "The text is too long";
			if (string.IsNullOrEmpty(i.Delimiter) || !Delimiters.Contains(i.Delimiter)) return "Choose the separator";
			return string.IsNullOrEmpty(i.DateFormat) || !DateFormats.Contains(i.DateFormat) ? "Choose the date format" : null;
		}

		public static int Decimals(string currency) => currency is "KWD" or "BHD" or "OMR" or "JOD" or "IQD" or "LYD" or "TND" ? 3 : 2;

		/// One cell: quoted when it holds the separator, a quote or a line break; a formula-looking text made inert.
		public static string Cell(string? v, bool numeric, char sep)
		{
			var s = v ?? "";
			if (!numeric && s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
			return s.IndexOfAny(new[] { sep, '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
		}

		/// The sheet: the header row, then one row per claim line.
		public static string Render(HospitalClaimSheetSetup setup, string batchNo, IEnumerable<HospitalEClaimClaimData> claims, string currency)
		{
			char sep = setup.Delimiter == "TAB" ? '\t' : setup.Delimiter[0];
			var inv = CultureInfo.InvariantCulture;
			string money = "0." + new string('0', Decimals(currency));
			string Day(DateTime? d) => d?.ToString(setup.DateFormat, inv) ?? "";
			var numeric = Fields.ToDictionary(f => f.Key, f => f.Numeric);
			var sb = new StringBuilder();
			sb.Append(string.Join(sep, setup.Columns.Select(c => Cell(c.Header, false, sep)))).Append("\r\n");
			foreach (var d in claims)
			{
				var primary = d.Diagnoses.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder).FirstOrDefault();
				int n = 0;
				foreach (var l in d.Lines)
				{
					n++;
					string? V(string key) => key switch
					{
						"BatchNo" => batchNo,
						"ClaimNo" => d.BillNo,
						"MemberNo" => d.Policy.PolicyNo,
						"MemberClass" => d.Policy.MemberClass,
						"PatientName" => string.IsNullOrWhiteSpace(d.Patient.NameEn) ? d.Patient.Name : d.Patient.NameEn,
						"PatientNameAr" => d.Patient.Name,
						"CivilId" => d.Patient.NationalId,
						"FileNo" => d.Patient.Mrn,
						"BirthDate" => Day(d.Patient.BirthDate),
						"Sex" => d.Patient.Sex == HospitalPatientSex.Female ? "F" : "M",
						"VisitDate" => Day(d.BillDay),
						"VisitType" => d.Stay ? "IP" : "OP",
						"Diagnoses" => d.Diagnoses.Count > 0 ? string.Join(";", d.Diagnoses.OrderByDescending(x => x.IsPrimary).ThenBy(x => x.SortOrder).Select(x => x.Code)) : d.FinalDiagnosis,
						"PrimaryDiagnosis" => primary == null ? d.FinalDiagnosis : primary.Code + " " + (string.IsNullOrWhiteSpace(primary.NameEn) ? primary.Name : primary.NameEn),
						"ApprovalNo" => string.Join(";", d.ApprovalRefs),
						"LineNo" => n.ToString(inv),
						"ServiceCode" => l.Code,
						"ServiceName" => string.IsNullOrWhiteSpace(l.DescriptionEn) ? l.Description : l.DescriptionEn,
						"Category" => l.Category,
						"Gross" => l.Amount.ToString(money, inv),
						"PatientShare" => (l.Amount - l.InsurerAmount).ToString(money, inv),
						"Claimed" => l.InsurerAmount.ToString(money, inv),
						"Currency" => currency,
						_ => null,
					};
					sb.Append(string.Join(sep, setup.Columns.Select(c => Cell(V(c.Key), numeric.GetValueOrDefault(c.Key), sep)))).Append("\r\n");
				}
			}
			return sb.ToString();
		}
	}
}
