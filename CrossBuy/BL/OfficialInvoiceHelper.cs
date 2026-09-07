using Microsoft.EntityFrameworkCore;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Context.Accounting;

namespace CrossBuy.BL
{
	// HM-8: guards + read-model for the official A4 invoice. StampCustomer is pure (no DI, no writer coupling) — it validates
	// and sets DISPLAY-ONLY fields on a loaded SalesInvoice; the caller saves. Hardcoded Arabic (BL file convention).
	public static class OfficialInvoiceHelper
	{
		// Stamp the walk-in beneficiary onto the invoice's DISPLAY fields. RULES:
		//   - SET-ONCE: a printed official document's beneficiary must not change after it is issued (like GoodsReceipt.InvoiceId).
		//   - TAX-ZERO ONLY: a TAXED invoice's beneficiary must equal the ledger account holder (a real registered customer),
		//     so a name-on-the-document override is REFUSED when TaxTotal > 0 — the auditor's exact mismatch to prevent.
		//   - No financial field is touched (CustomerId, control account, journal entry, amounts all unchanged).
		public static (bool ok, string? error) StampCustomer(SalesInvoice inv, string? name, string? taxNo, string? by)
		{
			if (inv == null) return (false, "Invoice not found");
			if (string.IsNullOrWhiteSpace(name)) return (false, "Customer name is required");
			if (!string.IsNullOrWhiteSpace(inv.CustomerNameOverride))
				return (false, "The customer details are already stamped on the invoice and cannot be changed (a one-time stamp)");
			if (inv.TaxTotal > 0m)
				return (false, "The invoice carries tax — it requires a registered customer, not just a name on the document (the beneficiary must match the account holder in the ledger)");
			inv.CustomerNameOverride = name.Trim();
			inv.CustomerTaxNoOverride = string.IsNullOrWhiteSpace(taxNo) ? null : taxNo.Trim();
			inv.CustomerOverrideBy = by;
			inv.CustomerOverrideAt = DateTime.UtcNow;
			return (true, null);
		}

		// The read-model the print view needs, loaded once (AsNoTracking) — same for both the accounting and the POS action.
		public sealed class PrintData
		{
			public Companies? Company;
			public Customer? Customer;
			public int Dp = 2;                 // document-currency decimals from Currency.DecimalPlaces (3 for KWD, 2 for EGP) — never hardcoded
			public string CurrencyCode = "";
			public Dictionary<int, string> Uoms = new();
		}

		public static async Task<PrintData> LoadPrintDataAsync(CrossDbContext db, SalesInvoice inv, bool isAr)
		{
			var d = new PrintData();
			d.Company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.CompanyID == inv.CompanyID);
			d.Customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.ID == inv.CustomerId);
			if (inv.CurrencyId != null)
			{
				var cur = await db.Currencies.AsNoTracking().FirstOrDefaultAsync(c => c.ID == inv.CurrencyId);
				if (cur != null) { d.Dp = cur.DecimalPlaces; d.CurrencyCode = cur.Code; }
			}
			var uomIds = inv.Lines.Where(l => l.UoMId != null).Select(l => l.UoMId!.Value).Distinct().ToList();
			if (uomIds.Count > 0)
				d.Uoms = await db.UnitsOfMeasure.AsNoTracking().Where(u => uomIds.Contains(u.ID))
					.ToDictionaryAsync(u => u.ID, u => isAr ? u.Name : (string.IsNullOrWhiteSpace(u.NameEn) ? u.Name : u.NameEn));
			return d;
		}
	}
}
