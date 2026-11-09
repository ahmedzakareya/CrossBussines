using ClosedXML.Excel;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// BANK DETAILS FROM EXCEL (2026-11-09) — civil ID, IBAN and bank for many employees at once, so the bank salary file
	// (HR8) has what it needs.
	//
	// Three steps, nothing stored in between: the template (every active employee with today's values), the upload
	// (parsed and checked, shown as a review, NOTHING written), and the confirm (the reviewed changes are checked AGAIN
	// on the server — company, IBAN check digits, duplicates — and only then written). The confirm never trusts the
	// review page: a value that no longer passes is refused there and then.
	//
	// A blank cell keeps what the employee has; the import never clears a value. Every change is written to the
	// employee's effective-dated history (EmployeeHistory), an IBAN masked to its last four characters.
	// ==========================================================================================
	public sealed record BankDetailsChange(int EmployeeId, string? CivilId, string? Iban, string? BankName);

	public sealed record BankDetailsRow(int RowNo, int EmployeeId, string? Name, string? OldCivilId, string? NewCivilId, string? OldIban, string? NewIban,
		string? OldBank, string? NewBank, string Status, string? ErrorAr, string? ErrorEn)
	{
		public bool IsChange => Status == "Change";
		public bool IsError => Status == "Error";
	}

	public sealed class HrBankDetailsImport
	{
		public const int MaxRows = 5000;
		private readonly CrossDbContext _db;
		public HrBankDetailsImport(CrossDbContext db) { _db = db; }

		public static string? NormIban(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();
		public static string? NormCivil(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Replace(" ", "").Replace("-", "").Trim();
		public static string? NormText(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
		public static string Mask(string? iban) => string.IsNullOrEmpty(iban) ? "—" : (iban.Length <= 4 ? iban : "…" + iban[^4..]);

		/// The template: every active employee of the company, with the values they have now.
		public async Task<byte[]> TemplateAsync(int companyId, bool arabic, CancellationToken ct = default)
		{
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId && e.IsActive).OrderBy(e => e.FullName)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.CivilIdNumber, e.Iban, e.BankName }).ToListAsync(ct);
			using var wb = new XLWorkbook();
			var ws = wb.AddWorksheet(arabic ? "بيانات البنك" : "Bank details");
			if (arabic) ws.RightToLeft = true;
			string[] heads = arabic
				? new[] { "رقم الموظف", "الاسم", "الرقم المدني", "IBAN", "البنك" }
				: new[] { "Employee no.", "Name", "Civil ID", "IBAN", "Bank" };
			for (int c = 0; c < heads.Length; c++) ws.Cell(1, c + 1).Value = heads[c];
			ws.Row(1).Style.Font.Bold = true;
			ws.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#E7F0FF");
			int r = 2;
			foreach (var e in emps)
			{
				ws.Cell(r, 1).Value = e.ID;
				ws.Cell(r, 2).Value = arabic ? e.FullName : (e.FullNameEn ?? e.FullName);
				ws.Cell(r, 3).Value = e.CivilIdNumber ?? ""; ws.Cell(r, 3).Style.NumberFormat.Format = "@";
				ws.Cell(r, 4).Value = e.Iban ?? ""; ws.Cell(r, 4).Style.NumberFormat.Format = "@";
				ws.Cell(r, 5).Value = e.BankName ?? "";
				r++;
			}
			ws.Column(3).Style.NumberFormat.Format = "@"; ws.Column(4).Style.NumberFormat.Format = "@";   // a civil ID or IBAN is text, never a number
			ws.Column(2).Style.Font.FontColor = XLColor.FromHtml("#6B7280");                                // the name is for reading; it is not imported
			ws.Columns().AdjustToContents();
			ws.SheetView.FreezeRows(1);
			using var ms = new MemoryStream();
			wb.SaveAs(ms);
			return ms.ToArray();
		}

		/// The rows of an uploaded workbook: the first sheet, the header on row 1, the columns by position (no., name,
		/// civil ID, IBAN, bank). Returns null when the file is not a readable workbook.
		public static List<(int RowNo, string? Id, string? Civil, string? Iban, string? Bank)>? Parse(Stream file)
		{
			try
			{
				using var wb = new XLWorkbook(file);
				var ws = wb.Worksheets.First();
				var rows = new List<(int, string?, string?, string?, string?)>();
				foreach (var row in ws.RowsUsed().Skip(1).Take(MaxRows))
				{
					string? Cell(int c) { var v = row.Cell(c).GetFormattedString(); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
					if (Cell(1) == null && Cell(3) == null && Cell(4) == null && Cell(5) == null) continue;
					rows.Add((row.RowNumber(), Cell(1), Cell(3), Cell(4), Cell(5)));
				}
				return rows;
			}
			catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
		}

		/// Check every row against the company's employees. Nothing is written.
		public async Task<List<BankDetailsRow>> ReviewAsync(int companyId, IReadOnlyList<(int RowNo, string? Id, string? Civil, string? Iban, string? Bank)> input, CancellationToken ct = default)
		{
			var emps = await _db.Employee.AsNoTracking().Where(e => e.EmpCompanyID == companyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.IsActive, e.CivilIdNumber, e.Iban, e.BankName }).ToListAsync(ct);
			var byId = emps.ToDictionary(e => e.ID);
			var seenIds = new HashSet<int>();
			var result = new List<BankDetailsRow>();
			// the values each employee will END with, to find duplicates across the file AND the people not in it
			var finalCivil = emps.Where(e => e.CivilIdNumber != null).ToDictionary(e => e.ID, e => NormCivil(e.CivilIdNumber)!);
			var finalIban = emps.Where(e => e.Iban != null).ToDictionary(e => e.ID, e => NormIban(e.Iban)!);
			var parsed = new List<(int RowNo, int Id, string? Civil, string? Iban, string? Bank, string? ErrAr, string? ErrEn)>();
			foreach (var r in input)
			{
				string? errAr = null, errEn = null;
				int id = 0;
				if (!int.TryParse(r.Id, out id) || !byId.TryGetValue(id, out var emp)) { errAr = "رقم الموظف غير موجود في الشركة"; errEn = "Employee no. not found in the company"; }
				else if (!emp.IsActive) { errAr = "الموظف غير نشط"; errEn = "The employee is not active"; }
				else if (!seenIds.Add(id)) { errAr = "الموظف مكرر في الملف"; errEn = "The employee appears twice in the file"; }
				var civil = NormCivil(r.Civil); var iban = NormIban(r.Iban); var bank = NormText(r.Bank);
				if (errAr == null && civil != null && (civil.Length is < 5 or > 20 || !civil.All(char.IsLetterOrDigit))) { errAr = "الرقم المدني غير صالح"; errEn = "Invalid civil ID"; }
				if (errAr == null && iban != null && !HrBankFileService.LooksLikeIban(iban)) { errAr = "IBAN غير صحيح (رقم التحقق لا يطابق)"; errEn = "Invalid IBAN (check digits do not match)"; }
				if (errAr == null) { if (civil != null) finalCivil[id] = civil; if (iban != null) finalIban[id] = iban; }
				parsed.Add((r.RowNo, id, civil, iban, bank, errAr, errEn));
			}
			foreach (var p in parsed)
			{
				string? errAr = p.ErrAr, errEn = p.ErrEn;
				if (errAr == null && p.Civil != null && finalCivil.Count(kv => kv.Value == p.Civil) > 1) { errAr = "الرقم المدني مستخدم لموظف آخر"; errEn = "The civil ID belongs to another employee"; }
				if (errAr == null && p.Iban != null && finalIban.Count(kv => kv.Value == p.Iban) > 1) { errAr = "نفس الـ IBAN لموظف آخر"; errEn = "The same IBAN is on another employee"; }
				byId.TryGetValue(p.Id, out var e);
				var name = e == null ? null : EmployeeNames.Of(e.FullName, e.FullNameEn);
				bool changes = e != null && ((p.Civil != null && p.Civil != NormCivil(e.CivilIdNumber)) || (p.Iban != null && p.Iban != NormIban(e.Iban)) || (p.Bank != null && p.Bank != e.BankName));
				result.Add(new BankDetailsRow(p.RowNo, p.Id, name, e?.CivilIdNumber, p.Civil, e?.Iban, p.Iban, e?.BankName, p.Bank,
					errAr != null ? "Error" : changes ? "Change" : "Same", errAr, errEn));
			}
			return result.OrderBy(r => r.Status == "Error" ? 0 : r.Status == "Change" ? 1 : 2).ThenBy(r => r.RowNo).ToList();
		}

		/// Write the reviewed changes — checked again here, never trusted from the page.
		public async Task<(int Applied, int Refused)> ApplyAsync(int companyId, IReadOnlyList<BankDetailsChange> changes, int? byEmployeeId, CancellationToken ct = default)
		{
			var review = await ReviewAsync(companyId, changes.Select((c, i) => (i + 2, (string?)c.EmployeeId.ToString(), c.CivilId, c.Iban, c.BankName)).ToList(), ct);
			var ok = review.Where(r => r.IsChange).ToList();
			var ids = ok.Select(r => r.EmployeeId).ToList();
			var emps = await _db.Employee.Where(e => ids.Contains(e.ID) && e.EmpCompanyID == companyId).ToDictionaryAsync(e => e.ID, ct);
			var today = DateTime.Today;
			void Track(Employee e, string field, string? oldV, string? newV) => _db.EmployeeHistory.Add(new EmployeeHistory
			{
				CompanyID = companyId, EmployeeID = e.ID, Field = field, OldValue = oldV, NewValue = newV, EffectiveFrom = today,
				ChangedByEmployeeId = byEmployeeId, ChangedAt = DateTime.UtcNow,
			});
			foreach (var r in ok)
			{
				if (!emps.TryGetValue(r.EmployeeId, out var e)) continue;
				if (r.NewCivilId != null && r.NewCivilId != NormCivil(e.CivilIdNumber)) { Track(e, "CivilId", e.CivilIdNumber, r.NewCivilId); e.CivilIdNumber = r.NewCivilId; }
				if (r.NewIban != null && r.NewIban != NormIban(e.Iban)) { Track(e, "Iban", Mask(NormIban(e.Iban)), Mask(r.NewIban)); e.Iban = r.NewIban; }
				if (r.NewBank != null && r.NewBank != e.BankName) { Track(e, "BankName", e.BankName, r.NewBank); e.BankName = r.NewBank; }
			}
			await _db.SaveChangesAsync(ct);
			return (ok.Count, review.Count(r => r.IsError));
		}
	}
}
