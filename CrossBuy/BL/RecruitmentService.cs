using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// R2 — one row of the required-documents checklist for an application: the catalog type + the attached doc (if any).
	public class ChecklistItem
	{
		public RequiredDocumentType Type { get; set; } = null!;
		public ApplicationDocument? Doc { get; set; }
		public bool Present => Doc != null;
	}

	// Recruitment R0 — required-document catalog CRUD. Grows in R1–R3 (applications, checklist, hire conversion).
	public class RequiredDocTypeDto
	{
		public int ID { get; set; }
		public string NameAr { get; set; } = "";
		public string? NameEn { get; set; }
		public bool IsMandatory { get; set; } = true;
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public interface IRecruitmentService
	{
		Task<List<RequiredDocumentType>> GetDocTypesAsync(int companyId, bool activeOnly = false);
		Task<RequiredDocTypeDto?> GetDocTypeAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveDocTypeAsync(int companyId, RequiredDocTypeDto dto);
		Task<(bool ok, string? error)> DeleteDocTypeAsync(int companyId, int id);

		// R1 — job applications
		Task<List<JobApplication>> GetApplicationsAsync(int companyId);
		Task<JobApplication?> GetApplicationAsync(int companyId, int id);
		Task<(bool ok, string? error, int id)> SaveApplicationAsync(int companyId, JobApplication input, int? userId);
		Task<(bool ok, string? error)> MoveStageAsync(int companyId, int id, string stage, int? userId);

		// R2 — application documents + checklist
		Task<List<ApplicationDocument>> GetApplicationDocsAsync(int companyId, int appId);
		Task<List<ChecklistItem>> GetChecklistAsync(int companyId, int appId);
		Task<(bool ok, string? error)> AddApplicationDocAsync(int companyId, int appId, int? reqTypeId, IFormFile? file, string? docNumber, DateTime? issue, DateTime? expiry, string? webRootPath);
		Task<(bool ok, string? error, int appId)> DeleteApplicationDocAsync(int companyId, int docId, string? webRootPath);

		// R3 — link an accepted application to a freshly-created employee (carry docs + mark Hired). Called after EmployeeService creates the employee.
		Task<(bool ok, string? error)> HireFromApplicationAsync(int companyId, int appId, int employeeId);
	}

	// canonical pipeline order — shared by the board + move validation
	public static class RecruitmentStages
	{
		public static readonly string[] Order = { "New", "Review", "Interview", "Accepted", "Rejected", "Hired" };
		public static bool IsValid(string s) => Array.IndexOf(Order, s) >= 0;
	}

	public class RecruitmentService : IRecruitmentService
	{
		private readonly CrossDbContext _db;
		public RecruitmentService(CrossDbContext db) { _db = db; }

		public Task<List<RequiredDocumentType>> GetDocTypesAsync(int companyId, bool activeOnly = false) =>
			_db.RequiredDocumentTypes.AsNoTracking()
				.Where(t => t.CompanyID == companyId && (!activeOnly || t.IsActive))
				.OrderBy(t => t.SortOrder).ThenBy(t => t.ID).ToListAsync();

		public async Task<RequiredDocTypeDto?> GetDocTypeAsync(int companyId, int id)
		{
			var t = await _db.RequiredDocumentTypes.AsNoTracking().FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			return t == null ? null : new RequiredDocTypeDto { ID = t.ID, NameAr = t.Name, NameEn = t.NameEn, IsMandatory = t.IsMandatory, SortOrder = t.SortOrder, IsActive = t.IsActive };
		}

		public async Task<(bool ok, string? error, int id)> SaveDocTypeAsync(int companyId, RequiredDocTypeDto dto)
		{
			if (string.IsNullOrWhiteSpace(dto.NameAr)) return (false, "الاسم العربي مطلوب", 0);
			RequiredDocumentType t;
			if (dto.ID > 0)
			{
				t = await _db.RequiredDocumentTypes.FirstOrDefaultAsync(x => x.ID == dto.ID && x.CompanyID == companyId)
					?? throw new InvalidOperationException("النوع غير موجود");
			}
			else
			{
				t = new RequiredDocumentType { CompanyID = companyId, CreatedAt = DateTime.UtcNow };
				if (dto.SortOrder == 0)
					dto.SortOrder = (await _db.RequiredDocumentTypes.Where(x => x.CompanyID == companyId).Select(x => (int?)x.SortOrder).MaxAsync() ?? 0) + 1;
				_db.RequiredDocumentTypes.Add(t);
			}
			t.Name = dto.NameAr.Trim();
			t.NameEn = string.IsNullOrWhiteSpace(dto.NameEn) ? null : dto.NameEn.Trim();
			t.IsMandatory = dto.IsMandatory;
			t.SortOrder = dto.SortOrder;
			t.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null, t.ID);
		}

		public async Task<(bool ok, string? error)> DeleteDocTypeAsync(int companyId, int id)
		{
			var t = await _db.RequiredDocumentTypes.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (t == null) return (false, "النوع غير موجود");
			// don't hard-break history: if any application document references it, just deactivate
			bool used = await _db.ApplicationDocuments.AnyAsync(d => d.RequiredDocumentTypeID == id);
			if (used) { t.IsActive = false; await _db.SaveChangesAsync(); return (true, null); }
			_db.RequiredDocumentTypes.Remove(t);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- R1: job applications ----
		public Task<List<JobApplication>> GetApplicationsAsync(int companyId) =>
			_db.JobApplications.AsNoTracking()
				.Where(a => a.CompanyID == companyId)
				.OrderByDescending(a => a.ApplicationNo).ToListAsync();

		public Task<JobApplication?> GetApplicationAsync(int companyId, int id) =>
			_db.JobApplications.AsNoTracking().FirstOrDefaultAsync(a => a.ID == id && a.CompanyID == companyId);

		public async Task<(bool ok, string? error, int id)> SaveApplicationAsync(int companyId, JobApplication input, int? userId)
		{
			if (string.IsNullOrWhiteSpace(input.FirstName) || string.IsNullOrWhiteSpace(input.LastName))
				return (false, "الاسم الأول والأخير مطلوبان", 0);

			JobApplication a;
			if (input.ID > 0)
			{
				a = await _db.JobApplications.FirstOrDefaultAsync(x => x.ID == input.ID && x.CompanyID == companyId)
					?? throw new InvalidOperationException("الطلب غير موجود");
				if (a.Status == "Hired") return (false, "لا يمكن تعديل طلب تم تعيينه", 0);
			}
			else
			{
				int nextNo = (await _db.JobApplications.Where(x => x.CompanyID == companyId).Select(x => (int?)x.ApplicationNo).MaxAsync() ?? 0) + 1;
				a = new JobApplication { CompanyID = companyId, ApplicationNo = nextNo, Status = "New", AppliedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.JobApplications.Add(a);
			}
			a.FirstName = input.FirstName.Trim();
			a.LastName = input.LastName.Trim();
			a.FullName = string.IsNullOrWhiteSpace(input.FullName) ? (a.FirstName + " " + a.LastName).Trim() : input.FullName.Trim();
			a.FullNameEn = string.IsNullOrWhiteSpace(input.FullNameEn) ? null : input.FullNameEn.Trim();
			a.Email = input.Email; a.PhoneNumber = input.PhoneNumber; a.Address = input.Address;
			if (!string.IsNullOrWhiteSpace(input.PhotoPath)) a.PhotoPath = input.PhotoPath;   // keep existing photo on edit when none re-uploaded
			a.DateOfBirth = input.DateOfBirth; a.Gender = input.Gender; a.MaritalStatus = input.MaritalStatus;
			a.CountryID = input.CountryID; a.JobTitleID = input.JobTitleID; a.BranchID = input.BranchID;
			a.EmpCompanyID = input.EmpCompanyID; a.DepartmentID = input.DepartmentID; a.EmploymentType = input.EmploymentType;
			a.ExpectedSalary = input.ExpectedSalary; a.Source = input.Source; a.Notes = input.Notes;
			if (input.ID > 0) { a.UpdatedAt = DateTime.UtcNow; a.UpdatedBy = userId; }
			await _db.SaveChangesAsync();
			return (true, null, a.ID);
		}

		public async Task<(bool ok, string? error)> MoveStageAsync(int companyId, int id, string stage, int? userId)
		{
			if (!RecruitmentStages.IsValid(stage)) return (false, "مرحلة غير صحيحة");
			var a = await _db.JobApplications.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (a == null) return (false, "الطلب غير موجود");
			// Hired is reached ONLY via conversion to an employee (R3), never by drag
			if (stage == "Hired") return (false, "التعيين يتم عبر زر «تعيين» فقط");
			if (a.Status == "Hired") return (false, "الطلب معيَّن بالفعل");
			a.Status = stage;
			var now = DateTime.UtcNow;
			if (stage == "Review" && a.ReviewedAt == null) a.ReviewedAt = now;
			if (stage == "Interview" && a.InterviewAt == null) a.InterviewAt = now;
			if (stage == "Accepted" || stage == "Rejected") a.DecisionAt = now;
			a.UpdatedAt = now; a.UpdatedBy = userId;
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ---- R2: application documents + checklist ----
		public Task<List<ApplicationDocument>> GetApplicationDocsAsync(int companyId, int appId) =>
			_db.ApplicationDocuments.AsNoTracking()
				.Where(d => d.Application!.CompanyID == companyId && d.ApplicationID == appId)
				.OrderBy(d => d.ID).ToListAsync();

		// checklist = every active catalog type LEFT JOIN the application's attached docs (first match per type)
		public async Task<List<ChecklistItem>> GetChecklistAsync(int companyId, int appId)
		{
			var types = await _db.RequiredDocumentTypes.AsNoTracking()
				.Where(t => t.CompanyID == companyId && t.IsActive)
				.OrderBy(t => t.SortOrder).ThenBy(t => t.ID).ToListAsync();
			var docs = await _db.ApplicationDocuments.AsNoTracking().Where(d => d.ApplicationID == appId).ToListAsync();
			return types.Select(t => new ChecklistItem { Type = t, Doc = docs.FirstOrDefault(d => d.RequiredDocumentTypeID == t.ID) }).ToList();
		}

		public async Task<(bool ok, string? error)> AddApplicationDocAsync(int companyId, int appId, int? reqTypeId, IFormFile? file, string? docNumber, DateTime? issue, DateTime? expiry, string? webRootPath)
		{
			var app = await _db.JobApplications.AsNoTracking().FirstOrDefaultAsync(a => a.ID == appId && a.CompanyID == companyId);
			if (app == null) return (false, "الطلب غير موجود");
			if (file == null || file.Length == 0) return (false, "اختر ملفًا");
			if (expiry.HasValue && issue.HasValue && expiry < issue) return (false, "تاريخ الانتهاء قبل الإصدار");

			string? docType = "Other";
			if (reqTypeId.HasValue)
			{
				var t = await _db.RequiredDocumentTypes.AsNoTracking().FirstOrDefaultAsync(x => x.ID == reqTypeId.Value && x.CompanyID == companyId);
				if (t == null) return (false, "نوع المستند غير موجود");
				docType = string.IsNullOrWhiteSpace(t.NameEn) ? t.Name : t.NameEn;   // snapshot label → EmployeeDocument.DocType on hire
				// replace an existing doc of the same type (re-upload)
				var prev = await _db.ApplicationDocuments.Where(d => d.ApplicationID == appId && d.RequiredDocumentTypeID == reqTypeId.Value).ToListAsync();
				if (prev.Count > 0) { _db.ApplicationDocuments.RemoveRange(prev); await _db.SaveChangesAsync(); }
			}

			string? path = null;
			if (!string.IsNullOrEmpty(webRootPath))
			{
				var dir = Path.Combine(webRootPath, "uploads", "hr-docs");
				if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
				var name = Guid.NewGuid() + Path.GetExtension(file.FileName);
				using (var s = File.Create(Path.Combine(dir, name))) await file.CopyToAsync(s);
				path = "/uploads/hr-docs/" + name;
			}
			_db.ApplicationDocuments.Add(new ApplicationDocument
			{
				ApplicationID = appId, RequiredDocumentTypeID = reqTypeId, DocType = docType,
				FilePath = path, FileName = Path.GetFileName(file.FileName),
				DocNumber = string.IsNullOrWhiteSpace(docNumber) ? null : docNumber.Trim(),
				IssueDate = issue, ExpiryDate = expiry, UploadedAt = DateTime.UtcNow
			});
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error, int appId)> DeleteApplicationDocAsync(int companyId, int docId, string? webRootPath)
		{
			var doc = await _db.ApplicationDocuments.Include(d => d.Application)
				.FirstOrDefaultAsync(d => d.ID == docId && d.Application!.CompanyID == companyId);
			if (doc == null) return (false, "المستند غير موجود", 0);
			int appId = doc.ApplicationID;
			try
			{
				if (!string.IsNullOrEmpty(webRootPath) && !string.IsNullOrEmpty(doc.FilePath))
				{
					var full = Path.Combine(webRootPath, doc.FilePath.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
					if (File.Exists(full)) File.Delete(full);
				}
			}
			catch { /* ignore fs errors */ }
			_db.ApplicationDocuments.Remove(doc);
			await _db.SaveChangesAsync();
			return (true, null, appId);
		}

		// ---- R3: hire — carry the application's documents onto the new employee + mark Hired (reverse-linked via HiredEmployeeID) ----
		public async Task<(bool ok, string? error)> HireFromApplicationAsync(int companyId, int appId, int employeeId)
		{
			var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.ID == appId && a.CompanyID == companyId);
			if (app == null) return (false, "الطلب غير موجود");
			if (app.Status == "Hired" || app.HiredEmployeeID != null) return (false, "الطلب معيَّن بالفعل");
			if (employeeId <= 0) return (false, "الموظف غير صالح");

			// carry the applicant photo onto the new employee if HR didn't upload a different one during the wizard
			if (!string.IsNullOrWhiteSpace(app.PhotoPath))
			{
				var emp = await _db.Employee.FirstOrDefaultAsync(e => e.ID == employeeId);
				if (emp != null && string.IsNullOrWhiteSpace(emp.ProfileImage)) emp.ProfileImage = app.PhotoPath;
			}

			// carry each application document into the employee's HR vault (same physical file; EmployeeDocument references it)
			var docs = await _db.ApplicationDocuments.AsNoTracking().Where(d => d.ApplicationID == appId).ToListAsync();
			foreach (var d in docs)
				_db.EmployeeDocuments.Add(new EmployeeDocument
				{
					CompanyID = companyId, EmployeeID = employeeId,
					DocType = string.IsNullOrWhiteSpace(d.DocType) ? "Other" : d.DocType,
					DocNumber = d.DocNumber, FilePath = d.FilePath,
					IssueDate = d.IssueDate, ExpiryDate = d.ExpiryDate, CreatedAt = DateTime.UtcNow
				});

			app.HiredEmployeeID = employeeId; app.HiredAt = DateTime.UtcNow; app.Status = "Hired"; app.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
