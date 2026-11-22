using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H210 — the payer's claim sheet. What must never happen: a sheet in another column order or with other
	// headers than the payer's, a name run as a formula, a comma in a name splitting a cell, KWD written with two decimals,
	// a column chosen twice or an unknown one saved, or a claim's line missing from the sheet.
	// ==========================================================================================
	public class HospitalClaimSheetTests
	{
		private static HospitalEClaimClaimData Claim(string name, string nameEn, params (string Code, decimal Amount, decimal Insurer)[] lines) => new(
			new HospitalClaim { ID = 1, Gross = lines.Sum(l => l.Amount) }, "BILL-000101", new DateTime(2026, 11, 18), false,
			new HospitalPatient { Name = name, NameEn = nameEn, Mrn = "MRN-100023", NationalId = "290010112345", Sex = HospitalPatientSex.Female, BirthDate = new DateTime(1990, 1, 1) },
			new HospitalPatientPolicy { PolicyNo = "GIG-5521", MemberClass = "A" },
			lines.Select((l, i) => new HospitalClaimLine { ID = i + 1, Code = l.Code, Description = "خدمة", DescriptionEn = "Service, general", Category = "Lab", Amount = l.Amount, InsurerAmount = l.Insurer }).ToList(),
			new[] { new HospitalEncounterDiagnosis { Code = "J18.9", Name = "التهاب رئوي", NameEn = "Pneumonia", IsPrimary = true }, new HospitalEncounterDiagnosis { Code = "E11.9", Name = "سكري", NameEn = "Diabetes", SortOrder = 1 } },
			null, new[] { "APP-77" });

		[Fact]
		public void The_sheet_follows_the_payers_columns_headers_separator_and_date_format()
		{
			var setup = new HospitalClaimSheetSetup(new[] { new HospitalClaimSheetColumn("MemberNo", "Card No"), new HospitalClaimSheetColumn("VisitDate", "DOS"),
				new HospitalClaimSheetColumn("ServiceCode", "CPT"), new HospitalClaimSheetColumn("Claimed", "Net"), new HospitalClaimSheetColumn("Diagnoses", "ICD") }, ";", "dd/MM/yyyy", true);
			var csv = HospitalClaimSheets.Render(setup, "CB-1", new[] { Claim("سارة", "Sara", ("85025", 12.5m, 10m), ("71045", 30m, 24m)) }, "KWD");
			var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
			Assert.Equal(3, rows.Length);   // the header and one row per claim line
			Assert.Equal("Card No;DOS;CPT;Net;ICD", rows[0]);
			Assert.Equal("GIG-5521;18/11/2026;85025;10.000;J18.9;E11.9", rows[1].Replace("\"", ""));
			Assert.EndsWith("24.000;\"J18.9;E11.9\"", rows[2]);   // the separator inside a cell is quoted
		}

		[Fact]
		public void A_name_is_never_a_formula_and_a_separator_inside_a_cell_is_quoted()
		{
			Assert.Equal("'=HYPERLINK(1)", HospitalClaimSheets.Cell("=HYPERLINK(1)", false, ','));
			Assert.Equal("'@SUM", HospitalClaimSheets.Cell("@SUM", false, ','));
			Assert.Equal("\"Service, general\"", HospitalClaimSheets.Cell("Service, general", false, ','));
			Assert.Equal("\"say \"\"ah\"\"\"", HospitalClaimSheets.Cell("say \"ah\"", false, ','));
			Assert.Equal("-1.500", HospitalClaimSheets.Cell("-1.500", true, ','));   // a number stays a number
			Assert.Equal("Service, general", HospitalClaimSheets.Cell("Service, general", false, ';'));
		}

		[Fact]
		public void The_standard_sheet_has_every_line_with_both_shares_in_the_currencys_decimals()
		{
			var csv = HospitalClaimSheets.Render(HospitalClaimSheets.Standard(), "CB-2", new[] { Claim("=cmd", "", ("A1", 10m, 8m)) }, "KWD");
			var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
			Assert.Equal(2, rows.Length);
			Assert.Contains("'=cmd", rows[1]);   // the English name is empty, so the Arabic one is used — made inert
			Assert.Contains("10.000,2.000,8.000,KWD", rows[1]);
			Assert.Contains("2.00", HospitalClaimSheets.Render(HospitalClaimSheets.Standard(), "CB-2", new[] { Claim("x", "x", ("A1", 10m, 8m)) }, "USD"));
			Assert.Equal(3, HospitalClaimSheets.Decimals("KWD"));
			Assert.Equal(2, HospitalClaimSheets.Decimals("SAR"));
		}

		[Fact]
		public void A_sheet_is_saved_only_with_known_columns_once_each_with_headers_a_separator_and_a_date_format()
		{
			HospitalClaimSheetInput I(params (string K, string H)[] c) => new() { Columns = c.Select(x => new HospitalClaimSheetColumn(x.K, x.H)).ToList(), Delimiter = ",", DateFormat = "dd/MM/yyyy" };
			Assert.Null(HospitalClaimSheets.Refusal(I(("ClaimNo", "Claim"), ("Claimed", "Amount"))));
			Assert.Equal("Choose at least one column", HospitalClaimSheets.Refusal(I()));
			Assert.Equal("not_found", HospitalClaimSheets.Refusal(I(("Salary", "x"))));
			Assert.Equal("A column is chosen twice", HospitalClaimSheets.Refusal(I(("ClaimNo", "a"), ("ClaimNo", "b"))));
			Assert.NotNull(HospitalClaimSheets.Refusal(I(("ClaimNo", " "))));
			var bad = I(("ClaimNo", "a")); bad.Delimiter = "|";
			Assert.Equal("Choose the separator", HospitalClaimSheets.Refusal(bad));
			bad = I(("ClaimNo", "a")); bad.DateFormat = "yy";
			Assert.Equal("Choose the date format", HospitalClaimSheets.Refusal(bad));
		}

		[Fact]
		public void The_columns_are_kept_in_order_with_their_headers()
		{
			var cols = new[] { new HospitalClaimSheetColumn("Claimed", "صافي المطالبة"), new HospitalClaimSheetColumn("ClaimNo", "Claim | No = 1") };
			var back = HospitalClaimSheets.Unpack(HospitalClaimSheets.Pack(cols));
			Assert.Equal(new[] { "Claimed", "ClaimNo" }, back.Select(c => c.Key));
			Assert.Equal("صافي المطالبة", back[0].Header);
			Assert.Equal("Claim / No - 1", back[1].Header);   // the packing characters cannot break a header
			Assert.Empty(HospitalClaimSheets.Unpack("Nope=x"));
		}

		[Fact]
		public void The_Excel_sheet_has_the_payers_headers_and_amounts_as_numbers()
		{
			var setup = new HospitalClaimSheetSetup(new[] { new HospitalClaimSheetColumn("PatientName", "Member Name"), new HospitalClaimSheetColumn("ServiceCode", "CPT"),
				new HospitalClaimSheetColumn("Claimed", "Net") }, ",", "dd/MM/yyyy", true);
			var bytes = HospitalClaimSheets.RenderXlsx(setup, "CB-9", new[] { Claim("=cmd", "", ("85025", 12.5m, 10m)) }, "KWD");
			using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(bytes));
			var ws = wb.Worksheet(1);
			Assert.Equal("Member Name", ws.Cell(1, 1).GetString());
			Assert.True(ws.Cell(1, 1).Style.Font.Bold);
			Assert.Equal("=cmd", ws.Cell(2, 1).GetString());   // a name is text, never a formula
			Assert.False(ws.Cell(2, 1).HasFormula);
			Assert.Equal(10m, ws.Cell(2, 3).GetValue<decimal>());   // an amount is a number the TPA can sum
			Assert.Equal("0.000", ws.Cell(2, 3).Style.NumberFormat.Format);
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalClaimSheetModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h210_claim_sheets.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalPayerClaimSheet), "HospitalPayerClaimSheets");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalPayerClaimSheet), "HospitalPayerClaimSheets");
			foreach (var d in HospitalClaimSheets.Delimiters) Assert.Contains("'" + d + "'", s);
		}

		// H229: a fixed column (the hospital's code at the payer, or kept empty) may repeat, keeps its value through saving,
		// and lands on every row; a too-long value is refused.
		[Fact]
		public void A_fixed_column_repeats_keeps_its_value_and_is_on_every_row()
		{
			var cols = new[] { new HospitalClaimSheetColumn("Fixed", "Provider", "KW-HOSP-0091"), new HospitalClaimSheetColumn("ClaimNo", "Invoice"),
				new HospitalClaimSheetColumn("Fixed", "Remarks", ""), new HospitalClaimSheetColumn("Claimed", "Net") };
			Assert.Null(HospitalClaimSheets.Refusal(new HospitalClaimSheetInput { Columns = cols.ToList(), Delimiter = ",", DateFormat = "yyyy-MM-dd" }));
			var back = HospitalClaimSheets.Unpack(HospitalClaimSheets.Pack(cols));
			Assert.Equal(cols, back);
			var csv = HospitalClaimSheets.Render(new HospitalClaimSheetSetup(back, ",", "yyyy-MM-dd", true), "CB-1", new[] { Claim("سارة", "Sara", ("85025", 12.5m, 10m), ("71045", 30m, 24m)) }, "KWD");
			var rows = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
			Assert.Equal(new[] { "Provider,Invoice,Remarks,Net", "KW-HOSP-0091,BILL-000101,,10.000", "KW-HOSP-0091,BILL-000101,,24.000" }, rows);
			Assert.Equal("A fixed value is 100 characters at most", HospitalClaimSheets.Refusal(new HospitalClaimSheetInput
				{ Columns = new() { new("Fixed", "X", new string('9', 101)) }, Delimiter = ",", DateFormat = "yyyy-MM-dd" }));
			Assert.Equal("A column is chosen twice", HospitalClaimSheets.Refusal(new HospitalClaimSheetInput
				{ Columns = new() { new("ClaimNo", "A"), new("ClaimNo", "B") }, Delimiter = ",", DateFormat = "yyyy-MM-dd" }));
		}

		[Fact]
		public void A_payers_headings_are_matched_to_the_fields_they_mean()
		{
			var m = HospitalClaimSheets.Map(new[] { "S.No", "Card No.", "Patient Name", "CIVIL ID", "DOS", "ICD-10", "CPT", "Description", "Gross Amount", "Co-Pay", "Net Amount", "Provider Code", "Invoice No", "Card Number", "", "" });
			Assert.Equal(new[] { "LineNo", "MemberNo", "PatientName", "CivilId", "VisitDate", "Diagnoses", "ServiceCode", "ServiceName", "Gross", "PatientShare", "Claimed", "Fixed", "ClaimNo", "Fixed" }, m.Select(x => x.Key));
			Assert.Equal("Card Number", m[13].Header);   // a second "card" is not guessed again: the administrator decides
			Assert.Equal("Fixed", HospitalClaimSheets.Guess("رمز مقدم الخدمة"));
			Assert.Equal("CivilId", HospitalClaimSheets.Guess("الرقم المدني"));
		}

		[Fact]
		public void The_headings_are_read_from_a_csv_or_an_excel_template()
		{
			var (h, d) = HospitalClaimSheets.CsvHeaders("\uFEFF\r\nMember No;\"Name; full\";DOS\r\n1;2;3");
			Assert.Equal(new[] { "Member No", "Name; full", "DOS" }, h);
			Assert.Equal(";", d);
			Assert.Equal("TAB", HospitalClaimSheets.CsvHeaders("A\tB\tC").Delimiter);
			using var wb = new ClosedXML.Excel.XLWorkbook();
			var ws = wb.Worksheets.Add("Claims");
			ws.Cell(3, 1).SetValue("Card No"); ws.Cell(3, 2).SetValue("CPT"); ws.Cell(3, 4).SetValue("Net");   // a title-less sheet whose headings start lower, one blank between
			using var ms = new MemoryStream(); wb.SaveAs(ms); ms.Position = 0;
			Assert.Equal(new[] { "Card No", "CPT", "", "Net" }, HospitalClaimSheets.XlsxHeaders(ms));
		}
	}
}
