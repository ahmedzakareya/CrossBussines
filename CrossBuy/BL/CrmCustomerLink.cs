using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// CRM 3-1: explicit boundary between CRM and the financial Customer master. CRM never touches
	// IReceivableService directly — it links to a Customer (create + enrich) only through this seam,
	// so the CRM module stays isolated and the integration point is one auditable place.
	public interface ICrmCustomerLink
	{
		Task<int> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit);
		Task EnrichAsync(int customerId, string? phone, string? email, string? segment, string? contactPerson);
	}

	public class CrmCustomerLink : ICrmCustomerLink
	{
		private readonly IReceivableService _ar;
		private readonly CrossDbContext _db;
		public CrmCustomerLink(IReceivableService ar, CrossDbContext db) { _ar = ar; _db = db; }

		public async Task<int> CreateCustomerAsync(int companyId, string name, string? nameEn, string? taxNo, decimal? creditLimit)
		{
			var c = await _ar.CreateCustomerAsync(companyId, name, nameEn, taxNo, creditLimit);
			return c.ID;
		}

		public async Task EnrichAsync(int customerId, string? phone, string? email, string? segment, string? contactPerson)
		{
			var c = await _db.Customers.FirstOrDefaultAsync(x => x.ID == customerId);
			if (c == null) return;
			if (!string.IsNullOrWhiteSpace(phone)) c.Phone = phone;
			if (!string.IsNullOrWhiteSpace(email)) c.Email = email;
			if (!string.IsNullOrWhiteSpace(segment)) c.Segment = segment;
			if (!string.IsNullOrWhiteSpace(contactPerson)) c.ContactPerson = contactPerson;
			await _db.SaveChangesAsync();
		}
	}
}
