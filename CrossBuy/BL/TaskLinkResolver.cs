using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// TM-2: resolves a task's polymorphic link (EntityType + EntityId) to a display label + a deep-link URL, and powers the
	// record picker (types + search). Read-only — NO GL/stock. Types with a real per-id detail screen get a deep link;
	// types with only a list screen link to the list; a type with no screen returns Url = null (label shown, no "open").
	public class TaskLinkTypeDto { public string Key { get; set; } = ""; public string LabelAr { get; set; } = ""; public string LabelEn { get; set; } = ""; public string Icon { get; set; } = ""; }
	public class TaskLinkOptionDto { public int Id { get; set; } public string Label { get; set; } = ""; }
	public class TaskLinkDto { public string TypeLabelAr { get; set; } = ""; public string TypeLabelEn { get; set; } = ""; public string Label { get; set; } = ""; public string? Url { get; set; } public string Icon { get; set; } = ""; }

	public interface ITaskLinkResolver
	{
		List<TaskLinkTypeDto> Types();
		Task<List<TaskLinkOptionDto>> SearchAsync(int companyId, string entityType, string? term);
		Task<TaskLinkDto?> ResolveAsync(int companyId, string? entityType, int? entityId);
		Task<string?> PartyNameAsync(int companyId, string? partyType, int? partyId);   // TM-9: Supplier(Vendor)/Customer name for a scheduled task
	}

	public class TaskLinkResolver : ITaskLinkResolver
	{
		private readonly CrossDbContext _db;
		public TaskLinkResolver(CrossDbContext db) { _db = db; }

		public List<TaskLinkTypeDto> Types() => new()
		{
			new() { Key = "SalesInvoice",   LabelAr = "فاتورة مبيعات", LabelEn = "Sales invoice", Icon = "ki-outline ki-bill" },
			new() { Key = "Customer",       LabelAr = "عميل",          LabelEn = "Customer",      Icon = "ki-outline ki-profile-circle" },
			new() { Key = "ManufWorkOrder", LabelAr = "أمر تشغيل",     LabelEn = "Work order",    Icon = "ki-outline ki-gear" },
			new() { Key = "PosOrder",       LabelAr = "طلب مطعم",      LabelEn = "Restaurant order", Icon = "ki-outline ki-handcart" },
			new() { Key = "Employee",       LabelAr = "موظف",          LabelEn = "Employee",      Icon = "ki-outline ki-user" },
			new() { Key = "Project",        LabelAr = "مشروع",         LabelEn = "Project",       Icon = "ki-outline ki-abstract-26" },
			new() { Key = "Item",           LabelAr = "صنف",           LabelEn = "Item",          Icon = "ki-outline ki-basket" },
		};

		public async Task<List<TaskLinkOptionDto>> SearchAsync(int companyId, string entityType, string? term)
		{
			term = (term ?? "").Trim();
			bool any = term.Length == 0;
			switch (entityType)
			{
				case "SalesInvoice":
					return await _db.SalesInvoices.AsNoTracking().Where(i => i.CompanyID == companyId && (any || (i.InvoiceNo != null && i.InvoiceNo.Contains(term))))
						.OrderByDescending(i => i.ID).Take(20).Select(i => new TaskLinkOptionDto { Id = i.ID, Label = (i.InvoiceNo ?? ("#" + i.ID)) }).ToListAsync();
				case "Customer":
					return await _db.Customers.AsNoTracking().Where(c => c.CompanyID == companyId && (any || c.Name.Contains(term)))
						.OrderBy(c => c.Name).Take(20).Select(c => new TaskLinkOptionDto { Id = c.ID, Label = c.Name }).ToListAsync();
					case "Supplier":   // TM-9: vendor picker for scheduled purchase-invoice tasks
						return await _db.Vendors.AsNoTracking().Where(v => v.CompanyID == companyId && (any || v.Name.Contains(term)))
							.OrderBy(v => v.Name).Take(20).Select(v => new TaskLinkOptionDto { Id = v.ID, Label = v.Name }).ToListAsync();
				case "ManufWorkOrder":
					return await _db.ManufWorkOrders.AsNoTracking().Where(w => w.CompanyID == companyId && (any || (w.WoNo != null && w.WoNo.Contains(term))))
						.OrderByDescending(w => w.ID).Take(20).Select(w => new TaskLinkOptionDto { Id = w.ID, Label = (w.WoNo ?? ("#" + w.ID)) }).ToListAsync();
				case "PosOrder":
					return await _db.PosOrders.AsNoTracking().Where(o => o.CompanyId == companyId && (any || (o.ReceiptNo != null && o.ReceiptNo.Contains(term))))
						.OrderByDescending(o => o.ID).Take(20).Select(o => new TaskLinkOptionDto { Id = o.ID, Label = (o.ReceiptNo ?? ("#" + o.ID)) }).ToListAsync();
				case "Employee":
					return await _db.Employee.AsNoTracking().Where(e => e.FullName != null && e.FullName != "" && (any || e.FullName.Contains(term)))
						.OrderBy(e => e.FullName).Take(20).Select(e => new TaskLinkOptionDto { Id = e.ID, Label = e.FullName! }).ToListAsync();
				case "Project":
					return await _db.Projects.AsNoTracking().Where(p => p.CompanyID == companyId && (any || p.Name.Contains(term) || p.Code.Contains(term)))
						.OrderBy(p => p.Name).Take(20).Select(p => new TaskLinkOptionDto { Id = p.ID, Label = (p.Code + " — " + p.Name) }).ToListAsync();
				case "Item":
					return await _db.Items.AsNoTracking().Where(i => i.CompanyID == companyId && (any || i.Name.Contains(term) || (i.ItemCode != null && i.ItemCode.Contains(term))))
						.OrderBy(i => i.Name).Take(20).Select(i => new TaskLinkOptionDto { Id = i.ID, Label = ((i.ItemCode ?? "") + " — " + i.Name) }).ToListAsync();
				default:
					return new();
			}
		}

		public async Task<TaskLinkDto?> ResolveAsync(int companyId, string? entityType, int? entityId)
		{
			if (string.IsNullOrWhiteSpace(entityType) || entityId == null || entityId <= 0) return null;
			var type = Types().FirstOrDefault(t => t.Key == entityType);
			if (type == null) return null;
			string? label = null, url = null;
			int id = entityId.Value;
			switch (entityType)
			{
				case "SalesInvoice":
					label = await _db.SalesInvoices.AsNoTracking().Where(i => i.ID == id && i.CompanyID == companyId).Select(i => i.InvoiceNo ?? ("#" + i.ID)).FirstOrDefaultAsync();
					if (label != null) url = $"/Accounting/SalesInvoiceDetail?id={id}";
					break;
				case "Customer":
					label = await _db.Customers.AsNoTracking().Where(c => c.ID == id && c.CompanyID == companyId).Select(c => c.Name).FirstOrDefaultAsync();
					if (label != null) url = $"/Accounting/CustomerStatement?id={id}";
					break;
				case "ManufWorkOrder":
					label = await _db.ManufWorkOrders.AsNoTracking().Where(w => w.ID == id && w.CompanyID == companyId).Select(w => w.WoNo ?? ("#" + w.ID)).FirstOrDefaultAsync();
					if (label != null) url = $"/Inventory/WorkOrderDetails?id={id}";
					break;
				case "Item":
					label = await _db.Items.AsNoTracking().Where(i => i.ID == id && i.CompanyID == companyId).Select(i => (i.ItemCode ?? "") + " — " + i.Name).FirstOrDefaultAsync();
					if (label != null) url = $"/Inventory/EditItem?id={id}";
					break;
				case "Employee":
					label = await _db.Employee.AsNoTracking().Where(e => e.ID == id).Select(e => e.FullName).FirstOrDefaultAsync();
					if (label != null) url = "/Admin/EmployeesList";   // no per-id detail screen → open the employees list
					break;
				case "Project":
					label = await _db.Projects.AsNoTracking().Where(p => p.ID == id && p.CompanyID == companyId).Select(p => p.Code + " — " + p.Name).FirstOrDefaultAsync();
					if (label != null) url = "/Project/Projects";       // no per-id detail screen → open the projects list
					break;
				case "PosOrder":
					label = await _db.PosOrders.AsNoTracking().Where(o => o.ID == id && o.CompanyId == companyId).Select(o => o.ReceiptNo ?? ("#" + o.ID)).FirstOrDefaultAsync();
					url = null;   // no admin detail screen for a POS order yet → reference-only (label shown, no "open")
					break;
			}
			if (label == null) return new TaskLinkDto { TypeLabelAr = type.LabelAr, TypeLabelEn = type.LabelEn, Icon = type.Icon, Label = "#" + id, Url = null };  // record deleted → safe
			return new TaskLinkDto { TypeLabelAr = type.LabelAr, TypeLabelEn = type.LabelEn, Icon = type.Icon, Label = label, Url = url };
		}

		// TM-9: resolve a scheduled task's expected party (Vendor/Customer) name for display. Read-only.
		public async Task<string?> PartyNameAsync(int companyId, string? partyType, int? partyId)
		{
			if (partyId == null || partyId <= 0 || string.IsNullOrWhiteSpace(partyType)) return null;
			int id = partyId.Value;
			if (partyType == "Supplier")
				return await _db.Vendors.AsNoTracking().Where(v => v.ID == id && v.CompanyID == companyId).Select(v => v.Name).FirstOrDefaultAsync();
			if (partyType == "Customer")
				return await _db.Customers.AsNoTracking().Where(c => c.ID == id && c.CompanyID == companyId).Select(c => c.Name).FirstOrDefaultAsync();
			return null;
		}
	}
}
