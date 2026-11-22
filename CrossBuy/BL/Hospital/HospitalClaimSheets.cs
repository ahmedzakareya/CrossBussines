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
	/// A column of the payer's sheet: a field, or (H229) a FIXED value the TPA wants on every row - the hospital's provider
	/// code, a plan code, or nothing at all for a column it keeps for itself.
	public sealed record HospitalClaimSheetColumn(string Key, string Header, string? Value = null);
	/// H229: a heading read from the payer's own template, and the field it most likely means (Fixed: none matched).
	public sealed record HospitalClaimSheetTemplateColumn(string Header, string Key);
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
		/// H229: a column with the same value on every row (empty when the TPA keeps it blank).
		public const string FixedKey = "Fixed";

		public static bool Known(string key) => key == FixedKey || Fields.Any(f => f.Key == key);

		public static HospitalClaimSheetSetup Standard() => new(Fields.Where(f => f.Key != "PatientNameAr").Select(f => new HospitalClaimSheetColumn(f.Key, f.En)).ToList(), ",", "yyyy-MM-dd", false);

		private static string Safe(string? s) => (s ?? "").Replace("|", "/").Replace("=", "-").Replace("~", "-");

		/// key=Header, or Fixed~value=Header for a fixed column.
		public static string Pack(IEnumerable<HospitalClaimSheetColumn> cols) =>
			string.Join("|", cols.Select(c => (c.Key == FixedKey ? FixedKey + "~" + Safe(c.Value) : c.Key) + "=" + Safe(c.Header)));

		public static IReadOnlyList<HospitalClaimSheetColumn> Unpack(string? packed) =>
			(packed ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Select(p =>
			{
				int i = p.IndexOf('=');
				string k = i < 0 ? p : p[..i], h = i < 0 ? p : p[(i + 1)..];
				if (k.StartsWith(FixedKey + "~", StringComparison.Ordinal)) return new HospitalClaimSheetColumn(FixedKey, h, k[(FixedKey.Length + 1)..]);
				return new HospitalClaimSheetColumn(k, h);
			}).Where(c => Known(c.Key)).ToList();

		public static string? Refusal(HospitalClaimSheetInput i)
		{
			var cols = i.Columns ?? new();
			if (cols.Count == 0) return "Choose at least one column";
			if (cols.Any(c => c == null || !Known(c.Key))) return "not_found";
			var fields = cols.Where(c => c.Key != FixedKey).Select(c => c.Key).ToList();   // a fixed column may repeat
			if (fields.Distinct().Count() != fields.Count) return "A column is chosen twice";
			if (cols.Any(c => c.Key == FixedKey && (c.Value ?? "").Trim().Length > 100)) return "A fixed value is 100 characters at most";
			if (cols.Any(c => string.IsNullOrWhiteSpace(c.Header) || c.Header.Trim().Length > 60)) return "Every column needs a header of at most 60 characters";
			if (Pack(cols.Select(c => c with { Header = c.Header.Trim() })).Length > 2000) return "The text is too long";
			if (string.IsNullOrEmpty(i.Delimiter) || !Delimiters.Contains(i.Delimiter)) return "Choose the separator";
			return string.IsNullOrEmpty(i.DateFormat) || !DateFormats.Contains(i.DateFormat) ? "Choose the date format" : null;
		}

		// ---- H229: the payer's own template ----------------------------------------------------------------------------
		// Names TPAs use for the same thing, beside each field's own Arabic and English names. Compared after folding case,
		// spaces and punctuation, so "Member No.", "member_no" and "MEMBER NO" are one heading.
		private static readonly (string Key, string[] Names)[] Synonyms =
		{
			("ClaimNo", new[] { "claim no", "claim number", "invoice no", "invoice number", "bill no", "bill number", "رقم الفاتورة" }),
			("MemberNo", new[] { "member no", "member id", "membership no", "card no", "card number", "insurance no", "policy no", "رقم البطاقة" }),
			("MemberClass", new[] { "class", "plan", "network", "category of member", "الفئة التأمينية" }),
			("PatientName", new[] { "patient name", "member name", "name", "insured name" }),
			("CivilId", new[] { "civil id", "national id", "cid", "id no", "emirates id", "iqama", "الرقم المدني" }),
			("FileNo", new[] { "file no", "mrn", "medical record no", "patient file", "رقم الملف" }),
			("BirthDate", new[] { "dob", "date of birth", "birth date", "تاريخ الميلاد" }),
			("Sex", new[] { "gender", "sex", "الجنس" }),
			("VisitDate", new[] { "service date", "date of service", "visit date", "treatment date", "dos", "encounter date", "تاريخ الزيارة" }),
			("VisitType", new[] { "visit type", "encounter type", "ip op", "patient type", "نوع الزيارة" }),
			("Diagnoses", new[] { "icd", "icd10", "icd 10", "diagnosis code", "diagnosis codes", "dx", "رمز التشخيص" }),
			("PrimaryDiagnosis", new[] { "diagnosis", "primary diagnosis", "principal diagnosis", "التشخيص" }),
			("ApprovalNo", new[] { "approval", "approval no", "pre auth", "preauth", "prior approval", "authorization", "authorization no", "auth no", "رقم الموافقة" }),
			("LineNo", new[] { "line", "line no", "item no", "seq", "serial", "s no", "م" }),
			("ServiceCode", new[] { "cpt", "service code", "item code", "activity code", "procedure code", "code", "رمز الخدمة" }),
			("ServiceName", new[] { "service", "service description", "description", "item", "activity", "procedure", "الخدمة" }),
			("Category", new[] { "category", "service type", "benefit", "الفئة" }),
			("Gross", new[] { "gross", "gross amount", "total", "amount", "charges", "price", "الإجمالي" }),
			("PatientShare", new[] { "copay", "co pay", "copayment", "deductible", "patient share", "patient amount", "حصة المريض" }),
			("Claimed", new[] { "net", "net amount", "claimed", "claimed amount", "payable", "insurer share", "company share", "المطالب به" }),
			("Currency", new[] { "currency", "العملة" }),
		};

		public static string Fold(string? s) =>
			new string((s ?? "").Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()).Replace("  ", " ").Trim()
				.Replace(" ", "");

		/// The field a heading most likely means; Fixed (an empty column the administrator fills or leaves) when none does.
		public static string Guess(string? header)
		{
			var h = Fold(header);
			if (h.Length == 0) return FixedKey;
			foreach (var f in Fields) if (Fold(f.Key) == h || Fold(f.En) == h || Fold(f.Ar) == h) return f.Key;
			foreach (var (key, names) in Synonyms) if (names.Any(n => Fold(n) == h)) return key;
			return FixedKey;
		}

		/// The headings of the payer's template, in its order, each with its guess; a field already guessed once is not
		/// guessed again (the second becomes Fixed for the administrator to decide). Empty: nothing readable.
		public static IReadOnlyList<HospitalClaimSheetTemplateColumn> Map(IEnumerable<string?> headers)
		{
			var taken = new HashSet<string>(); var outp = new List<HospitalClaimSheetTemplateColumn>();
			foreach (var raw in headers)
			{
				var h = (raw ?? "").Trim();
				if (h.Length > 60) h = h[..60];
				var k = Guess(h);
				if (k != FixedKey && !taken.Add(k)) k = FixedKey;
				outp.Add(new HospitalClaimSheetTemplateColumn(h, k));
			}
			while (outp.Count > 0 && outp[^1].Header.Length == 0) outp.RemoveAt(outp.Count - 1);
			return outp.Count > 60 ? outp.Take(60).ToList() : outp;
		}

		/// The heading row of a CSV template: its first non-empty line, split on the separator it uses most.
		public static (IReadOnlyList<string> Headers, string Delimiter) CsvHeaders(string text)
		{
			var line = text.TrimStart('\uFEFF').Replace("\r\n", "\n").Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
			var (sep, name) = new[] { ('\t', "TAB"), (';', ";"), (',', ",") }.OrderByDescending(d => line.Count(c => c == d.Item1)).First();
			var cells = new List<string>(); var cur = new StringBuilder(); bool q = false;
			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (q) { if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else if (c == '"') q = false; else cur.Append(c); }
				else if (c == '"') q = true;
				else if (c == sep) { cells.Add(cur.ToString()); cur.Clear(); }
				else cur.Append(c);
			}
			cells.Add(cur.ToString());
			return (cells.Select(x => x.Trim()).ToList(), name);
		}

		/// The heading row of an Excel template: the first row of its first sheet that has any text.
		public static IReadOnlyList<string> XlsxHeaders(Stream xlsx)
		{
			using var wb = new ClosedXML.Excel.XLWorkbook(xlsx);
			var ws = wb.Worksheets.FirstOrDefault();
			var row = ws?.RowsUsed().FirstOrDefault(r => r.CellsUsed().Any(c => c.GetString().Trim().Length > 0));
			if (row == null) return Array.Empty<string>();
			int last = row.LastCellUsed()!.Address.ColumnNumber;
			return Enumerable.Range(1, Math.Min(last, 60)).Select(i => row.Cell(i).GetString().Trim()).ToList();
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
			var numeric = Fields.ToDictionary(f => f.Key, f => f.Numeric);
			var sb = new StringBuilder();
			sb.Append(string.Join(sep, setup.Columns.Select(c => Cell(c.Header, false, sep)))).Append("\r\n");
			foreach (var row in Values(setup, batchNo, claims, currency))
				sb.Append(string.Join(sep, setup.Columns.Select((c, i) => Cell(row[i], numeric.GetValueOrDefault(c.Key), sep)))).Append("\r\n");
			return sb.ToString();
		}

		/// H223: the same sheet as a real Excel workbook — the payer's headers in bold, amounts as NUMBERS in the currency's
		/// decimals (a sheet the TPA sums without retyping), every other cell as text exactly as the CSV has it.
		public static byte[] RenderXlsx(HospitalClaimSheetSetup setup, string batchNo, IEnumerable<HospitalEClaimClaimData> claims, string currency)
		{
			var inv = CultureInfo.InvariantCulture;
			var numeric = Fields.ToDictionary(f => f.Key, f => f.Numeric);
			using var wb = new ClosedXML.Excel.XLWorkbook();
			var ws = wb.Worksheets.Add(batchNo.Length <= 31 ? batchNo : batchNo[..31]);
			for (int c = 0; c < setup.Columns.Count; c++) { var h = ws.Cell(1, c + 1); h.SetValue(setup.Columns[c].Header); h.Style.Font.Bold = true; }
			string fmt = "0." + new string('0', Decimals(currency));
			int r = 1;
			foreach (var row in Values(setup, batchNo, claims, currency))
			{
				r++;
				for (int c = 0; c < setup.Columns.Count; c++)
				{
					var cell = ws.Cell(r, c + 1); var v = row[c];
					if (numeric.GetValueOrDefault(setup.Columns[c].Key) && decimal.TryParse(v, NumberStyles.Number, inv, out var d))
					{ cell.SetValue(d); if (setup.Columns[c].Key != "LineNo") cell.Style.NumberFormat.Format = fmt; }
					else cell.SetValue(v ?? "");   // text stays text: a "=..." name is never a formula in a typed cell
				}
			}
			ws.SheetView.FreezeRows(1);
			ws.Columns().AdjustToContents(1, Math.Min(r, 200));
			using var ms = new MemoryStream();
			wb.SaveAs(ms);
			return ms.ToArray();
		}

		/// The sheet's values, one array per claim line in the payer's column order (text exactly as the CSV writes it).
		public static IEnumerable<string?[]> Values(HospitalClaimSheetSetup setup, string batchNo, IEnumerable<HospitalEClaimClaimData> claims, string currency)
		{
			var inv = CultureInfo.InvariantCulture;
			string money = "0." + new string('0', Decimals(currency));
			string Day(DateTime? d) => d?.ToString(setup.DateFormat, inv) ?? "";
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
					yield return setup.Columns.Select(c => c.Key == FixedKey ? (c.Value ?? "") : V(c.Key)).ToArray();
				}
			}
		}
	}
}
