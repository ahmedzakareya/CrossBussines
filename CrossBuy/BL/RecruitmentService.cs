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
		private readonly CrossBuy.BL.Documents.IPlatformDocumentService? _docs;
		private readonly CrossBuy.BL.Platform.IDocumentStorage? _storage;
		// The platform services are optional so a one-argument construction still compiles; the container always supplies
		// them, and without them no file is stored at all (never a local fallback).
		public RecruitmentService(CrossDbContext db, CrossBuy.BL.Documents.IPlatformDocumentService? docs = null, CrossBuy.BL.Platform.IDocumentStorage? storage = null)
		{ _db = db; _docs = docs; _storage = storage; }
		private CrossBuy.BL.Hr.HrFileStore Files => _docs != null && _storage != null
			? new CrossBuy.BL.Hr.HrFileStore(_db, _docs, _storage)
			: throw new InvalidOperationException("The File Manager is not available, so the file was not stored.");

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
					?? throw new InvalidOperationException("Type not found");
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
			if (t == null) return (false, "Type not found");
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
				return (false, "First and last name are required", 0);

			JobApplication a;
			if (input.ID > 0)
			{
				a = await _db.JobApplications.FirstOrDefaultAsync(x => x.ID == input.ID && x.CompanyID == companyId)
					?? throw new InvalidOperationException("Order not found");
				if (a.Status == "Hired") return (false, "An application that has been hired cannot be edited", 0);
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
			if (!RecruitmentStages.IsValid(stage)) return (false, "Invalid stage");
			var a = await _db.JobApplications.FirstOrDefaultAsync(x => x.ID == id && x.CompanyID == companyId);
			if (a == null) return (false, "Order not found");
			// Hired is reached ONLY via conversion to an employee (R3), never by drag
			if (stage == "Hired") return (false, "Hiring is done only through the Hire button");
			if (a.Status == "Hired") return (false, "The application has already been hired");
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
			if (app == null) return (false, "Order not found");
			if (file == null || file.Length == 0) return (false, "Choose a file");
			if (expiry.HasValue && issue.HasValue && expiry < issue) return (false, "The expiry date is before the issue date");

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

			// The File Manager, on the application (owner rule: every file lives there; nothing under wwwroot).
			string? path, ferr;
			using (var fs = file.OpenReadStream()) (path, ferr) = await Files.StoreApplicantFileAsync(appId, false, file.FileName, fs, file.Length);
			if (path == null) return (false, ferr ?? "The file could not be stored");
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
			if (doc == null) return (false, "Document not found", 0);
			int appId = doc.ApplicationID;
			// The row goes; the File Manager keeps the document as the application's history (the platform has no delete).
			_db.ApplicationDocuments.Remove(doc);
			await _db.SaveChangesAsync();
			return (true, null, appId);
		}

		// ---- R3: hire — carry the application's documents onto the new employee + mark Hired (reverse-linked via HiredEmployeeID) ----
		public async Task<(bool ok, string? error)> HireFromApplicationAsync(int companyId, int appId, int employeeId)
		{
			var app = await _db.JobApplications.FirstOrDefaultAsync(a => a.ID == appId && a.CompanyID == companyId);
			if (app == null) return (false, "Order not found");
			if (app.Status == "Hired" || app.HiredEmployeeID != null) return (false, "The application has already been hired");
			if (employeeId <= 0) return (false, "Invalid employee");

			// The applicant's files are COPIED onto the new employee in the File Manager: the employee owns their own
			// documents (read under employee-view), and the application keeps its originals as its history.
			var files = Files;

			// the photo, if HR didn't upload a different one during the wizard
			if (CrossBuy.BL.Hr.HrFileStore.DocumentIdOf(app.PhotoPath) is long photoDoc)
			{
				var emp = await _db.Employee.FirstOrDefaultAsync(e => e.ID == employeeId && e.EmpCompanyID == companyId);
				if (emp != null && string.IsNullOrWhiteSpace(emp.ProfileImage) && await files.OpenCurrentBytesAsync(companyId, photoDoc) is { } p)
				{
					using var buf = new MemoryStream();
					await using (p.Content) await p.Content.CopyToAsync(buf);
					buf.Position = 0;
					var (url, _) = await files.StorePhotoAsync(companyId, employeeId, buf, buf.Length);
					if (url != null) emp.ProfileImage = url;
				}
			}

			// each document into the employee's HR vault
			var docs = await _db.ApplicationDocuments.AsNoTracking().Where(d => d.ApplicationID == appId).ToListAsync();
			foreach (var d in docs)
			{
				string? copied = null;
				if (CrossBuy.BL.Hr.HrFileStore.DocumentIdOf(d.FilePath) is long srcDoc && await files.OpenCurrentBytesAsync(companyId, srcDoc) is { } f)
				{
					using var buf = new MemoryStream();
					await using (f.Content) await f.Content.CopyToAsync(buf);
					buf.Position = 0;
					(copied, _) = await files.StoreScanAsync(employeeId, d.FileName ?? f.FileName, buf, buf.Length, "Carried from the job application on hire");
				}
				_db.EmployeeDocuments.Add(new EmployeeDocument
				{
					CompanyID = companyId, EmployeeID = employeeId,
					DocType = string.IsNullOrWhiteSpace(d.DocType) ? "Other" : d.DocType,
					DocNumber = d.DocNumber, FilePath = copied,
					IssueDate = d.IssueDate, ExpiryDate = d.ExpiryDate, CreatedAt = DateTime.UtcNow
				});
			}

			app.HiredEmployeeID = employeeId; app.HiredAt = DateTime.UtcNow; app.Status = "Hired"; app.UpdatedAt = DateTime.UtcNow;
			await _db.SaveChangesAsync();
			return (true, null);
		}
	}
}
