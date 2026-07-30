using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	/// A chart-of-accounts node with its children + type info, for tree rendering.
	public class AccountNode
	{
		public int Id { get; set; }
		public int? ParentId { get; set; }
		public string Code { get; set; } = "";
		public string Name { get; set; } = "";
		public string NameEn { get; set; } = "";
		public int AccountTypeId { get; set; }
		public string? TypeCode { get; set; }
		public string? TypeNameAr { get; set; }
		public string? TypeNameEn { get; set; }
		public string NormalBalance { get; set; } = "D";
		public bool IsPostable { get; set; }
		public bool IsActive { get; set; }
		public bool RequireCostCenter { get; set; }
		public string? CashFlowCategory { get; set; }
		public int Level { get; set; }
		public List<AccountNode> Children { get; set; } = new();
	}

	public interface IChartOfAccountsService
	{
		/// Full account tree for a company (ordered by Code), with type info attached.
		Task<List<AccountNode>> GetTreeAsync(int companyId);

		/// Flat list (ordered by Code) — handy for dropdowns / posting screens.
		Task<List<AccountNode>> GetFlatAsync(int companyId, bool postableOnly = false);

		Task<List<AccountType>> GetAccountTypesAsync();
		Task<Account?> GetAsync(int companyId, int id);
		Task<(bool ok, string? error, int? id)> CreateAsync(int companyId, string code, string nameAr, string nameEn,
			int accountTypeId, int? parentId, bool isPostable, bool requireCostCenter, string? cashFlowCategory, int? userId);
		Task<(bool ok, string? error)> UpdateAsync(int companyId, int id, string code, string nameAr, string nameEn,
			bool isPostable, bool isActive, bool requireCostCenter, string? cashFlowCategory, int? userId);
	}

	public class ChartOfAccountsService : IChartOfAccountsService
	{
		private readonly CrossDbContext _context;
		public ChartOfAccountsService(CrossDbContext context) { _context = context; }

		private async Task<(List<Account> accounts, Dictionary<int, AccountType> types)> LoadAsync(int companyId)
		{
			var accounts = await _context.Accounts.AsNoTracking()
				.Where(a => a.CompanyID == companyId)
				.OrderBy(a => a.Code)
				.ToListAsync();
			var types = await _context.AccountTypes.AsNoTracking().ToDictionaryAsync(t => t.ID);
			return (accounts, types);
		}

		private AccountNode Map(Account a, Dictionary<int, AccountType> types)
		{
			types.TryGetValue(a.AccountTypeId, out var t);
			return new AccountNode
			{
				Id = a.ID, ParentId = a.ParentId, Code = a.Code, Name = a.Name, NameEn = a.NameEn,
				AccountTypeId = a.AccountTypeId,
				TypeCode = t?.Code, TypeNameAr = t?.Name, TypeNameEn = t?.NameEn,
				NormalBalance = t?.NormalBalance ?? "D",
				IsPostable = a.IsPostable, IsActive = a.IsActive,
				RequireCostCenter = a.RequireCostCenter, CashFlowCategory = a.CashFlowCategory,
			};
		}

		public async Task<List<AccountNode>> GetTreeAsync(int companyId)
		{
			var (accounts, types) = await LoadAsync(companyId);
			var nodes = accounts.ToDictionary(a => a.ID, a => Map(a, types));

			var roots = new List<AccountNode>();
			foreach (var a in accounts)
			{
				var node = nodes[a.ID];
				if (a.ParentId.HasValue && nodes.TryGetValue(a.ParentId.Value, out var parent))
					parent.Children.Add(node);
				else
					roots.Add(node);
			}

			// stamp depth for indentation
			void SetLevel(AccountNode n, int lvl)
			{
				n.Level = lvl;
				foreach (var c in n.Children) SetLevel(c, lvl + 1);
			}
			foreach (var r in roots) SetLevel(r, 0);

			return roots;
		}

		public async Task<List<AccountNode>> GetFlatAsync(int companyId, bool postableOnly = false)
		{
			var (accounts, types) = await LoadAsync(companyId);
			return accounts
				.Where(a => !postableOnly || a.IsPostable)
				.Select(a => Map(a, types))
				.ToList();
		}

		public async Task<List<AccountType>> GetAccountTypesAsync() =>
			await _context.AccountTypes.AsNoTracking().OrderBy(t => t.ID).ToListAsync();

		public async Task<Account?> GetAsync(int companyId, int id) =>
			await _context.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);

		public async Task<(bool ok, string? error, int? id)> CreateAsync(int companyId, string code, string nameAr, string nameEn,
			int accountTypeId, int? parentId, bool isPostable, bool requireCostCenter, string? cashFlowCategory, int? userId)
		{
			if (string.IsNullOrWhiteSpace(code)) return (false, "كود الحساب مطلوب", null);
			if (string.IsNullOrWhiteSpace(nameAr)) return (false, "اسم الحساب مطلوب", null);
			if (!await _context.AccountTypes.AnyAsync(t => t.ID == accountTypeId)) return (false, "نوع الحساب غير صحيح", null);
			if (await _context.Accounts.AnyAsync(a => a.CompanyID == companyId && a.Code == code)) return (false, $"الكود {code} مستخدم بالفعل", null);
			if (parentId.HasValue && !await _context.Accounts.AnyAsync(a => a.ID == parentId.Value && a.CompanyID == companyId)) return (false, "الحساب الأب غير موجود", null);

			var acc = new Account
			{
				CompanyID = companyId, Code = code.Trim(), Name = nameAr.Trim(), NameEn = string.IsNullOrWhiteSpace(nameEn) ? nameAr.Trim() : nameEn.Trim(),
				AccountTypeId = accountTypeId, ParentId = parentId, IsPostable = isPostable, IsActive = true,
				RequireCostCenter = requireCostCenter, CashFlowCategory = cashFlowCategory, CreatedBy = userId, CreatedAt = DateTime.UtcNow,
			};
			_context.Accounts.Add(acc);
			await _context.SaveChangesAsync();
			return (true, null, acc.ID);
		}

		public async Task<(bool ok, string? error)> UpdateAsync(int companyId, int id, string code, string nameAr, string nameEn,
			bool isPostable, bool isActive, bool requireCostCenter, string? cashFlowCategory, int? userId)
		{
			var acc = await _context.Accounts.FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);
			if (acc == null) return (false, "الحساب غير موجود");
			if (string.IsNullOrWhiteSpace(code)) return (false, "كود الحساب مطلوب");
			if (await _context.Accounts.AnyAsync(a => a.CompanyID == companyId && a.Code == code && a.ID != id)) return (false, $"الكود {code} مستخدم بالفعل");
			// can't make a header (with children) postable-incompatible silently; allow flag but warn-free
			var hasChildren = await _context.Accounts.AnyAsync(a => a.ParentId == id);
			if (hasChildren && isPostable) return (false, "لا يمكن جعل حساب له فروع قابلًا للترحيل");

			acc.Code = code.Trim(); acc.Name = nameAr.Trim(); acc.NameEn = string.IsNullOrWhiteSpace(nameEn) ? nameAr.Trim() : nameEn.Trim();
			acc.IsPostable = isPostable; acc.IsActive = isActive; acc.RequireCostCenter = requireCostCenter;
			acc.CashFlowCategory = cashFlowCategory; acc.ModifiedBy = userId; acc.ModifiedAt = DateTime.UtcNow;
			await _context.SaveChangesAsync();
			return (true, null);
		}
	}
}
