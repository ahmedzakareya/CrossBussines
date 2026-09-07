using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// A contract end-date or document expiry approaching/past (HR-5 alerts).
	public class ExpiryItem
	{
		public string Kind { get; set; } = "";       // Contract | Document
		public int RefId { get; set; }
		public int EmployeeId { get; set; }
		public string? EmployeeName { get; set; }
		public string Label { get; set; } = "";       // contract type / document type
		public DateTime Date { get; set; }            // end/expiry date
		public int DaysLeft { get; set; }             // negative = already expired
		public int FileCount { get; set; }            // number of attachments linked to this record
	}

	public interface IHrDocumentService
	{
		Task<List<EmploymentContract>> GetContractsAsync(int companyId, int employeeId);
		Task<(bool ok, string? error)> SaveContractAsync(EmploymentContract c, IEnumerable<IFormFile>? files, string? webRootPath);
		Task DeleteContractAsync(int companyId, int id);
		Task<List<EmployeeDocument>> GetDocumentsAsync(int companyId, int employeeId);
		Task<(bool ok, string? error)> SaveDocumentAsync(EmployeeDocument d, IEnumerable<IFormFile>? files, string? webRootPath);
		Task DeleteDocumentAsync(int companyId, int id);
		Task<List<ExpiryItem>> ExpiringAsync(int companyId, int daysAhead);
		Task<int> NotifyExpiringAsync(int companyId, int daysAhead);
		// Attachments (many per record)
		Task<List<HrDocumentAttachment>> GetAttachmentsAsync(int companyId, string ownerKind, int ownerId);
		Task<Dictionary<int, int>> GetAttachmentCountsAsync(int companyId, string ownerKind, IEnumerable<int> ownerIds);
		Task AddFilesAsync(int companyId, string ownerKind, int ownerId, IEnumerable<IFormFile>? files, string? webRootPath);
		Task<(bool ok, int employeeId)> DeleteAttachmentAsync(int companyId, int id, string? webRootPath);
	}

	// HR-5: employee contracts + a classified document vault + expiry alerts.
	public class HrDocumentService : IHrDocumentService
	{
		private readonly CrossDbContext _context;
		private readonly INotificationService _notifications;
		public HrDocumentService(CrossDbContext context, INotificationService notifications)
		{ _context = context; _notifications = notifications; }

		private static async Task<string?> SaveFileAsync(IFormFile? file, string? webRootPath)
		{
			if (file == null || file.Length == 0 || string.IsNullOrEmpty(webRootPath)) return null;
			var dir = Path.Combine(webRootPath, "uploads", "hr-docs");
			if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
			var name = Guid.NewGuid() + Path.GetExtension(file.FileName);
			using (var s = File.Create(Path.Combine(dir, name))) await file.CopyToAsync(s);
			return "/uploads/hr-docs/" + name;
		}

		// Persist every uploaded file as an attachment row linked to the given record.
		private async Task AddAttachmentsAsync(int companyId, string ownerKind, int ownerId, IEnumerable<IFormFile>? files, string? webRootPath)
		{
			if (files == null) return;
			foreach (var f in files)
			{
				var path = await SaveFileAsync(f, webRootPath);
				if (path == null) continue;
				_context.HrDocumentAttachments.Add(new HrDocumentAttachment
				{
					CompanyID = companyId, OwnerKind = ownerKind, OwnerID = ownerId,
					FilePath = path, FileName = Path.GetFileName(f.FileName), UploadedAt = DateTime.UtcNow
				});
			}
			await _context.SaveChangesAsync();
		}

		public Task<List<EmploymentContract>> GetContractsAsync(int companyId, int employeeId) =>
			_context.EmploymentContracts.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.EmployeeID == employeeId)
				.OrderByDescending(c => c.StartDate).ToListAsync();

		public async Task<(bool ok, string? error)> SaveContractAsync(EmploymentContract c, IEnumerable<IFormFile>? files, string? webRootPath)
		{
			if (c.EndDate.HasValue && c.EndDate.Value < c.StartDate) return (false, "The contract end date is before its start date");
			var existing = c.ID > 0 ? await _context.EmploymentContracts.FirstOrDefaultAsync(x => x.ID == c.ID && x.CompanyID == c.CompanyID) : null;
			int ownerId;
			if (existing == null)
			{
				c.CreatedAt = DateTime.UtcNow;
				_context.EmploymentContracts.Add(c);
				await _context.SaveChangesAsync();
				ownerId = c.ID;
			}
			else
			{
				existing.ContractType = c.ContractType; existing.StartDate = c.StartDate; existing.EndDate = c.EndDate;
				existing.Status = c.Status; existing.Notes = c.Notes;
				await _context.SaveChangesAsync();
				ownerId = existing.ID;
			}
			await AddAttachmentsAsync(c.CompanyID, "Contract", ownerId, files, webRootPath);
			return (true, null);
		}

		public async Task DeleteContractAsync(int companyId, int id)
		{
			var c = await _context.EmploymentContracts.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (c != null)
			{
				var atts = await _context.HrDocumentAttachments.Where(a => a.CompanyID == companyId && a.OwnerKind == "Contract" && a.OwnerID == id).ToListAsync();
				_context.HrDocumentAttachments.RemoveRange(atts);
				_context.EmploymentContracts.Remove(c);
				await _context.SaveChangesAsync();
			}
		}

		public Task<List<EmployeeDocument>> GetDocumentsAsync(int companyId, int employeeId) =>
			_context.EmployeeDocuments.AsNoTracking()
				.Where(d => d.CompanyID == companyId && d.EmployeeID == employeeId)
				.OrderBy(d => d.DocType).ToListAsync();

		public async Task<(bool ok, string? error)> SaveDocumentAsync(EmployeeDocument d, IEnumerable<IFormFile>? files, string? webRootPath)
		{
			if (d.ExpiryDate.HasValue && d.IssueDate.HasValue && d.ExpiryDate.Value < d.IssueDate.Value) return (false, "The expiry date is before the issue date");
			var existing = d.ID > 0 ? await _context.EmployeeDocuments.FirstOrDefaultAsync(x => x.ID == d.ID && x.CompanyID == d.CompanyID) : null;
			int ownerId;
			if (existing == null)
			{
				d.CreatedAt = DateTime.UtcNow;
				_context.EmployeeDocuments.Add(d);
				await _context.SaveChangesAsync();
				ownerId = d.ID;
			}
			else
			{
				existing.DocType = d.DocType; existing.DocNumber = d.DocNumber; existing.IssueDate = d.IssueDate;
				existing.ExpiryDate = d.ExpiryDate; existing.Notes = d.Notes;
				await _context.SaveChangesAsync();
				ownerId = existing.ID;
			}
			await AddAttachmentsAsync(d.CompanyID, "Document", ownerId, files, webRootPath);
			return (true, null);
		}

		public async Task DeleteDocumentAsync(int companyId, int id)
		{
			var d = await _context.EmployeeDocuments.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (d != null)
			{
				var atts = await _context.HrDocumentAttachments.Where(a => a.CompanyID == companyId && a.OwnerKind == "Document" && a.OwnerID == id).ToListAsync();
				_context.HrDocumentAttachments.RemoveRange(atts);
				_context.EmployeeDocuments.Remove(d);
				await _context.SaveChangesAsync();
			}
		}

		public Task AddFilesAsync(int companyId, string ownerKind, int ownerId, IEnumerable<IFormFile>? files, string? webRootPath) =>
			AddAttachmentsAsync(companyId, ownerKind == "Contract" ? "Contract" : "Document", ownerId, files, webRootPath);

		public Task<List<HrDocumentAttachment>> GetAttachmentsAsync(int companyId, string ownerKind, int ownerId) =>
			_context.HrDocumentAttachments.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.OwnerKind == ownerKind && a.OwnerID == ownerId)
				.OrderBy(a => a.ID).ToListAsync();

		public async Task<Dictionary<int, int>> GetAttachmentCountsAsync(int companyId, string ownerKind, IEnumerable<int> ownerIds)
		{
			var ids = ownerIds.ToList();
			if (ids.Count == 0) return new Dictionary<int, int>();
			return await _context.HrDocumentAttachments.AsNoTracking()
				.Where(a => a.CompanyID == companyId && a.OwnerKind == ownerKind && ids.Contains(a.OwnerID))
				.GroupBy(a => a.OwnerID)
				.Select(g => new { g.Key, Count = g.Count() })
				.ToDictionaryAsync(x => x.Key, x => x.Count);
		}

		public async Task<(bool ok, int employeeId)> DeleteAttachmentAsync(int companyId, int id, string? webRootPath)
		{
			var a = await _context.HrDocumentAttachments.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (a == null) return (false, 0);
			// resolve the owning employee (so the caller can redirect back to the right screen)
			int empId = a.OwnerKind == "Contract"
				? (await _context.EmploymentContracts.Where(c => c.ID == a.OwnerID).Select(c => (int?)c.EmployeeID).FirstOrDefaultAsync()) ?? 0
				: (await _context.EmployeeDocuments.Where(d => d.ID == a.OwnerID).Select(d => (int?)d.EmployeeID).FirstOrDefaultAsync()) ?? 0;
			// best-effort remove the physical file
			try
			{
				if (!string.IsNullOrEmpty(webRootPath) && !string.IsNullOrEmpty(a.FilePath))
				{
					var full = Path.Combine(webRootPath, a.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
					if (File.Exists(full)) File.Delete(full);
				}
			}
			catch { /* ignore file-system errors, still drop the row */ }
			_context.HrDocumentAttachments.Remove(a);
			await _context.SaveChangesAsync();
			return (true, empId);
		}

		public async Task<List<ExpiryItem>> ExpiringAsync(int companyId, int daysAhead)
		{
			var today = DateTime.Today;
			var until = today.AddDays(daysAhead);
			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var names = await _context.Employee.AsNoTracking()
				.Where(e => e.EmpCompanyID == companyId)
				.ToDictionaryAsync(e => e.ID, e => !isAr && !string.IsNullOrWhiteSpace(e.FullNameEn) ? e.FullNameEn : e.FullName);

			var items = new List<ExpiryItem>();

			// contracts with an end date due within the window (or already past) and not terminated
			var contracts = await _context.EmploymentContracts.AsNoTracking()
				.Where(c => c.CompanyID == companyId && c.EndDate != null && c.Status != "Terminated" && c.EndDate <= until)
				.ToListAsync();
			foreach (var c in contracts)
				items.Add(new ExpiryItem { Kind = "Contract", RefId = c.ID, EmployeeId = c.EmployeeID,
					EmployeeName = names.TryGetValue(c.EmployeeID, out var n1) ? n1 : null,
					Label = c.ContractType, Date = c.EndDate!.Value, DaysLeft = (int)(c.EndDate!.Value.Date - today).TotalDays });

			// documents with an expiry due within the window (or already past)
			var docs = await _context.EmployeeDocuments.AsNoTracking()
				.Where(d => d.CompanyID == companyId && d.ExpiryDate != null && d.ExpiryDate <= until)
				.ToListAsync();
			foreach (var d in docs)
				items.Add(new ExpiryItem { Kind = "Document", RefId = d.ID, EmployeeId = d.EmployeeID,
					EmployeeName = names.TryGetValue(d.EmployeeID, out var n2) ? n2 : null,
					Label = d.DocType, Date = d.ExpiryDate!.Value, DaysLeft = (int)(d.ExpiryDate!.Value.Date - today).TotalDays });

			// attachment counts per record (so the UI shows a gallery affordance)
			var cIds = items.Where(i => i.Kind == "Contract").Select(i => i.RefId).ToList();
			var dIds = items.Where(i => i.Kind == "Document").Select(i => i.RefId).ToList();
			var cCounts = await GetAttachmentCountsAsync(companyId, "Contract", cIds);
			var dCounts = await GetAttachmentCountsAsync(companyId, "Document", dIds);
			foreach (var i in items)
				i.FileCount = (i.Kind == "Contract" ? cCounts : dCounts).TryGetValue(i.RefId, out var n) ? n : 0;

			return items.OrderBy(i => i.DaysLeft).ToList();
		}

		public async Task<int> NotifyExpiringAsync(int companyId, int daysAhead)
		{
			var items = await ExpiringAsync(companyId, daysAhead);
			foreach (var i in items)
			{
				var what = i.Kind == "Contract" ? "عقد العمل" : "مستند";
				var whatEn = i.Kind == "Contract" ? "employment contract" : "document";
				var when = i.DaysLeft < 0 ? $"Expired {-i.DaysLeft} day(s) ago" : $"Expires in {i.DaysLeft} day(s)";
				await _notifications.NotifyAsync(i.EmployeeId,
					$"تنبيه انتهاء: {what}", $"Expiry alert: {whatEn}",
					$"{i.Label} — {when} ({i.Date:yyyy-MM-dd})", $"{i.Label} — expires {i.Date:yyyy-MM-dd}",
					"hr_expiry", i.RefId);
			}
			return items.Count;
		}
	}
}
