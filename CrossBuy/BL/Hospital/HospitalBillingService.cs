using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL BILLING SERVICE — the catalogue, the bill, the cashier.
	//
	//   · A signed outpatient visit gets its bill at signing (consultation fee + the doctor's
	//     orders); an inpatient stay gets its bill at admission, and its accommodation is counted
	//     from the bed stays — one charge per night, to the bed the patient lay in at midnight.
	//   · Lines are snapshots (code, names, price, tax); a later price change never rewrites a bill.
	//   · Closing a bill posts it: Dr patient receivable / Cr revenue (per account) / Cr output VAT.
	//     A stay is closed only after discharge, a visit only once it is signed.
	//   · A payment is taken on the cashier's OPEN shift and posts at once: Dr cash or bank /
	//     Cr patient receivable. It never exceeds what the patient still owes.
	//   · Closing a shift counts the cash by the currency's own notes and coins; a difference posts
	//     to the cash over/short account.
	// The general ledger is reached only through IHospitalLedger (the accounting journal engine).
	// Catalogue and settings ask `manage`; bills, payments and shifts ask `bill`.
	// ==========================================================================================

	public sealed class HospitalServiceInput
	{
		public int? Id { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string Category { get; set; } = HospitalServiceCategories.Other;
		public int? DepartmentId { get; set; }
		public decimal Price { get; set; }
		public bool Taxable { get; set; }
		public int? RevenueAccountId { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public sealed class HospitalBillingSettingsInput
	{
		public decimal VatRatePercent { get; set; }
		public int? CashAccountId { get; set; }
		public int? BankAccountId { get; set; }
		public int? ReceivableAccountId { get; set; }
		public int? RevenueAccountId { get; set; }
		public int? VatAccountId { get; set; }
		public int? CashOverShortAccountId { get; set; }
		public int? FxDifferenceAccountId { get; set; }
		public int? PatientDepositAccountId { get; set; }   // H143
	}

	/// A bill line as the screen shows it, with its money worked out. Accommodation lines of an open
	/// stay are "running": computed now, not yet stored.
	public sealed record HospitalBillLineView(int? Id, string Code, string Description, string DescriptionEn, string Category, decimal Quantity,
		decimal UnitPrice, decimal Gross, decimal Insurer, decimal Patient, decimal Vat, string Source, bool Running, int? RevenueAccountId, bool OutsideList = false);

	/// H63: `Waived` = approved charity waivers on the bill — what the patient no longer owes, never collected.
	/// H148: `Credited` = credit notes issued on the bill after it closed (medicine brought back) — net + VAT.
	public sealed record HospitalBillTotals(decimal Gross, decimal Insurer, decimal Patient, decimal Vat, decimal Paid, decimal Waived = 0, decimal Credited = 0)
	{
		public decimal Due => Patient + Vat - Paid - Waived - Credited;
	}

	/// Currency is the bill's (its list's, or the facility's); List is the list that priced it (null = the main list).
	/// Who covers a bill (H18): the payer, its co-pay, and the contract discount neither side pays.
	public sealed record HospitalBillCover(string Payer, string PayerEn, decimal CopayPct, decimal Discount, bool Frozen, int PayerId = 0);

	public sealed record HospitalBillView(HospitalBill Bill, HospitalPatient Patient, IReadOnlyList<HospitalBillLineView> Lines,
		IReadOnlyList<HospitalPayment> Payments, HospitalBillTotals Totals, decimal VatRatePercent, HospitalPriceCurrency? Currency = null, HospitalPriceList? List = null,
		HospitalBillCover? Cover = null)
	{
		public bool Foreign => Bill.CurrencyId != null;
	}

	public sealed record HospitalBillRow(int Id, string BillNo, int PatientId, string PatientName, string PatientNameEn, string Mrn, string? PatientPhoto,
		bool Inpatient, string Status, DateTime OpenedAt, decimal Total, decimal Paid, HospitalPriceCurrency? Currency = null);

	public sealed record HospitalShiftSummary(HospitalCashierShift Shift, IReadOnlyDictionary<string, decimal> ByMethod, decimal CashReceipts, decimal Expected, int Receipts);

	public sealed class HospitalBillingService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IHospitalLedger _ledger;
		private readonly HospitalPricingService? _pricing;

		public HospitalBillingService(CrossDbContext db, IHospitalAccessService access, IHospitalLedger ledger, HospitalPricingService? pricing = null)
		{ _db = db; _access = access; _ledger = ledger; _pricing = pricing; }

		/// H7: a new bill takes the patient's approved list valid on the day — and that list's currency.
		private async Task PriceBillAsync(BusinessContext ctx, HospitalBill bill, DateTime day, CancellationToken ct)
		{
			if (_pricing == null) return;
			var pl = await _pricing.ListForPatientAsync(ctx, bill.PatientId, day, ct);
			bill.PriceListId = pl?.ID; bill.CurrencyId = pl?.CurrencyId;
		}

		private async Task<HospitalPriceQuote> QuoteAsync(BusinessContext ctx, HospitalBill bill, HospitalService s, decimal? orderPrice, CancellationToken ct)
		{
			// The main list (or no pricing service): the card's price, or the price the doctor ordered at.
			if (bill.PriceListId == null || _pricing == null) return new HospitalPriceQuote(orderPrice ?? s.Price, null, null, false, "main");
			return await _pricing.QuoteServiceAsync(ctx, bill.PriceListId, s, ct);
		}

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);

		// ====================================================================================
		// SETTINGS
		// ====================================================================================

		public async Task<HospitalBillingSettings> SettingsAsync(BusinessContext ctx, CancellationToken ct = default)
			=> await _db.HospitalBillingSettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct)
				?? new HospitalBillingSettings { CompanyId = ctx.CompanyId };

		public async Task<HospitalResult> SaveSettingsAsync(BusinessContext ctx, HospitalBillingSettingsInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			if (input.VatRatePercent is < 0 or > 100) return HospitalResult.Fail("The tax rate is 0 to 100%");
			var ids = new[] { input.CashAccountId, input.BankAccountId, input.ReceivableAccountId, input.RevenueAccountId, input.VatAccountId, input.CashOverShortAccountId, input.FxDifferenceAccountId, input.PatientDepositAccountId }
				.Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
			var found = await _db.Accounts.AsNoTracking().Where(a => a.CompanyID == ctx.CompanyId && a.IsActive && a.IsPostable && ids.Contains(a.ID)).Select(a => a.ID).ToListAsync(ct);
			if (found.Count != ids.Count) return HospitalResult.Fail("Choose active posting accounts of this company");

			var row = await _db.HospitalBillingSettings.FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId, ct);
			if (row == null) { row = new HospitalBillingSettings { CompanyId = ctx.CompanyId }; _db.HospitalBillingSettings.Add(row); }
			row.VatRatePercent = input.VatRatePercent; row.CashAccountId = input.CashAccountId; row.BankAccountId = input.BankAccountId;
			row.ReceivableAccountId = input.ReceivableAccountId; row.RevenueAccountId = input.RevenueAccountId; row.VatAccountId = input.VatAccountId;
			row.CashOverShortAccountId = input.CashOverShortAccountId; row.FxDifferenceAccountId = input.FxDifferenceAccountId; row.PatientDepositAccountId = input.PatientDepositAccountId; row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		// ====================================================================================
		// THE SERVICE CATALOGUE
		// ====================================================================================

		public async Task<IReadOnlyList<HospitalService>> ServicesAsync(BusinessContext ctx, string? category = null, bool activeOnly = false, CancellationToken ct = default)
		{
			var q = _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId);
			if (category != null) q = q.Where(s => s.Category == category);
			if (activeOnly) q = q.Where(s => s.IsActive);
			return await q.OrderBy(s => s.Category).ThenBy(s => s.SortOrder).ThenBy(s => s.Code).ToListAsync(ct);
		}

		public async Task<HospitalResult> SaveServiceAsync(BusinessContext ctx, HospitalServiceInput input, int? employeeId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var code = (input.Code ?? "").Trim();
			if (code.Length is 0 or > 40) return HospitalResult.Fail("Enter a code of up to 40 characters");
			if (string.IsNullOrWhiteSpace(input.Name)) return HospitalResult.Fail("Enter the Arabic name");
			if (string.IsNullOrWhiteSpace(input.NameEn)) return HospitalResult.Fail("Enter the English name");
			if (!HospitalServiceCategories.IsKnown(input.Category)) return HospitalResult.Fail("Unknown service category");
			if (input.Price < 0) return HospitalResult.Fail("A price cannot be negative");
			int self = input.Id ?? 0;   // a local, not `input.Id ?? 0` inside the query: EF cannot translate that comparison
			if (await _db.HospitalServices.AnyAsync(s => s.CompanyId == ctx.CompanyId && s.Code == code && s.ID != self, ct))
				return HospitalResult.Fail("Another service already uses this code");
			if (input.DepartmentId is int dep && !await _db.HospitalDepartments.AnyAsync(d => d.ID == dep && d.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			if (input.RevenueAccountId is int acc && !await _db.Accounts.AnyAsync(a => a.ID == acc && a.CompanyID == ctx.CompanyId && a.IsPostable && a.IsActive, ct))
				return HospitalResult.Fail("Choose active posting accounts of this company");

			HospitalService? row;
			if (input.Id is int id)
			{
				row = await _db.HospitalServices.FirstOrDefaultAsync(s => s.ID == id && s.CompanyId == ctx.CompanyId, ct);
				if (row == null) return HospitalResult.Fail("not_found");
				row.UpdatedAt = DateTime.UtcNow; row.UpdatedByEmployeeId = employeeId;
			}
			else
			{
				row = new HospitalService { CompanyId = ctx.CompanyId, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
				_db.HospitalServices.Add(row);
			}
			row.Code = code; row.Name = input.Name.Trim(); row.NameEn = input.NameEn.Trim(); row.Category = input.Category;
			row.DepartmentId = input.DepartmentId; row.Price = Math.Round(input.Price, 4); row.Taxable = input.Taxable;
			row.RevenueAccountId = input.RevenueAccountId; row.IsActive = input.IsActive;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// A starter catalogue: a consultation per department (and a general one), common lab tests,
		/// imaging and procedures. Prices are placeholders in the company's currency, edited after.
		public async Task<HospitalResult> SeedDefaultServicesAsync(BusinessContext ctx, int? employeeId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("forbidden");
			var have = (await _db.HospitalServices.Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.Code).ToListAsync(ct)).ToHashSet();
			var depts = await _db.HospitalDepartments.AsNoTracking().Where(d => d.CompanyId == ctx.CompanyId && d.IsActive).ToListAsync(ct);
			var list = new List<HospitalService>
			{
				New("CONS-GEN", "كشف عام", "General consultation", HospitalServiceCategories.Consultation, 15m, null),
			};
			foreach (var d in depts.Where(d => d.Code != "ER"))
				list.Add(New("CONS-" + d.Code, "كشف " + d.Name, "Consultation — " + (d.NameEn ?? d.Name), HospitalServiceCategories.Consultation, d.Code is "CARD" or "OBG" or "ORTH" ? 25m : 20m, d.ID));
			var fixedOnes = new (string Code, string Ar, string En, string Cat, decimal Price)[]
			{
				("LAB-CBC", "صورة دم كاملة", "Complete blood count", HospitalServiceCategories.Lab, 6m),
				("LAB-HBA1C", "سكر تراكمي", "HbA1c", HospitalServiceCategories.Lab, 8m),
				("LAB-FBS", "سكر صائم", "Fasting blood sugar", HospitalServiceCategories.Lab, 3m),
				("LAB-LIPID", "دهون الدم", "Lipid profile", HospitalServiceCategories.Lab, 10m),
				("LAB-KFT", "وظائف الكلى", "Kidney function tests", HospitalServiceCategories.Lab, 9m),
				("LAB-LFT", "وظائف الكبد", "Liver function tests", HospitalServiceCategories.Lab, 9m),
				("LAB-TSH", "هرمون الغدة الدرقية", "TSH", HospitalServiceCategories.Lab, 9m),
				("LAB-URINE", "تحليل بول", "Urinalysis", HospitalServiceCategories.Lab, 3m),
				("LAB-CRP", "بروتين سي التفاعلي", "C-reactive protein", HospitalServiceCategories.Lab, 5m),
				("LAB-TROP", "إنزيم القلب (تروبونين)", "Troponin", HospitalServiceCategories.Lab, 12m),
				("RAD-CXR", "أشعة صدر", "Chest X-ray", HospitalServiceCategories.Radiology, 12m),
				("RAD-USAB", "موجات صوتية للبطن", "Abdominal ultrasound", HospitalServiceCategories.Radiology, 25m),
				("RAD-USPEL", "موجات صوتية للحوض", "Pelvic ultrasound", HospitalServiceCategories.Radiology, 25m),
				("RAD-CT", "أشعة مقطعية", "CT scan", HospitalServiceCategories.Radiology, 80m),
				("RAD-MRI", "رنين مغناطيسي", "MRI scan", HospitalServiceCategories.Radiology, 120m),
				("PROC-ECG", "رسم قلب", "ECG", HospitalServiceCategories.Procedure, 8m),
				("PROC-ECHO", "إيكو القلب", "Echocardiogram", HospitalServiceCategories.Procedure, 45m),
				("PROC-STRESS", "اختبار جهد", "Exercise stress test", HospitalServiceCategories.Procedure, 60m),
				("PROC-NEB", "جلسة بخار", "Nebuliser session", HospitalServiceCategories.Procedure, 4m),
				("PROC-DRESS", "غيار جرح", "Wound dressing", HospitalServiceCategories.Procedure, 5m),
				("PROC-IV", "تركيب محلول وريدي", "IV fluids", HospitalServiceCategories.Procedure, 7m),
				("NM-MEAL", "وجبة مرافق", "Companion meal", HospitalServiceCategories.Other, 3m),
			};
			foreach (var f in fixedOnes) list.Add(New(f.Code, f.Ar, f.En, f.Cat, f.Price, null, taxable: f.Cat == HospitalServiceCategories.Other));
			int order = 0;
			foreach (var s in list) { s.SortOrder = order += 10; if (!have.Contains(s.Code)) _db.HospitalServices.Add(s); }
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success();

			HospitalService New(string code, string ar, string en, string cat, decimal price, int? dept, bool taxable = false) => new()
			{ CompanyId = ctx.CompanyId, Code = code, Name = ar, NameEn = en, Category = cat, Price = price, DepartmentId = dept, Taxable = taxable, CreatedAt = DateTime.UtcNow, CreatedByEmployeeId = employeeId };
		}

		// ====================================================================================
		// BILLS
		// ====================================================================================

		private async Task<string> NextNumberAsync(IQueryable<string> issued, string prefix, CancellationToken ct)
		{
			var nums = await issued.Where(n => n.StartsWith(prefix)).ToListAsync(ct);
			int max = 0;
			foreach (var n in nums) if (int.TryParse(n.AsSpan(prefix.Length), out var v) && v > max) max = v;
			return prefix + (max + 1).ToString("000000");
		}

		/// Adds a signed visit's charges to the context (no save): the bill, the doctor's consultation
		/// fee and the orders. Called by the consultation service inside its signing transaction.
		public async Task AddEncounterChargesAsync(BusinessContext ctx, HospitalEncounter e, IReadOnlyList<HospitalEncounterOrder> orders, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (await _db.HospitalBills.AnyAsync(b => b.CompanyId == ctx.CompanyId && b.EncounterId == e.ID, ct)) return;
			var settings = await SettingsAsync(ctx, ct);
			var bill = new HospitalBill
			{
				CompanyId = ctx.CompanyId, BillNo = await NextNumberAsync(_db.HospitalBills.Where(b => b.CompanyId == ctx.CompanyId).Select(b => b.BillNo), "BILL-", ct),
				PatientId = e.PatientId, EncounterId = e.ID, Status = HospitalBillStatus.Open, OpenedAt = now, CreatedByEmployeeId = employeeId,
			};
			await PriceBillAsync(ctx, bill, now, ct);
			_db.HospitalBills.Add(bill);
			await _db.SaveChangesAsync(ct);   // the bill needs its id for the lines; the caller's transaction still owns the commit

			var doc = await _db.HospitalPractitioners.AsNoTracking().FirstOrDefaultAsync(p => p.ID == e.PractitionerId && p.CompanyId == ctx.CompanyId, ct);
			var fee = doc?.ConsultationServiceId is int sid
				? await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.ID == sid && s.CompanyId == ctx.CompanyId, ct)
				: null;
			fee ??= await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.IsActive && s.Category == HospitalServiceCategories.Consultation && s.DepartmentId == doc!.DepartmentId, ct);
			fee ??= await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.IsActive && s.Code == "CONS-GEN", ct);
			if (fee != null) _db.HospitalBillLines.Add(Line(bill.ID, fee, 1, settings, HospitalBillLineSources.Consultation, employeeId, now, await QuoteAsync(ctx, bill, fee, null, ct)));
			var ids = orders.Select(o => o.ServiceId).ToList();
			var services = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && ids.Contains(s.ID)).ToDictionaryAsync(s => s.ID, ct);
			foreach (var o in orders)
				if (services.TryGetValue(o.ServiceId, out var s)) _db.HospitalBillLines.Add(Line(bill.ID, s, 1, settings, HospitalBillLineSources.Order, employeeId, now, await QuoteAsync(ctx, bill, s, o.Price, ct)));
		}

		private HospitalBillLine Line(int billId, HospitalService s, decimal qty, HospitalBillingSettings settings, string source, int? employeeId, DateTime now, HospitalPriceQuote quote) => new()
		{
			CompanyId = s.CompanyId, BillId = billId, ServiceId = s.ID, Code = s.Code, Description = s.Name, DescriptionEn = s.NameEn, Category = s.Category,
			DepartmentId = s.DepartmentId, Quantity = qty, UnitPrice = quote.Price, Taxable = s.Taxable, VatRatePercent = s.Taxable ? settings.VatRatePercent : 0,
			RevenueAccountId = s.RevenueAccountId, Source = source, AddedAt = now, AddedByEmployeeId = employeeId, OutsideList = quote.OutsideList,
		};

		/// H10: the bill a dispense is charged on — the visit's while it is open, otherwise the patient's own open
		/// pharmacy bill (no visit), created and priced like a visit bill the first time. Saves; the caller (the
		/// pharmacy, inside its dispensing transaction) authorizes.
		public async Task<HospitalBill> BillForDispenseAsync(BusinessContext ctx, int patientId, int? encounterId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (encounterId != null && await _db.HospitalBills.FirstOrDefaultAsync(b => b.CompanyId == ctx.CompanyId && b.EncounterId == encounterId && b.Status == HospitalBillStatus.Open, ct) is { } visit)
				return visit;
			var own = await _db.HospitalBills.FirstOrDefaultAsync(b => b.CompanyId == ctx.CompanyId && b.PatientId == patientId && b.EncounterId == null && b.AdmissionId == null && b.Status == HospitalBillStatus.Open, ct);
			if (own != null) return own;
			var bill = new HospitalBill
			{
				CompanyId = ctx.CompanyId, BillNo = await NextNumberAsync(_db.HospitalBills.Where(b => b.CompanyId == ctx.CompanyId).Select(b => b.BillNo), "BILL-", ct),
				PatientId = patientId, Status = HospitalBillStatus.Open, OpenedAt = now, CreatedByEmployeeId = employeeId,
			};
			await PriceBillAsync(ctx, bill, now, ct);
			_db.HospitalBills.Add(bill);
			await _db.SaveChangesAsync(ct);
			return bill;
		}

		/// H10: an item's price on a bill priced by `listId` — what the pharmacy shows before it charges.
		public async Task<HospitalPriceQuote?> QuoteItemForBillAsync(BusinessContext ctx, int? listId, int itemId, CancellationToken ct = default)
			=> _pricing == null ? null : await _pricing.QuoteItemAsync(ctx, listId, itemId, ct);

		/// The label of a bill's currency (the facility's when the bill is null or in it).
		public async Task<string?> CurrencyLabelAsync(BusinessContext ctx, HospitalBill? bill, CancellationToken ct = default)
			=> _pricing == null ? null : (await _pricing.CurrencyOfAsync(ctx, bill?.CurrencyId, ct)).Label(DisplayName.IsArabic);

		/// H10: a dispensed inventory item as a bill line, priced through the bill's list (the main price
		/// otherwise). Adds without saving; the caller (the pharmacy) authorizes and saves.
		public async Task<HospitalBillLine> AddItemChargeAsync(BusinessContext ctx, HospitalBill bill, int itemId, string code, string name, string nameEn, decimal qty, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			var quote = _pricing == null ? null : await _pricing.QuoteItemAsync(ctx, bill.PriceListId, itemId, ct);
			quote ??= new HospitalPriceQuote(await _db.Items.AsNoTracking().Where(i => i.ID == itemId && i.CompanyID == ctx.CompanyId).Select(i => i.SalesPrice ?? 0m).FirstOrDefaultAsync(ct), null, null, false, "main");
			var line = new HospitalBillLine
			{
				CompanyId = ctx.CompanyId, BillId = bill.ID, Code = code, Description = name, DescriptionEn = nameEn, Category = HospitalServiceCategories.Pharmacy,
				Quantity = qty, UnitPrice = quote.Price, Taxable = false, VatRatePercent = 0, Source = HospitalBillLineSources.Pharmacy, AddedAt = now, AddedByEmployeeId = employeeId,
				OutsideList = quote.OutsideList,
			};
			_db.HospitalBillLines.Add(line);
			return line;
		}

		/// H29: a catalogue service (an operation) as a bill line, priced through the bill's list like an order.
		/// Adds without saving; the caller (the theatre, closing the case) authorizes and saves.
		/// H51: `quantity` — the blood bank charges the bags issued on one line. H53: `agreedPrice` — the fee the patient
		/// accepted (a dental plan line); on a payer's price list the list still decides, exactly as an order's price does.
		public async Task<HospitalBillLine?> AddServiceChargeAsync(BusinessContext ctx, HospitalBill bill, int serviceId, int? employeeId, DateTime now, CancellationToken ct = default, decimal quantity = 1, decimal? agreedPrice = null)
		{
			var s = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(x => x.ID == serviceId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null) return null;
			var line = Line(bill.ID, s, quantity, await SettingsAsync(ctx, ct), HospitalBillLineSources.Order, employeeId, now, await QuoteAsync(ctx, bill, s, agreedPrice, ct));
			_db.HospitalBillLines.Add(line);
			return line;
		}

		/// H121: the bill BillForWardChargeAsync would put a charge on, if there is one yet — read only, creates nothing (the
		/// pharmacy's preview prices by it and names it).
		public async Task<HospitalBill?> PeekWardChargeBillAsync(BusinessContext ctx, int patientId, CancellationToken ct = default)
		{
			var adm = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			if (adm != null && await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.CompanyId == ctx.CompanyId && b.AdmissionId == adm && b.Status == HospitalBillStatus.Open, ct) is { } stay)
				return stay;
			return await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.CompanyId == ctx.CompanyId && b.PatientId == patientId && b.EncounterId == null && b.AdmissionId == null && b.Status == HospitalBillStatus.Open, ct);
		}

		/// H51/H52: the bill a charge made on the ward goes on — the stay's bill while the patient is admitted, otherwise the
		/// patient's own open bill (created if there is none). One answer for the blood bank and the ward's doses.
		public async Task<HospitalBill> BillForWardChargeAsync(BusinessContext ctx, int patientId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			var adm = await _db.HospitalAdmissions.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.PatientId == patientId && a.Status == HospitalAdmissionStatus.Active).Select(a => (int?)a.ID).FirstOrDefaultAsync(ct);
			if (adm is int admId && (await EnsureAdmissionBillAsync(ctx, admId, employeeId, now, ct)).Id is int billId
				&& await _db.HospitalBills.FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId && b.Status == HospitalBillStatus.Open, ct) is { } stay)
				return stay;
			return await BillForDispenseAsync(ctx, patientId, null, employeeId, now, ct);
		}

		/// The stay's bill, created the first time it is asked for (on admission, or for a stay that predates billing).
		public async Task<HospitalResult> EnsureAdmissionBillAsync(BusinessContext ctx, int admissionId, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			var existing = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.AdmissionId == admissionId).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);
			if (existing != null) return HospitalResult.Success(existing);
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return HospitalResult.Fail("not_found");
			var bill = new HospitalBill
			{
				CompanyId = ctx.CompanyId, BillNo = await NextNumberAsync(_db.HospitalBills.Where(b => b.CompanyId == ctx.CompanyId).Select(b => b.BillNo), "BILL-", ct),
				PatientId = a.PatientId, AdmissionId = a.ID, Status = HospitalBillStatus.Open, OpenedAt = a.AdmittedAt, CreatedByEmployeeId = employeeId,
			};
			await PriceBillAsync(ctx, bill, a.AdmittedAt, ct);
			_db.HospitalBills.Add(bill);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException)
			{
				_db.Entry(bill).State = EntityState.Detached;
				var other = await _db.HospitalBills.AsNoTracking().Where(b => b.CompanyId == ctx.CompanyId && b.AdmissionId == admissionId).Select(b => (int?)b.ID).FirstOrDefaultAsync(ct);
				return other == null ? HospitalResult.Fail("The change could not be saved") : HospitalResult.Success(other);
			}
			return HospitalResult.Success(bill.ID);
		}

		/// Accommodation for a stay: one charge per night, to the bed the patient lay in at midnight
		/// (so a transfer day is charged once, to the later bed). A same-day stay is one day.
		public async Task<IReadOnlyList<(string RoomNumber, HospitalRoomType? Type, int Days)>> AccommodationAsync(BusinessContext ctx, int admissionId, DateTime nowUtc, CancellationToken ct = default)
		{
			var a = await _db.HospitalAdmissions.AsNoTracking().FirstOrDefaultAsync(x => x.ID == admissionId && x.CompanyId == ctx.CompanyId, ct);
			if (a == null) return Array.Empty<(string, HospitalRoomType?, int)>();
			var stays = await (from s in _db.HospitalBedStays.AsNoTracking()
							   join r in _db.HospitalRooms.AsNoTracking() on s.RoomId equals r.ID
							   join t0 in _db.HospitalRoomTypes.AsNoTracking() on r.RoomTypeId equals (int?)t0.ID into ts
							   from t in ts.DefaultIfEmpty()
							   where s.CompanyId == ctx.CompanyId && s.AdmissionId == a.ID
							   orderby s.StartedAt
							   select new { s.StartedAt, s.EndedAt, r.Number, Type = t }).ToListAsync(ct);
			if (stays.Count == 0) return Array.Empty<(string, HospitalRoomType?, int)>();
			var end = (a.DischargedAt ?? nowUtc).Date;
			var first = a.AdmittedAt.Date;
			var nights = new List<DateTime>();
			for (var d = first; d < end; d = d.AddDays(1)) nights.Add(d);
			if (nights.Count == 0) nights.Add(first);   // admitted and discharged the same day: one day
			var counts = new Dictionary<int, int>();
			foreach (var d in nights)
			{
				var idx = stays.FindLastIndex(s => s.StartedAt.Date <= d && (s.EndedAt == null || s.EndedAt.Value.Date > d || (nights.Count == 1)));
				if (idx < 0) idx = 0;
				counts[idx] = counts.GetValueOrDefault(idx) + 1;
			}
			return counts.OrderBy(k => k.Key).Select(k => (stays[k.Key].Number, stays[k.Key].Type, k.Value)).ToList();
		}

		private async Task<List<HospitalBillLineView>> RunningAccommodationAsync(BusinessContext ctx, HospitalBill bill, HospitalBillingSettings settings, DateTime nowUtc, CancellationToken ct)
		{
			var list = new List<HospitalBillLineView>();
			if (bill.AdmissionId is not int adm || bill.Status != HospitalBillStatus.Open) return list;
			foreach (var (room, type, days) in await AccommodationAsync(ctx, adm, nowUtc, ct))
			{
				decimal rate = type?.DailyRate ?? 0;
				if (bill.PriceListId != null && _pricing != null) rate = await _pricing.QuoteRoomRateAsync(ctx, bill.PriceListId, rate, ct);
				decimal gross = rate * days;
				list.Add(new HospitalBillLineView(null, "ACC-" + (type?.Code ?? room).ToUpperInvariant(), "إقامة — غرفة " + room + (type == null ? "" : " (" + type.Name + ")"),
					"Accommodation — room " + room + (type == null ? "" : " (" + (type.NameEn ?? type.Name) + ")"), HospitalServiceCategories.Accommodation,
					days, rate, gross, 0, gross, 0, HospitalBillLineSources.Accommodation, true, null));
			}
			return list;
		}

		private static HospitalBillLineView View(HospitalBillLine l)
		{
			decimal gross = Math.Round(l.Quantity * l.UnitPrice, 4);
			decimal insurer = 0;   // the payer's part is applied afterwards, by the bill's split (CoverAsync)
			decimal patient = gross - insurer;
			decimal vat = l.Taxable ? Math.Round(patient * l.VatRatePercent / 100m, 4) : 0;
			return new HospitalBillLineView(l.ID, l.Code, l.Description, l.DescriptionEn, l.Category, l.Quantity, l.UnitPrice, gross, insurer, patient, vat, l.Source, false, l.RevenueAccountId, l.OutsideList);
		}

		public async Task<HospitalBillView?> BillAsync(BusinessContext ctx, int billId, DateTime nowUtc, CancellationToken ct = default)
		{
			var bill = await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId, ct);
			if (bill == null) return null;
			var patient = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(p => p.ID == bill.PatientId && p.CompanyId == ctx.CompanyId, ct);
			if (patient == null) return null;
			var settings = await SettingsAsync(ctx, ct);
			var stored = await _db.HospitalBillLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.BillId == bill.ID).OrderBy(l => l.ID).ToListAsync(ct);
			var lines = stored.Select(View).ToList();
			// Accommodation is either running (computed now) or frozen (stored at closing) — never both,
			// or a stay being closed would be charged twice.
			if (!stored.Any(l => l.Source == HospitalBillLineSources.Accommodation))
				lines.AddRange(await RunningAccommodationAsync(ctx, bill, settings, nowUtc, ct));
			// H18: the payer's part. A bill split when it closed reads that split back; an open one is split live by the
			// patient's cover today (HospitalCoverage, the same split the claim uses). VAT is on the patient's part only.
			var rates = stored.Select(l => l.Taxable ? l.VatRatePercent : 0m).Concat(Enumerable.Repeat(0m, lines.Count - stored.Count)).ToList();
			var (cover, amounts, insurer) = await CoverAsync(ctx, bill, lines, nowUtc, ct);
			if (cover != null)
				lines = lines.Select((l, i) =>
				{
					decimal pat = amounts[i] - insurer[i];
					return l with { Insurer = insurer[i], Patient = pat, Vat = rates[i] == 0 ? 0 : Math.Round(pat * rates[i] / 100m, 4) };
				}).ToList();
			var payments = await _db.HospitalPayments.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.BillId == bill.ID).OrderBy(p => p.ReceivedAt).ToListAsync(ct);
			decimal waived = 0;
			waived = await HospitalCharityService.WaivedAsync(_db, ctx.CompanyId, bill.ID, ct);   // H63
			var credited = await CreditedAsync(_db, ctx.CompanyId, bill.ID, ct);   // H148
			var totals = new HospitalBillTotals(lines.Sum(l => l.Gross), lines.Sum(l => l.Insurer), lines.Sum(l => l.Patient), lines.Sum(l => l.Vat), payments.Sum(p => p.Amount), waived, credited);
			HospitalPriceCurrency? currency = _pricing == null ? null : await _pricing.CurrencyOfAsync(ctx, bill.CurrencyId, ct);
			var list = bill.PriceListId is int lid ? await _db.HospitalPriceLists.AsNoTracking().FirstOrDefaultAsync(l => l.ID == lid && l.CompanyId == ctx.CompanyId, ct) : null;
			return new HospitalBillView(bill, patient, lines, payments, totals, settings.VatRatePercent, currency, list, cover);
		}

		/// H148: a closed bill's credit notes, oldest first (empty when the tables are not installed).
		public async Task<IReadOnlyList<HospitalCreditNote>> CreditNotesAsync(BusinessContext ctx, int billId, CancellationToken ct = default)
		{
			try { return await _db.Set<HospitalCreditNote>().AsNoTracking().Where(n => n.CompanyId == ctx.CompanyId && n.BillId == billId).OrderBy(n => n.ID).ToListAsync(ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalCreditNote>(); }
		}

		/// H148: what credit notes took off a closed bill (0 when the credit-note tables are not installed).
		public static async Task<decimal> CreditedAsync(CrossDbContext db, int companyId, int billId, CancellationToken ct)
		{
			try { return await db.Set<HospitalCreditNote>().AsNoTracking().Where(n => n.CompanyId == companyId && n.BillId == billId).SumAsync(n => (decimal?)n.Total, ct) ?? 0; }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return 0; }
		}

		/// The split a bill is shown and posted on: frozen at closing if it was split then, live while open, none for a
		/// bill closed with no split (self-pay, or closed before H18 — the patient owed it all, as it was posted).
		private async Task<(HospitalBillCover? Cover, decimal[] Amounts, decimal[] Insurer)> CoverAsync(BusinessContext ctx, HospitalBill bill, IReadOnlyList<HospitalBillLineView> lines, DateTime nowUtc, CancellationToken ct)
		{
			var none = ((HospitalBillCover?)null, Array.Empty<decimal>(), Array.Empty<decimal>());
			var frozen = await HospitalCoverage.FrozenAsync(_db, ctx, bill.ID, ct);
			if (frozen is { } f)
			{
				var payer = await _db.Set<HospitalPayer>().AsNoTracking().FirstAsync(p => p.ID == f.Head.PayerId && p.CompanyId == ctx.CompanyId, ct);
				var amt = lines.Select(l => f.Lines.FirstOrDefault(x => x.BillLineId == l.Id)?.Amount ?? l.Gross).ToArray();
				var ins = lines.Select(l => f.Lines.FirstOrDefault(x => x.BillLineId == l.Id)?.InsurerAmount ?? 0).ToArray();
				return (new HospitalBillCover(payer.Name, payer.NameEn, f.Head.CopayPct, lines.Sum(l => l.Gross) - amt.Sum(), true, payer.ID), amt, ins);
			}
			if (bill.Status != HospitalBillStatus.Open) return none;
			var split = await HospitalCoverage.SplitAsync(_db, _pricing, ctx, bill.PatientId, bill.PriceListId, bill.CurrencyId,
				lines.Select(l => new HospitalSplitLine(l.Id, l.Code, l.Category, l.Quantity, l.UnitPrice, l.OutsideList)).ToList(), nowUtc, null, ct);
			if (split == null) return none;
			var amounts = split.Lines.Select(l => l.Amount).ToArray();
			return (new HospitalBillCover(split.Payer.Name, split.Payer.NameEn, split.Payer.CopayPct, lines.Sum(l => l.Gross) - amounts.Sum(), false, split.Payer.ID), amounts, split.Split.InsurerPerLine.ToArray());
		}

		public async Task<IReadOnlyList<HospitalBillRow>> BillsAsync(BusinessContext ctx, string? status, int? patientId, DateTime nowUtc, CancellationToken ct = default)
		{
			var q = from b in _db.HospitalBills.AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on b.PatientId equals p.ID
					where b.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId
					select new { b, p };
			if (status != null) q = q.Where(x => x.b.Status == status);
			if (patientId != null) q = q.Where(x => x.b.PatientId == patientId);
			var rows = await q.OrderByDescending(x => x.b.ID).Take(300).ToListAsync(ct);
			var result = new List<HospitalBillRow>();
			foreach (var x in rows)
			{
				var v = await BillAsync(ctx, x.b.ID, nowUtc, ct);
				result.Add(new HospitalBillRow(x.b.ID, x.b.BillNo, x.p.ID, x.p.Name, x.p.NameEn, x.p.Mrn, x.p.PhotoPath, x.b.AdmissionId != null, x.b.Status, x.b.OpenedAt,
					v!.Totals.Patient + v.Totals.Vat - v.Totals.Waived - v.Totals.Credited, v.Totals.Paid, v.Currency));   // H63: net of charity (H148: and of credit notes)
			}
			return result;
		}

		public async Task<HospitalResult> AddLineAsync(BusinessContext ctx, int billId, int serviceId, decimal quantity, int? employeeId, DateTime now, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (quantity is <= 0 or > 1000) return HospitalResult.Fail("A quantity is 1 to 1000");
			var bill = await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId, ct);
			if (bill == null) return HospitalResult.Fail("not_found");
			if (bill.Status != HospitalBillStatus.Open) return HospitalResult.Fail("This bill is closed");
			var s = await _db.HospitalServices.AsNoTracking().FirstOrDefaultAsync(x => x.ID == serviceId && x.CompanyId == ctx.CompanyId && x.IsActive, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			if (s.Category == HospitalServiceCategories.Accommodation) return HospitalResult.Fail("Accommodation is counted from the bed stays");
			var line = Line(bill.ID, s, quantity, await SettingsAsync(ctx, ct), HospitalBillLineSources.Manual, employeeId, now, await QuoteAsync(ctx, bill, s, null, ct));
			_db.HospitalBillLines.Add(line);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(line.ID);
		}

		public async Task<HospitalResult> RemoveLineAsync(BusinessContext ctx, int lineId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var line = await _db.HospitalBillLines.FirstOrDefaultAsync(l => l.ID == lineId && l.CompanyId == ctx.CompanyId, ct);
			if (line == null) return HospitalResult.Fail("not_found");
			var bill = await _db.HospitalBills.AsNoTracking().FirstAsync(b => b.ID == line.BillId && b.CompanyId == ctx.CompanyId, ct);
			if (bill.Status != HospitalBillStatus.Open) return HospitalResult.Fail("This bill is closed");
			if (line.Source != HospitalBillLineSources.Manual) return HospitalResult.Fail("Only a line added at the desk can be removed here");
			_db.HospitalBillLines.Remove(line);
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(bill.ID);
		}

		/// What closing the bill would post, line by line (the screen's journal preview), in the FACILITY's
		/// currency: a foreign-currency bill is converted at `rate` (bill → facility; its closing rate once closed,
		/// today's for a preview), and a rounding cent lands on the receivable so the entry balances.
		public async Task<(IReadOnlyList<HospitalLedgerLine> Lines, string? Missing)> PreviewAsync(BusinessContext ctx, HospitalBillView v, CancellationToken ct = default)
		{
			if (!v.Foreign) return await PreviewInBillCurrencyAsync(ctx, v, null, ct);
			decimal rate;
			if (v.Bill.ExchangeRate is decimal fixedRate) rate = fixedRate;
			else
			{
				var (ok, r) = _pricing == null ? (false, 0m) : await _pricing.RateToFacilityAsync(ctx, v.Bill.CurrencyId, DateTime.UtcNow, ct);
				if (!ok) return (Array.Empty<HospitalLedgerLine>(), "There is no exchange rate for the bill's currency. Enter one in Accounting first");
				rate = r;
			}
			return await PreviewAtRateAsync(ctx, v, rate, null, ct);
		}

		private async Task<(IReadOnlyList<HospitalLedgerLine> Lines, string? Missing)> PreviewAtRateAsync(BusinessContext ctx, HospitalBillView v, decimal rate, (int? Account, string? Refusal)? payerAr, CancellationToken ct)
		{
			var (lines, missing) = await PreviewInBillCurrencyAsync(ctx, v, payerAr, ct);
			if (missing != null || lines.Count == 0) return (lines, missing);
			int dec = _pricing == null ? 3 : (await _pricing.FacilityCurrencyAsync(ctx, ct)).Decimals;
			var conv = lines.Select(l => l with { Debit = Math.Round(l.Debit * rate, dec, MidpointRounding.AwayFromZero), Credit = Math.Round(l.Credit * rate, dec, MidpointRounding.AwayFromZero) }).ToList();
			var diff = conv.Sum(l => l.Credit) - conv.Sum(l => l.Debit);
			if (diff != 0 && conv.Count > 0 && conv[0].Debit > 0) conv[0] = conv[0] with { Debit = conv[0].Debit + diff };
			return (conv, null);
		}

		/// `payerAr`: the payer receivable already resolved (closing resolves it before its transaction opens);
		/// null = resolve it here. An insured bill with no payer receivable has NO preview and cannot close (H20, D2):
		/// the payer's share never lands on the patients' account.
		private async Task<(IReadOnlyList<HospitalLedgerLine> Lines, string? Missing)> PreviewInBillCurrencyAsync(BusinessContext ctx, HospitalBillView v, (int? Account, string? Refusal)? payerAr, CancellationToken ct)
		{
			var s = await SettingsAsync(ctx, ct);
			if (s.ReceivableAccountId == null || s.RevenueAccountId == null) return (Array.Empty<HospitalLedgerLine>(), "Map the receivable and revenue accounts in billing settings");
			if (v.Totals.Vat > 0 && s.VatAccountId == null) return (Array.Empty<HospitalLedgerLine>(), "Map the output VAT account in billing settings");
			int? payerAccount = null;
			if (v.Totals.Insurer > 0)
			{
				if (v.Cover is not { PayerId: > 0 } cover) return (Array.Empty<HospitalLedgerLine>(), HospitalClaimAccounts.NotSetUp);
				var (acc, refusal) = payerAr ?? await HospitalClaimAccounts.PayerReceivableAsync(_db, ctx, cover.PayerId, ct);
				if (refusal != null || acc == null) return (Array.Empty<HospitalLedgerLine>(), refusal ?? HospitalClaimAccounts.NotSetUp);
				if (acc == s.ReceivableAccountId) return (Array.Empty<HospitalLedgerLine>(), "The payer receivable cannot be the patients' receivable account");
				payerAccount = acc;
			}
			var lines = new List<HospitalLedgerLine>();
			var due = v.Totals.Patient + v.Totals.Vat;
			if (due > 0) lines.Add(new HospitalLedgerLine(s.ReceivableAccountId.Value, due, 0, v.Bill.BillNo));
			// H18: what the payer owes is its own receivable line, referenced by the payer; the contract's discount is
			// revenue never earned, so revenue is credited net of it (patient + payer, line by line)
			if (payerAccount is int pa) lines.Add(new HospitalLedgerLine(pa, v.Totals.Insurer, 0, v.Bill.BillNo + " · " + (v.Cover?.PayerEn ?? "payer")));
			foreach (var g in v.Lines.GroupBy(l => l.RevenueAccountId ?? s.RevenueAccountId.Value))
			{
				var amt = g.Sum(l => l.Insurer + l.Patient);
				if (amt > 0) lines.Add(new HospitalLedgerLine(g.Key, 0, amt, v.Bill.BillNo));
			}
			if (v.Totals.Vat > 0) lines.Add(new HospitalLedgerLine(s.VatAccountId!.Value, 0, v.Totals.Vat, v.Bill.BillNo));
			return (lines, null);
		}

		public async Task<HospitalResult> CloseAsync(BusinessContext ctx, int billId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var bill = await _db.HospitalBills.FirstOrDefaultAsync(b => b.ID == billId && b.CompanyId == ctx.CompanyId, ct);
			if (bill == null) return HospitalResult.Fail("not_found");
			if (bill.Status != HospitalBillStatus.Open) return HospitalResult.Fail("This bill is closed");
			if (bill.AdmissionId is int adm)
			{
				var a = await _db.HospitalAdmissions.AsNoTracking().FirstAsync(x => x.ID == adm && x.CompanyId == ctx.CompanyId, ct);
				if (a.Status != HospitalAdmissionStatus.Discharged) return HospitalResult.Fail("Discharge the patient before closing the stay's bill");
			}
			if (bill.EncounterId is int enc && !await _db.HospitalEncounters.AnyAsync(e => e.ID == enc && e.CompanyId == ctx.CompanyId && e.Status == HospitalEncounterStatus.Signed, ct))
				return HospitalResult.Fail("Sign the visit before closing its bill");

			// H20 (D2): an insured bill's payer receivable is resolved BEFORE the transaction opens (a database without
			// the mapping tables reads as unmapped, and the close is refused rather than posting to the patients' account)
			var before = (await BillAsync(ctx, bill.ID, nowUtc, ct))!;
			(int? Account, string? Refusal)? payerAr = null;
			if (before.Cover is { PayerId: > 0 } bc)
			{
				payerAr = await HospitalClaimAccounts.PayerReceivableAsync(_db, ctx, bc.PayerId, ct);
				if (before.Totals.Insurer > 0 && payerAr.Value.Refusal != null) return HospitalResult.Fail(payerAr.Value.Refusal);
			}

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			// Freeze the running accommodation into stored lines: the bill no longer moves with the clock.
			var settings = await SettingsAsync(ctx, ct);
			foreach (var r in await RunningAccommodationAsync(ctx, bill, settings, nowUtc, ct))
				_db.HospitalBillLines.Add(new HospitalBillLine
				{
					CompanyId = ctx.CompanyId, BillId = bill.ID, Code = r.Code, Description = r.Description, DescriptionEn = r.DescriptionEn, Category = r.Category,
					Quantity = r.Quantity, UnitPrice = r.UnitPrice, Source = HospitalBillLineSources.Accommodation, AddedAt = nowUtc, AddedByEmployeeId = employeeId,
				});
			await _db.SaveChangesAsync(ct);

			// H18: freeze the split the journal is about to post, so the claim asks the payer for exactly that
			var storedNow = await _db.HospitalBillLines.AsNoTracking().Where(l => l.CompanyId == ctx.CompanyId && l.BillId == bill.ID).OrderBy(l => l.ID).ToListAsync(ct);
			var split = await HospitalCoverage.SplitAsync(_db, _pricing, ctx, bill.PatientId, bill.PriceListId, bill.CurrencyId,
				storedNow.Select(l => new HospitalSplitLine(l.ID, l.Code, l.Category, l.Quantity, l.UnitPrice, l.OutsideList)).ToList(), nowUtc, null, ct);
			if (split != null && !await _db.Set<HospitalBillCoverage>().AnyAsync(c => c.CompanyId == ctx.CompanyId && c.BillId == bill.ID, ct))
			{
				var head = new HospitalBillCoverage
				{
					CompanyId = ctx.CompanyId, BillId = bill.ID, PayerId = split.Payer.ID, PolicyId = split.Policy.ID, CopayPct = split.Payer.CopayPct, CopayCap = split.Payer.CopayCap,
					DiscountPct = bill.PriceListId == null ? split.Payer.DiscountPct : 0, Amount = split.Split.Gross, Covered = split.Split.Covered, Copay = split.Split.Copay,
					InsurerShare = split.Split.InsurerShare, PatientShare = split.Split.PatientShare, FrozenAt = nowUtc,
				};
				_db.Add(head);
				await _db.SaveChangesAsync(ct);
				for (int i = 0; i < storedNow.Count; i++)
					_db.Add(new HospitalBillCoverageLine { CompanyId = ctx.CompanyId, CoverageId = head.ID, BillLineId = storedNow[i].ID, Amount = split.Lines[i].Amount, Covered = split.Lines[i].Covered, InsurerAmount = split.Split.InsurerPerLine[i] });
				await _db.SaveChangesAsync(ct);
			}

			var v = (await BillAsync(ctx, bill.ID, nowUtc, ct))!;
			if (v.Totals.Gross > 0)
			{
				decimal rate = 1;
				if (v.Foreign)
				{
					var (rok, r) = _pricing == null ? (false, 0m) : await _pricing.RateToFacilityAsync(ctx, bill.CurrencyId, nowUtc, ct);
					if (!rok) return HospitalResult.Fail("There is no exchange rate for the bill's currency. Enter one in Accounting first");
					rate = r; bill.ExchangeRate = r;
				}
				// the payer must be the one resolved before the transaction; otherwise refuse rather than query inside it
				if (v.Totals.Insurer > 0 && (payerAr == null || v.Cover?.PayerId != before.Cover?.PayerId)) return HospitalResult.Fail(HospitalInsuranceService.ConcurrentChange);
				var (lines, missing) = v.Foreign ? await PreviewAtRateAsync(ctx, v, rate, payerAr ?? (null, null), ct) : await PreviewInBillCurrencyAsync(ctx, v, payerAr ?? (null, null), ct);
				if (missing != null) return HospitalResult.Fail(missing);
				var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "فاتورة " + bill.BillNo, "Hospital bill " + bill.BillNo, "HospitalBill", bill.ID, lines, ct);
				if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
				bill.JournalEntryId = entryId;
			}
			if (bill.CurrencyId != null && bill.ExchangeRate == null && _pricing != null)
			{
				var (rok, r) = await _pricing.RateToFacilityAsync(ctx, bill.CurrencyId, nowUtc, ct);
				if (rok) bill.ExchangeRate = r;
			}
			bill.Status = HospitalBillStatus.Closed; bill.ClosedAt = nowUtc; bill.ClosedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(bill.ID);
		}

		// ====================================================================================
		// PAYMENTS
		// ====================================================================================

		public async Task<HospitalResult> CollectAsync(BusinessContext ctx, int billId, decimal amount, string method, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			if (!HospitalPaymentMethods.IsKnown(method)) return HospitalResult.Fail("Unknown payment method");
			if (amount <= 0) return HospitalResult.Fail("Enter an amount above zero");
			var shift = await _db.HospitalCashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.CashierEmployeeId == employeeId && s.Status == HospitalShiftStatus.Open, ct);
			if (shift == null) return HospitalResult.Fail("Open your cashier shift before taking a payment");
			var v = await BillAsync(ctx, billId, nowUtc, ct);
			if (v == null) return HospitalResult.Fail("not_found");
			if (amount > v.Totals.Due) return HospitalResult.Fail("The amount is more than the patient owes ({0})", v.Totals.Due.ToString("0.###"));
			var s = await SettingsAsync(ctx, ct);
			int? debit = method == HospitalPaymentMethods.Cash ? s.CashAccountId : s.BankAccountId;
			if (debit == null || s.ReceivableAccountId == null) return HospitalResult.Fail("Map the cash, bank and receivable accounts in billing settings");

			// A foreign-currency bill is paid once it is closed: the receivable then stands at the closing rate, the
			// money comes in at today's rate (in the facility's currency), and the difference is an exchange gain or loss.
			decimal amountIn = Math.Round(amount, 4), baseAmount = amountIn, receivable = amountIn;
			if (v.Foreign)
			{
				if (v.Bill.Status != HospitalBillStatus.Closed || v.Bill.ExchangeRate is not decimal closing) return HospitalResult.Fail("A foreign-currency bill is collected after it is closed");
				var (rok, today) = _pricing == null ? (false, 0m) : await _pricing.RateToFacilityAsync(ctx, v.Bill.CurrencyId, nowUtc, ct);
				if (!rok) return HospitalResult.Fail("There is no exchange rate for the bill's currency. Enter one in Accounting first");
				int dec = (await _pricing!.FacilityCurrencyAsync(ctx, ct)).Decimals;
				baseAmount = Math.Round(amountIn * today, dec, MidpointRounding.AwayFromZero);
				receivable = Math.Round(amountIn * closing, dec, MidpointRounding.AwayFromZero);
				if (baseAmount != receivable && s.FxDifferenceAccountId == null) return HospitalResult.Fail("Map the exchange-difference account in billing settings");
			}
			var postLines = new List<HospitalLedgerLine>();

			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			var pay = new HospitalPayment
			{
				CompanyId = ctx.CompanyId, BillId = v.Bill.ID, ShiftId = shift.ID, Amount = amountIn, Method = method, ReceivedAt = nowUtc, CashierEmployeeId = employeeId,
				BaseAmount = v.Foreign ? baseAmount : null,
				ReceiptNo = await NextNumberAsync(_db.HospitalPayments.Where(p => p.CompanyId == ctx.CompanyId).Select(p => p.ReceiptNo), "RCPT-", ct),
			};
			_db.HospitalPayments.Add(pay);
			await _db.SaveChangesAsync(ct);
			postLines.Add(new HospitalLedgerLine(debit.Value, baseAmount, 0, pay.ReceiptNo));
			postLines.Add(new HospitalLedgerLine(s.ReceivableAccountId.Value, 0, receivable, pay.ReceiptNo));
			if (baseAmount > receivable) postLines.Add(new HospitalLedgerLine(s.FxDifferenceAccountId!.Value, 0, baseAmount - receivable, pay.ReceiptNo));
			else if (baseAmount < receivable) postLines.Add(new HospitalLedgerLine(s.FxDifferenceAccountId!.Value, receivable - baseAmount, 0, pay.ReceiptNo));
			var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "إيصال " + pay.ReceiptNo + " — " + v.Bill.BillNo, "Receipt " + pay.ReceiptNo + " — " + v.Bill.BillNo,
				"HospitalPayment", pay.ID, postLines, ct);
			if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
			pay.JournalEntryId = entryId;
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(pay.ID);
		}

		// ====================================================================================
		// CASHIER SHIFTS
		// ====================================================================================

		/// The notes and coins a till is counted in, per currency (largest first). Unknown currencies
		/// get a generic set.
		public static IReadOnlyList<decimal> Denominations(string? currencyCode) => (currencyCode ?? "").ToUpperInvariant() switch
		{
			"KWD" => new[] { 20m, 10m, 5m, 1m, 0.5m, 0.25m, 0.1m, 0.05m, 0.02m, 0.01m, 0.005m },
			"SAR" or "AED" => new[] { 500m, 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.5m, 0.25m },
			"EGP" => new[] { 200m, 100m, 50m, 20m, 10m, 5m, 1m, 0.5m, 0.25m },
			"JOD" => new[] { 50m, 20m, 10m, 5m, 1m, 0.5m, 0.25m, 0.1m, 0.05m },
			_ => new[] { 100m, 50m, 20m, 10m, 5m, 1m, 0.5m, 0.25m, 0.1m },
		};

		public Task<HospitalCashierShift?> OpenShiftOfAsync(BusinessContext ctx, int employeeId, CancellationToken ct = default)
			=> _db.HospitalCashierShifts.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == ctx.CompanyId && s.CashierEmployeeId == employeeId && s.Status == HospitalShiftStatus.Open, ct);

		public async Task<HospitalResult> OpenShiftAsync(BusinessContext ctx, string desk, decimal openingFloat, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			desk = (desk ?? "").Trim();
			if (desk.Length is 0 or > 100) return HospitalResult.Fail("Name the cashier desk");
			if (openingFloat < 0) return HospitalResult.Fail("A float cannot be negative");
			if (await OpenShiftOfAsync(ctx, employeeId, ct) != null) return HospitalResult.Fail("You already have an open shift");
			var s = new HospitalCashierShift { CompanyId = ctx.CompanyId, Desk = desk, CashierEmployeeId = employeeId, Status = HospitalShiftStatus.Open, OpenedAt = nowUtc, OpeningFloat = Math.Round(openingFloat, 4) };
			_db.HospitalCashierShifts.Add(s);
			try { await _db.SaveChangesAsync(ct); }
			catch (DbUpdateException) { _db.Entry(s).State = EntityState.Detached; return HospitalResult.Fail("You already have an open shift"); }
			return HospitalResult.Success(s.ID);
		}

		public async Task<HospitalShiftSummary?> ShiftSummaryAsync(BusinessContext ctx, int shiftId, CancellationToken ct = default)
		{
			var s = await _db.HospitalCashierShifts.AsNoTracking().FirstOrDefaultAsync(x => x.ID == shiftId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null) return null;
			var pays = await _db.HospitalPayments.AsNoTracking().Where(p => p.CompanyId == ctx.CompanyId && p.ShiftId == s.ID).ToListAsync(ct);
			// The till is counted in the facility's currency: a foreign-currency receipt counts at what was taken.
			var byMethod = HospitalPaymentMethods.All.ToDictionary(m => m, m => pays.Where(p => p.Method == m).Sum(p => p.BaseAmount ?? p.Amount));
			// H143: deposits taken in this shift are money in the drawer, refunds money out; a deposit used on a bill moved no
			// money (method "Deposit", outside All) and is not counted again.
			var (inn, outt, deposits) = await HospitalDepositService.ShiftMovesAsync(_db, ctx.CompanyId, s.ID, ct);
			foreach (var m in HospitalPaymentMethods.All) byMethod[m] += inn.GetValueOrDefault(m) - outt.GetValueOrDefault(m);
			var cash = byMethod[HospitalPaymentMethods.Cash];
			return new HospitalShiftSummary(s, byMethod, cash, s.OpeningFloat + cash, pays.Count(p => p.Method != HospitalPaymentMethods.Deposit) + deposits);
		}

		public async Task<IReadOnlyList<HospitalShiftSummary>> ShiftsOfDayAsync(BusinessContext ctx, DateTime dayStartUtc, CancellationToken ct = default)
		{
			var ids = await _db.HospitalCashierShifts.AsNoTracking()
				.Where(s => s.CompanyId == ctx.CompanyId && (s.OpenedAt >= dayStartUtc || s.Status == HospitalShiftStatus.Open))
				.OrderByDescending(s => s.OpenedAt).Select(s => s.ID).ToListAsync(ct);
			var list = new List<HospitalShiftSummary>();
			foreach (var id in ids) list.Add((await ShiftSummaryAsync(ctx, id, ct))!);
			return list;
		}

		/// Counts and closes a shift. `counts` maps a denomination to how many were counted.
		public async Task<HospitalResult> CloseShiftAsync(BusinessContext ctx, int shiftId, IReadOnlyDictionary<decimal, int> counts, string? handoverTo, int employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Bill, ct)) return HospitalResult.Fail("forbidden");
			var s = await _db.HospitalCashierShifts.FirstOrDefaultAsync(x => x.ID == shiftId && x.CompanyId == ctx.CompanyId, ct);
			if (s == null) return HospitalResult.Fail("not_found");
			if (s.Status != HospitalShiftStatus.Open) return HospitalResult.Fail("This shift is already closed");
			if (s.CashierEmployeeId != employeeId && !await Can(ctx, HospitalActions.Manage, ct)) return HospitalResult.Fail("Only the shift's cashier or a supervisor closes it");
			if (counts.Any(c => c.Value < 0 || c.Key <= 0)) return HospitalResult.Fail("A count cannot be negative");

			var summary = (await ShiftSummaryAsync(ctx, s.ID, ct))!;
			decimal counted = Math.Round(counts.Sum(c => c.Key * c.Value), 4);
			decimal variance = counted - summary.Expected;
			await using var tx = await _db.Database.BeginTransactionAsync(ct);
			if (variance != 0)
			{
				var set = await SettingsAsync(ctx, ct);
				if (set.CashAccountId == null || set.CashOverShortAccountId == null) return HospitalResult.Fail("Map the cash and cash over/short accounts in billing settings");
				var abs = Math.Abs(variance);
				var lines = variance > 0
					? new[] { new HospitalLedgerLine(set.CashAccountId.Value, abs, 0), new HospitalLedgerLine(set.CashOverShortAccountId.Value, 0, abs) }
					: new[] { new HospitalLedgerLine(set.CashOverShortAccountId.Value, abs, 0), new HospitalLedgerLine(set.CashAccountId.Value, 0, abs) };
				var (ok, error, entryId) = await _ledger.PostAsync(ctx, nowUtc, "فرق خزينة — " + s.Desk, "Cash over/short — " + s.Desk, "HospitalShift", s.ID, lines, ct);
				if (!ok) return HospitalResult.Fail("The ledger refused the posting: {0}", error ?? "");
				s.JournalEntryId = entryId;
			}
			s.Status = HospitalShiftStatus.Closed; s.ClosedAt = nowUtc; s.ExpectedCash = summary.Expected; s.CountedCash = counted; s.Variance = variance;
			s.CountJson = JsonSerializer.Serialize(counts.Where(c => c.Value > 0).ToDictionary(c => c.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), c => c.Value));
			s.HandoverTo = string.IsNullOrWhiteSpace(handoverTo) ? null : handoverTo.Trim()[..Math.Min(200, handoverTo.Trim().Length)];
			await _db.SaveChangesAsync(ct);
			await tx.CommitAsync(ct);
			return HospitalResult.Success(s.ID);
		}
	}
}
