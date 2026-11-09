using System.Globalization;
using System.Text;
using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR8 — THE SALARY TRANSFER FILE FOR THE BANK (wage protection), 2026-11-09.
	//
	// Built from the month's POSTED payslips only (what was paid is what was booked), one line per employee with a
	// positive net. Its SHAPE is the company's HrBankFileFormat: which columns, in what order, under what headers,
	// separated by what, dates and decimals written how, with or without a count/total trailer, and the file's name.
	// Every bank's template differs; matching a new one is a settings change, never code.
	//
	// Before the file is produced, every line is checked: an employee with no IBAN, an IBAN that is not an IBAN, or no
	// civil ID is reported as a PROBLEM and listed on the screen — the bank would reject or misroute those lines.
	// ==========================================================================================
	public sealed class HrBankFileColumn
	{
		public string Field { get; set; } = "";
		public string Header { get; set; } = "";
		public string? Value { get; set; }   // the text of a Constant column
	}

	/// One line of an export file. Net is the line's AMOUNT — what the trailer totals (the transfer for the bank, the total
	/// contribution for social insurance). Amounts carries any further money fields a file kind offers, by field key.
	public sealed record HrBankFileLine(int Seq, int EmployeeId, string NameAr, string? NameEn, string? CivilId, string? Iban, string? BankName,
		decimal Basic, decimal Allowances, decimal Gross, decimal Deductions, decimal Net, string? Problem,
		IReadOnlyDictionary<string, decimal>? Amounts = null, string? Nationality = null);

	public sealed record HrBankFileResult(byte[] Content, string FileName, int Lines, decimal Total, int Problems);

	public static class HrBankFileFields
	{
		public const string Bank = "Bank";
		public const string Insurance = "Insurance";
		public static string Norm(string? kind) => string.Equals(kind, Insurance, StringComparison.OrdinalIgnoreCase) ? Insurance : Bank;

		/// The social-insurance file's catalogue (the monthly contributions file, e.g. Kuwait PIFSS).
		public static readonly (string Key, string Ar, string En)[] InsuranceFields =
		{
			("Seq", "مسلسل", "Sequence"), ("EmployeeId", "رقم الموظف", "Employee no."), ("NameAr", "الاسم بالعربي", "Name (Arabic)"),
			("NameEn", "الاسم بالإنجليزي", "Name (English)"), ("CivilId", "الرقم المدني", "Civil ID"), ("Nationality", "مواطن / وافد", "National / expatriate"),
			("Wage", "الأجر الخاضع", "Insurable wage"), ("EmployeeShare", "حصة الموظف", "Employee share"), ("EmployerShare", "حصة صاحب العمل", "Employer share"),
			("TotalShare", "إجمالي الاشتراك", "Total contribution"), ("Currency", "العملة", "Currency"), ("Month", "شهر الاشتراك", "Contribution month"),
			("PaymentDate", "تاريخ السداد", "Payment date"), ("EmployerCode", "رقم صاحب العمل", "Employer code"), ("Constant", "نص ثابت", "Fixed text"),
		};

		public static (string Key, string Ar, string En)[] For(string? kind) => Norm(kind) == Insurance ? InsuranceFields : All;
		public static bool Known(string? kind, string key) => For(kind).Any(f => f.Key == key);

		/// The catalogue the bank file's column editor offers: key, Arabic, English.
		public static readonly (string Key, string Ar, string En)[] All =
		{
			("Seq", "مسلسل", "Sequence"), ("EmployeeId", "رقم الموظف", "Employee no."), ("NameAr", "الاسم بالعربي", "Name (Arabic)"),
			("NameEn", "الاسم بالإنجليزي", "Name (English)"), ("CivilId", "الرقم المدني", "Civil ID"), ("Iban", "IBAN", "IBAN"),
			("BankName", "بنك الموظف", "Employee bank"), ("Net", "صافي الراتب", "Net salary"), ("Basic", "الأساسي", "Basic"),
			("Allowances", "البدلات", "Allowances"), ("Gross", "الإجمالي", "Gross"), ("Deductions", "الاستقطاعات", "Deductions"),
			("Currency", "العملة", "Currency"), ("Month", "شهر الراتب", "Salary month"), ("PaymentDate", "تاريخ الدفع", "Payment date"),
			("CompanyAccount", "حساب الشركة", "Company account"), ("EmployerCode", "رقم صاحب العمل", "Employer code"),
			("Constant", "نص ثابت", "Fixed text"),
		};

		public static bool Known(string key) => All.Any(f => f.Key == key);
	}

	public sealed class HrBankFileService
	{
		private readonly CrossDbContext _db;
		public HrBankFileService(CrossDbContext db) { _db = db; }

		public static List<HrBankFileColumn> DefaultColumns(string? kind) => HrBankFileFields.Norm(kind) == HrBankFileFields.Insurance
			? new()
			{
				new() { Field = "Seq", Header = "SEQ" }, new() { Field = "CivilId", Header = "CIVIL_ID" }, new() { Field = "NameAr", Header = "NAME" },
				new() { Field = "Wage", Header = "WAGE" }, new() { Field = "EmployeeShare", Header = "EMPLOYEE_SHARE" }, new() { Field = "EmployerShare", Header = "EMPLOYER_SHARE" },
				new() { Field = "TotalShare", Header = "TOTAL" }, new() { Field = "Month", Header = "MONTH" },
			}
			: DefaultColumns();

		public static List<HrBankFileColumn> DefaultColumns() => new()
		{
			new() { Field = "Seq", Header = "SEQ" }, new() { Field = "CivilId", Header = "CIVIL_ID" }, new() { Field = "NameEn", Header = "NAME" },
			new() { Field = "Iban", Header = "IBAN" }, new() { Field = "BankName", Header = "BANK" }, new() { Field = "Net", Header = "AMOUNT" },
			new() { Field = "Currency", Header = "CURRENCY" }, new() { Field = "PaymentDate", Header = "VALUE_DATE" },
		};

		public static List<HrBankFileColumn> Columns(HrBankFileFormat f)
		{
			try { return JsonSerializer.Deserialize<List<HrBankFileColumn>>(f.ColumnsJson) ?? new(); } catch (JsonException) { return new(); }
		}

		/// The company's format for a file kind, or that kind's default (not saved) when it has none yet.
		public async Task<(HrBankFileFormat Format, bool Saved)> FormatAsync(int companyId, CancellationToken ct = default) => await FormatAsync(companyId, HrBankFileFields.Bank, ct);

		public async Task<(HrBankFileFormat Format, bool Saved)> FormatAsync(int companyId, string? kind, CancellationToken ct = default)
		{
			var k = HrBankFileFields.Norm(kind);
			var f = await _db.HrBankFileFormats.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyID == companyId && x.Kind == k, ct);
			return f != null ? (f, true) : (new HrBankFileFormat
			{
				CompanyID = companyId, Kind = k, ColumnsJson = JsonSerializer.Serialize(DefaultColumns(k)),
				FileNamePattern = k == HrBankFileFields.Insurance ? "INSURANCE_{yyyyMM}.csv" : "SALARIES_{yyyyMM}.csv",
			}, false);
		}

		public async Task<(bool Ok, string? Ar, string? En)> SaveAsync(int companyId, HrBankFileFormat input, List<HrBankFileColumn> columns, int? by, CancellationToken ct = default)
		{
			var kind = HrBankFileFields.Norm(input.Kind);
			columns = columns.Where(c => !string.IsNullOrWhiteSpace(c.Field)).ToList();
			if (columns.Count == 0) return (false, "أضف عموداً واحداً على الأقل", "Add at least one column");
			if (columns.Any(c => !HrBankFileFields.Known(kind, c.Field))) return (false, "حقل غير معروف في الأعمدة", "Unknown field in the columns");
			if (kind == HrBankFileFields.Bank && !columns.Any(c => c.Field == "Net")) return (false, "الملف يحتاج عمود صافي الراتب", "The file needs the net salary column");
			if (kind == HrBankFileFields.Insurance && !columns.Any(c => c.Field is "EmployeeShare" or "EmployerShare" or "TotalShare"))
				return (false, "الملف يحتاج عموداً للاشتراك", "The file needs a contribution column");
			if (input.Delimiter is not ("," or ";" or "\t" or "|")) return (false, "الفاصل غير مدعوم", "Unsupported delimiter");
			if (input.DecimalPlaces is < 0 or > 3 || input.PaymentDay is < 0 or > 31) return (false, "القيم خارج النطاق", "Values are out of range");
			if (input.TrailerRow is not ("None" or "CountTotal")) input.TrailerRow = "None";
			try { _ = new DateTime(2026, 1, 31).ToString(input.DateFormat, CultureInfo.InvariantCulture); }
			catch (FormatException) { return (false, "صيغة التاريخ غير صحيحة", "Invalid date format"); }
			if (string.IsNullOrWhiteSpace(input.FileNamePattern) || input.FileNamePattern.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '{' && c != '}').ToArray()) >= 0)
				return (false, "اسم الملف غير صالح", "Invalid file name");

			var row = await _db.HrBankFileFormats.FirstOrDefaultAsync(x => x.CompanyID == companyId && x.Kind == kind, ct);
			if (row == null) { row = new HrBankFileFormat { CompanyID = companyId, Kind = kind }; _db.HrBankFileFormats.Add(row); }
			row.BankName = Clean(input.BankName); row.Delimiter = input.Delimiter; row.WithBom = input.WithBom; row.HeaderRow = input.HeaderRow;
			row.DateFormat = input.DateFormat.Trim(); row.DecimalPlaces = input.DecimalPlaces; row.TrailerRow = input.TrailerRow;
			row.FileNamePattern = input.FileNamePattern.Trim(); row.CompanyAccount = Clean(input.CompanyAccount); row.EmployerCode = Clean(input.EmployerCode);
			row.PaymentDay = input.PaymentDay; row.ColumnsJson = JsonSerializer.Serialize(columns);
			row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeID = by;
			await _db.SaveChangesAsync(ct);
			return (true, null, null);
		}

		/// The month's lines from the POSTED payslips, each checked for what the bank needs.
		public async Task<List<HrBankFileLine>> LinesAsync(int companyId, int year, int month, CancellationToken ct = default)
		{
			var slips = await _db.Payslips.AsNoTracking().Where(p => p.CompanyID == companyId && p.Year == year && p.Month == month && p.Net > 0)
				.Join(_db.Employee.AsNoTracking(), p => p.EmployeeID, e => e.ID, (p, e) => new
				{
					p.EmployeeID, e.FullName, e.FullNameEn, e.CivilIdNumber, e.Iban, e.BankName,
					p.BaseSalary, p.Allowances, p.GrossEarnings, p.Net,
					Ded = p.LatePenalty + p.AbsencePenalty + p.SiEmployee + p.Tax + p.LoanDeduction + p.SickLeaveDeduction,
				})
				.OrderBy(x => x.FullName).ToListAsync(ct);
			int seq = 0;
			return slips.Select(s =>
			{
				string? problem = string.IsNullOrWhiteSpace(s.Iban) ? "NoIban" : !LooksLikeIban(s.Iban) ? "BadIban" : string.IsNullOrWhiteSpace(s.CivilIdNumber) ? "NoCivilId" : null;
				return new HrBankFileLine(++seq, s.EmployeeID, s.FullName, s.FullNameEn, s.CivilIdNumber, s.Iban, s.BankName,
					s.BaseSalary, s.Allowances, s.GrossEarnings, s.Ded, s.Net, problem);
			}).ToList();
		}

		/// IBAN shape and its ISO 7064 mod-97 check digits.
		public static bool LooksLikeIban(string? iban)
		{
			var s = (iban ?? "").Replace(" ", "").ToUpperInvariant();
			if (s.Length is < 15 or > 34 || !char.IsLetter(s[0]) || !char.IsLetter(s[1]) || !s.All(char.IsLetterOrDigit)) return false;
			var r = s[4..] + s[..4];
			int mod = 0;
			foreach (var ch in r)
			{
				var v = char.IsDigit(ch) ? (ch - '0').ToString() : (ch - 'A' + 10).ToString();
				foreach (var d in v) mod = (mod * 10 + (d - '0')) % 97;
			}
			return mod == 1;
		}

		public static string FileName(HrBankFileFormat f, int year, int month) =>
			f.FileNamePattern.Replace("{yyyyMM}", $"{year:D4}{month:D2}").Replace("{yyyy}", $"{year:D4}").Replace("{MM}", $"{month:D2}");

		public static DateTime PaymentDate(HrBankFileFormat f, int year, int month)
		{
			int last = DateTime.DaysInMonth(year, month);
			return new DateTime(year, month, f.PaymentDay is > 0 ? Math.Min(f.PaymentDay, last) : last);
		}

		/// The file itself. Lines with a problem are NOT written (the caller shows them); the trailer counts what was written.
		public static HrBankFileResult Build(HrBankFileFormat f, IReadOnlyList<HrBankFileLine> lines, int year, int month, string currency)
		{
			var cols = Columns(f);
			var inv = CultureInfo.InvariantCulture;
			var pay = PaymentDate(f, year, month);
			string amountFmt = "0." + new string('0', Math.Max(0, f.DecimalPlaces));
			if (f.DecimalPlaces == 0) amountFmt = "0";
			string Money(decimal v) => Math.Round(v, f.DecimalPlaces, MidpointRounding.AwayFromZero).ToString(amountFmt, inv);
			string Esc(string? v)
			{
				v ??= "";
				return v.IndexOfAny((f.Delimiter + "\"\r\n").ToCharArray()) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
			}
			string Cell(HrBankFileColumn c, HrBankFileLine l) => c.Field switch
			{
				"Seq" => l.Seq.ToString(inv), "EmployeeId" => l.EmployeeId.ToString(inv), "NameAr" => l.NameAr, "NameEn" => l.NameEn ?? l.NameAr,
				"CivilId" => l.CivilId ?? "", "Iban" => (l.Iban ?? "").Replace(" ", "").ToUpperInvariant(), "BankName" => l.BankName ?? "",
				"Net" => Money(l.Net), "Basic" => Money(l.Basic), "Allowances" => Money(l.Allowances), "Gross" => Money(l.Gross), "Deductions" => Money(l.Deductions),
				"Currency" => currency, "Month" => $"{year:D4}{month:D2}", "PaymentDate" => pay.ToString(f.DateFormat, inv),
				"CompanyAccount" => f.CompanyAccount ?? "", "EmployerCode" => f.EmployerCode ?? "", "Constant" => c.Value ?? "",
				"Nationality" => l.Nationality ?? "",
				_ when l.Amounts != null && l.Amounts.TryGetValue(c.Field, out var amount) => Money(amount),
				_ => "",
			};

			// renumbered: the file's sequence runs 1..n over the lines it carries (a left-out employee leaves no gap)
			var good = lines.Where(l => l.Problem == null).Select((l, i) => l with { Seq = i + 1 }).ToList();
			var sb = new StringBuilder();
			if (f.HeaderRow) sb.Append(string.Join(f.Delimiter, cols.Select(c => Esc(c.Header)))).Append("\r\n");
			foreach (var l in good) sb.Append(string.Join(f.Delimiter, cols.Select(c => Esc(Cell(c, l))))).Append("\r\n");
			var total = good.Sum(l => Math.Round(l.Net, f.DecimalPlaces, MidpointRounding.AwayFromZero));
			if (f.TrailerRow == "CountTotal") sb.Append(string.Join(f.Delimiter, new[] { "TOTAL", good.Count.ToString(inv), Money(total) })).Append("\r\n");
			var body = Encoding.UTF8.GetBytes(sb.ToString());
			var content = f.WithBom ? Encoding.UTF8.GetPreamble().Concat(body).ToArray() : body;
			return new HrBankFileResult(content, FileName(f, year, month), good.Count, total, lines.Count - good.Count);
		}

		private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
	}
}
