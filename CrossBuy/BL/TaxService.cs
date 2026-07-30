using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// ETA e-invoice integration is isolated behind this interface and intentionally deferred.
	// The stub records nothing on the wire; a real implementation would call the ETA API.
	public class EtaSubmitResult
	{
		public bool Submitted { get; set; }
		public string? Uuid { get; set; }
		public string Status { get; set; } = "NotConfigured";
		public string? Message { get; set; }
	}

	public interface IEtaInvoiceService
	{
		bool IsEnabled(EtaSettings? settings);
		Task<EtaSubmitResult> SubmitSalesInvoiceAsync(int companyId, int salesInvoiceId);
	}

	public class EtaInvoiceServiceStub : IEtaInvoiceService
	{
		public bool IsEnabled(EtaSettings? settings) => settings?.Enabled == true;
		public Task<EtaSubmitResult> SubmitSalesInvoiceAsync(int companyId, int salesInvoiceId) =>
			Task.FromResult(new EtaSubmitResult { Submitted = false, Status = "NotConfigured", Message = "تكامل مصلحة الضرائب غير مُفعَّل (مؤجَّل)" });
	}

	public interface ITaxService
	{
		Task<List<TaxCode>> GetCodesAsync(int companyId);
		Task<(bool ok, string? error)> CreateCodeAsync(int companyId, string code, string name, string? nameEn, string kind, decimal rate, bool isDefault);
		Task<(bool ok, string? error)> UpdateCodeAsync(int companyId, int id, string name, string? nameEn, decimal rate, bool isDefault);
		Task<(bool ok, string? error)> ToggleCodeAsync(int companyId, int id);
		Task<List<VatReturn>> GetReturnsAsync(int companyId);
		Task<VatReturn?> GetReturnAsync(int companyId, int id);
		Task<(decimal output, decimal input)> PreviewVatAsync(int companyId, DateTime from, DateTime to);
		Task<(bool ok, string? error, VatReturn? ret)> FileVatReturnAsync(int companyId, DateTime from, DateTime to, string? notes);
		Task<(bool ok, string? error)> SettleVatReturnAsync(int companyId, int returnId, DateTime settleDate, int? userId);
		Task<EtaSettings> GetEtaSettingsAsync(int companyId);
	}

	public class TaxService : ITaxService
	{
		private readonly CrossDbContext _context;
		private readonly IJournalEntryService _journals;
		public TaxService(CrossDbContext context, IJournalEntryService journals) { _context = context; _journals = journals; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
		private async Task<int?> AccIdAsync(int companyId, string code) =>
			await _context.Accounts.Where(a => a.CompanyID == companyId && a.Code == code).Select(a => (int?)a.ID).FirstOrDefaultAsync();

		private async Task<int> EnsureAccAsync(int companyId, string code, string ar, string en, string typeCode, string parentCode)
		{
			var existing = await AccIdAsync(companyId, code);
			if (existing != null) return existing.Value;
			var typeId = await _context.AccountTypes.Where(t => t.Code == typeCode).Select(t => t.ID).FirstOrDefaultAsync();
			var parentId = await AccIdAsync(companyId, parentCode);
			var acc = new Account { CompanyID = companyId, Code = code, Name = ar, NameEn = en, AccountTypeId = typeId, ParentId = parentId, IsPostable = true, IsActive = true, CreatedAt = DateTime.UtcNow };
			_context.Accounts.Add(acc);
			await _context.SaveChangesAsync();
			return acc.ID;
		}

		public async Task<List<TaxCode>> GetCodesAsync(int companyId) =>
			await _context.TaxCodes.AsNoTracking().Where(c => c.CompanyID == companyId).OrderBy(c => c.Kind).ThenBy(c => c.Code).ToListAsync();

		public async Task<(bool ok, string? error)> CreateCodeAsync(int companyId, string code, string name, string? nameEn, string kind, decimal rate, bool isDefault)
		{
			if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name)) return (false, "الكود والاسم مطلوبان");
			if (rate < 0 || rate > 100) return (false, "النسبة يجب أن تكون بين 0 و100");
			if (await _context.TaxCodes.AnyAsync(c => c.CompanyID == companyId && c.Code == code)) return (false, "كود الضريبة مستخدم من قبل");
			kind = kind == "WHT" ? "WHT" : "VAT";
			if (isDefault)
			{
				var others = await _context.TaxCodes.Where(c => c.CompanyID == companyId && c.Kind == kind && c.IsDefault).ToListAsync();
				foreach (var o in others) o.IsDefault = false;
			}
			_context.TaxCodes.Add(new TaxCode { CompanyID = companyId, Code = code.Trim(), Name = name.Trim(), NameEn = nameEn, Kind = kind, Rate = rate, IsActive = true, IsDefault = isDefault, CreatedAt = DateTime.UtcNow });
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> UpdateCodeAsync(int companyId, int id, string name, string? nameEn, decimal rate, bool isDefault)
		{
			var c = await _context.TaxCodes.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (c == null) return (false, "كود الضريبة غير موجود");
			if (string.IsNullOrWhiteSpace(name)) return (false, "الاسم مطلوب");
			if (rate < 0 || rate > 100) return (false, "النسبة يجب أن تكون بين 0 و100");
			// only one default per kind (Code + Kind stay immutable — they may be referenced by posted transactions)
			if (isDefault && !c.IsDefault)
			{
				var others = await _context.TaxCodes.Where(x => x.CompanyID == companyId && x.Kind == c.Kind && x.IsDefault && x.ID != id).ToListAsync();
				foreach (var o in others) o.IsDefault = false;
			}
			c.Name = name.Trim(); c.NameEn = nameEn; c.Rate = rate; c.IsDefault = isDefault;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> ToggleCodeAsync(int companyId, int id)
		{
			var c = await _context.TaxCodes.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (c == null) return (false, "كود الضريبة غير موجود");
			c.IsActive = !c.IsActive;
			if (!c.IsActive) c.IsDefault = false;   // an inactive code can't stay the default
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<List<VatReturn>> GetReturnsAsync(int companyId) =>
			await _context.VatReturns.AsNoTracking().Where(r => r.CompanyID == companyId).OrderByDescending(r => r.PeriodEnd).ToListAsync();

		public async Task<VatReturn?> GetReturnAsync(int companyId, int id) =>
			await _context.VatReturns.AsNoTracking().FirstOrDefaultAsync(r => r.ID == id && r.CompanyID == companyId);

		// movement on the VAT accounts within the period
		private async Task<(decimal output, decimal input)> ComputeAsync(int companyId, DateTime fromDate, DateTime toDate)
		{
			var outAcc = await AccIdAsync(companyId, "210201");   // VAT output payable (credit-normal)
			var inAcc = await AccIdAsync(companyId, "110401");    // VAT input (debit-normal)
			var start = fromDate.Date; var end = toDate.Date;
			var lines = await (from l in _context.JournalEntryLines.AsNoTracking()
							   join e in _context.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.ID
							   where e.CompanyID == companyId && (e.Status == "Posted" || e.Status == "Reversed")
									 && e.EntryDate >= start && e.EntryDate <= end
									 && (l.AccountId == outAcc || l.AccountId == inAcc)
							   select new { l.AccountId, l.Debit, l.Credit }).ToListAsync();
			var output = R(lines.Where(x => x.AccountId == outAcc).Sum(x => x.Credit - x.Debit));
			var input = R(lines.Where(x => x.AccountId == inAcc).Sum(x => x.Debit - x.Credit));
			return (output, input);
		}

		public async Task<(decimal output, decimal input)> PreviewVatAsync(int companyId, DateTime from, DateTime to) =>
			await ComputeAsync(companyId, from, to);

		public async Task<(bool ok, string? error, VatReturn? ret)> FileVatReturnAsync(int companyId, DateTime from, DateTime to, string? notes)
		{
			if (to.Date < from.Date) return (false, "نهاية الفترة قبل بدايتها", null);
			if (await _context.VatReturns.AnyAsync(r => r.CompanyID == companyId && r.PeriodStart == from.Date && r.PeriodEnd == to.Date))
				return (false, "تم تقديم إقرار لهذه الفترة من قبل", null);
			var (output, input) = await ComputeAsync(companyId, from, to);
			var ret = new VatReturn
			{
				CompanyID = companyId, PeriodStart = from.Date, PeriodEnd = to.Date, OutputVat = output, InputVat = input,
				NetDue = R(output - input), Status = "Filed", FiledAt = DateTime.UtcNow, Notes = notes, CreatedAt = DateTime.UtcNow,
			};
			_context.VatReturns.Add(ret);
			await _context.SaveChangesAsync();
			return (true, null, ret);
		}

		public async Task<(bool ok, string? error)> SettleVatReturnAsync(int companyId, int returnId, DateTime settleDate, int? userId)
		{
			var ret = await _context.VatReturns.FirstOrDefaultAsync(r => r.ID == returnId && r.CompanyID == companyId);
			if (ret == null) return (false, "الإقرار غير موجود");
			if (ret.Status == "Settled") return (false, "الإقرار مُسوّى بالفعل");

			var outAcc = await AccIdAsync(companyId, "210201");
			var inAcc = await AccIdAsync(companyId, "110401");
			if (outAcc == null || inAcc == null) return (false, "حسابات ض.ق.م غير مُهيّأة");

			var jlines = new List<JournalLineInput>();
			if (ret.OutputVat != 0) jlines.Add(new() { AccountId = outAcc.Value, Debit = ret.OutputVat, Credit = 0, Description = "إقفال ض.ق.م مخرجات" });
			if (ret.InputVat != 0) jlines.Add(new() { AccountId = inAcc.Value, Debit = 0, Credit = ret.InputVat, Description = "إقفال ض.ق.م مدخلات" });

			var net = R(ret.OutputVat - ret.InputVat);
			if (net > 0)
			{
				var payable = await EnsureAccAsync(companyId, "210205", "ض.ق.م مستحقة السداد للمصلحة", "VAT Payable to Authority", "LIAB", "2102");
				jlines.Add(new() { AccountId = payable, Debit = 0, Credit = net, Description = "صافي ض.ق.م مستحقة" });
			}
			else if (net < 0)
			{
				var carry = await EnsureAccAsync(companyId, "110402", "رصيد ض.ق.م مُرحَّل", "VAT Credit Carryforward", "ASSET", "1104");
				jlines.Add(new() { AccountId = carry, Debit = -net, Credit = 0, Description = "رصيد ضريبي دائن مُرحَّل" });
			}
			if (jlines.Count < 2) return (false, "لا توجد حركة ضريبية في هذه الفترة للتسوية");

			var (ok, err, entry) = await _journals.CreateAndPostAsync(new JournalEntryInput
			{
				CompanyID = companyId, EntryDate = settleDate.Date, JournalType = "Auto", SourceType = "VatReturn", SourceId = ret.ID,
				Description = $"تسوية ض.ق.م للفترة {ret.PeriodStart:yyyy/MM/dd} - {ret.PeriodEnd:yyyy/MM/dd}", DescriptionEn = "VAT settlement", Lines = jlines,
			}, userId);
			if (!ok) return (false, err);
			ret.Status = "Settled"; ret.JournalEntryId = entry!.ID;
			await _context.SaveChangesAsync();
			return (true, null);
		}

		public async Task<EtaSettings> GetEtaSettingsAsync(int companyId)
		{
			var s = await _context.EtaSettings.FirstOrDefaultAsync(x => x.CompanyID == companyId);
			if (s == null)
			{
				s = new EtaSettings { CompanyID = companyId, Enabled = false, Environment = "Preprod", UpdatedAt = DateTime.UtcNow };
				_context.EtaSettings.Add(s);
				await _context.SaveChangesAsync();
			}
			return s;
		}
	}
}
