using System.Text.Json;
using CrossBuy.Models.Context;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// Accounting RBAC. Roles: ChiefAccountant (full + approve + close), Accountant (post journals/invoices),
	/// Cashier (receipts/payments/bank only), Auditor (read-only). No role configured company-wide → open (bootstrap).
	public interface IAccountingAccessService
	{
		int? CurrentEmployeeId();
		Task<List<string>> MyRolesAsync();
		Task<bool> CanAsync(string action);            // read | post | pay | manage | currency-override
		Task<string> RoleLabelAsync(bool isAr);
	}

	public class AccountingAccessService : IAccountingAccessService
	{
		private const int CompanyId = 1;
		private readonly CrossDbContext _db;
		private readonly IHttpContextAccessor _http;
		public AccountingAccessService(CrossDbContext db, IHttpContextAccessor http) { _db = db; _http = http; }

		public int? CurrentEmployeeId()
		{
			var json = _http.HttpContext?.Session.GetString("Employee");
			if (string.IsNullOrEmpty(json)) return null;
			try { return JsonSerializer.Deserialize<EmployeeViewModel>(json)?.ID; } catch { return null; }
		}

		public async Task<List<string>> MyRolesAsync()
		{
			var emp = CurrentEmployeeId();
			if (emp == null) return new();
			return await _db.AccountingUserRoles.AsNoTracking().Where(r => r.CompanyID == CompanyId && r.EmployeeId == emp).Select(r => r.Role).ToListAsync();
		}

		// dormant until at least one accounting role is assigned company-wide (avoids lockout while configuring)
		private Task<bool> AnyRoleConfiguredAsync() => _db.AccountingUserRoles.AnyAsync(r => r.CompanyID == CompanyId);

		public async Task<bool> CanAsync(string action)
		{
			if (!await AnyRoleConfiguredAsync()) return true;   // not configured yet → open
			var roles = await MyRolesAsync();
			bool chief = roles.Contains("ChiefAccountant");
			bool acct = roles.Contains("Accountant");
			bool cashier = roles.Contains("Cashier");
			return action switch
			{
				"read" => true,                          // any authenticated user may view
				"post" => chief || acct,                 // journals, sales/purchase invoices
				"pay" => chief || acct || cashier,       // receipts, payments, bank/cash transfers
				"manage" => chief,                       // period close, year-end, posting rules, roles, approvals
				"currency-override" => chief || acct,    // issue a document in a currency other than the branch's
				_ => false                               // Auditor (or unknown role) → read-only
			};
		}

		public async Task<string> RoleLabelAsync(bool isAr)
		{
			var roles = await MyRolesAsync();
			if (roles.Count == 0) return isAr ? "قارئ" : "Viewer";
			string name(string r) => r switch
			{
				"ChiefAccountant" => isAr ? "رئيس حسابات" : "Chief accountant",
				"Accountant" => isAr ? "محاسب" : "Accountant",
				"Cashier" => isAr ? "أمين صندوق" : "Cashier",
				"Auditor" => isAr ? "مدقّق" : "Auditor",
				_ => r
			};
			return string.Join("، ", roles.Select(name));
		}
	}
}
