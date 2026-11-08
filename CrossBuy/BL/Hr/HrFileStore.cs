using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hr
{
	// ==========================================================================================
	// HR FILES THROUGH THE CENTRAL DOCUMENT PLATFORM (owner rule, restated 2026-11-08: "every document goes to the
	// File Manager, even the pictures — adding them outside is forbidden").
	//
	// Until now HR wrote three stores of its own under wwwroot/uploads: employee photos (/uploads/employees),
	// contract and document scans (/uploads/hr-docs) and applicant photos (/uploads/applicants) — static paths,
	// readable by anyone who knew them. Every HR file is now a document on the EMPLOYEE record:
	//
	//  * THE PHOTO is one document whose Metadata marks it as the photo; a new photo is a new VERSION of it, so the
	//    history stays (the hospital patient photo works the same way). Employee.ProfileImage keeps holding a URL —
	//    /People/Photo/{employeeId}?d=&v= — so every screen that renders ProfileImage keeps working unchanged.
	//    Photos are read by colleagues everywhere (chat, mentions, lists), while the platform's View narrows an
	//    employee's documents to self/team, so the photo route reads the current version itself AFTER checking the
	//    document is this employee's photo in the reader's own company. That is strictly tighter than today, when a
	//    photo under /uploads was public.
	//  * A SCAN (contract or document attachment) is a platform document on the employee; the row keeps the URL
	//    /People/HrFile/{id}. Not /Documents/{id}/content: that surface authorises employee-view ON THE SUBJECT, which
	//    while no HR role is assigned narrows to self/team — HR could no longer open the scans it opens today. The HR
	//    route applies the same module-level employee-view the documents screen applies, plus the company check.
	// ==========================================================================================
	public static class HrFileRules
	{
		public const string PhotoMetadata = "{\"hr\":\"employee-photo\"}";      // the platform takes a JSON object
		public const string ScanMetadata = "{\"hr\":\"attachment\"}";
		public const string ApplicantPhotoMetadata = "{\"hr\":\"applicant-photo\"}";
		public const string ApplicantDocMetadata = "{\"hr\":\"applicant-document\"}";
		public const string LeaveCertificateMetadata = "{\"hr\":\"leave-certificate\"}";
		public const long MaxPhotoBytes = 5 * 1024 * 1024;
		public const long MaxScanBytes = 15 * 1024 * 1024;

		/// The type from the file's own first bytes (JPEG, PNG or WebP), never from its name.
		public static (string Ext, string ContentType)? SniffImage(ReadOnlySpan<byte> h)
		{
			if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0xD8) return (".jpg", "image/jpeg");
			if (h.Length >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return (".png", "image/png");
			if (h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return (".webp", "image/webp");
			// ISO-BMFF "ftyp" box with the avif/avis brand (bytes 4..11)
			if (h.Length >= 12 && h[4] == 0x66 && h[5] == 0x74 && h[6] == 0x79 && h[7] == 0x70 && h[8] == 0x61 && h[9] == 0x76 && h[10] == 0x69 && (h[11] == 0x66 || h[11] == 0x73)) return (".avif", "image/avif");
			return null;
		}

		public static string PhotoUrl(int employeeId, long documentId, long versionId) => $"/People/Photo/{employeeId}?d={documentId}&v={versionId}";
		public static string ScanUrl(long documentId) => $"/People/HrFile/{documentId}";
		public static string ApplicantFileUrl(long documentId) => $"/People/ApplicantFile/{documentId}";
		public static string LeaveCertificateUrl(long documentId) => $"/People/LeaveFile/{documentId}";

		/// A scan's content type from its extension, restricted to what HR actually files.
		/// Null when a scan may be stored; otherwise why not (size, type) — checked before anything is written.
		public static string? CheckScan(string? fileName, long length) =>
			length <= 0 || length > MaxScanBytes ? "The file must be up to 15 MB"
			: ScanContentType(fileName ?? "") == null ? "Only PDF, image or Word files are accepted" : null;

		public static string? ScanContentType(string fileName) => Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
		{
			".pdf" => "application/pdf",
			".jpg" or ".jpeg" => "image/jpeg",
			".png" => "image/png",
			".webp" => "image/webp",
			".avif" => "image/avif",
			".doc" => "application/msword",
			".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
			_ => null,
		};
	}

	public sealed class HrFileStore
	{
		private readonly CrossDbContext _db;
		private readonly IPlatformDocumentService _docs;
		private readonly IDocumentStorage _storage;

		public HrFileStore(CrossDbContext db, IPlatformDocumentService docs, IDocumentStorage storage)
		{ _db = db; _docs = docs; _storage = storage; }

		private Task<PlatformDocument?> PhotoDocAsync(int companyId, int employeeId, CancellationToken ct) =>
			_db.Set<PlatformDocument>().AsNoTracking()
				.Where(d => d.CompanyID == companyId && d.EntityType == EntityRegistry.Employee && d.EntityId == employeeId && d.Metadata == HrFileRules.PhotoMetadata && d.Status == "Active")
				.OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);

		/// Stores the bytes as the employee's photo (a first upload or a new version) and returns the URL for
		/// ProfileImage. The platform authorises it (employee-manage on this employee) and keeps company isolation.
		public async Task<(string? Url, string? Error)> StorePhotoAsync(int companyId, int employeeId, Stream content, long length, CancellationToken ct = default)
		{
			const string bad = "The photo must be a JPEG, PNG or WebP image of up to 5 MB";
			if (employeeId <= 0) return (null, "The employee must be saved before a photo is stored");
			if (length <= 0 || length > HrFileRules.MaxPhotoBytes) return (null, bad);
			using var buffer = new MemoryStream();
			await content.CopyToAsync(buffer, ct);
			var kind = HrFileRules.SniffImage(buffer.GetBuffer().AsSpan(0, (int)Math.Min(12, buffer.Length)));
			if (kind is not { } k) return (null, bad);
			buffer.Position = 0;
			string name = "employee-" + employeeId + k.Ext;
			var existing = await PhotoDocAsync(companyId, employeeId, ct);
			var r = existing != null
				? await _docs.ReplaceAsync(existing.Id, buffer, name, k.ContentType, "New employee photo", ct)
				: await _docs.UploadAsync(new DocumentUploadRequest
				{
					EntityType = EntityRegistry.Employee, EntityId = employeeId, FileName = name, ContentType = k.ContentType,
					Metadata = HrFileRules.PhotoMetadata, Reason = "Employee photo",
				}, buffer, ct);
			if (!r.Ok) return (null, r.ReasonCode);
			return (HrFileRules.PhotoUrl(employeeId, r.DocumentId, r.VersionId), null);
		}

		/// Stores a contract/document scan as a platform document on the employee and returns its delivery URL.
		public async Task<(string? Url, string? Error)> StoreScanAsync(int employeeId, string fileName, Stream content, long length, string reason, CancellationToken ct = default)
		{
			if (employeeId <= 0) return (null, "unknown_employee");
			if (length <= 0 || length > HrFileRules.MaxScanBytes) return (null, "The file must be up to 15 MB");
			var type = HrFileRules.ScanContentType(fileName);
			if (type == null) return (null, "Only PDF, image or Word files are accepted");
			var safeName = Path.GetFileName(fileName);
			var r = await _docs.UploadAsync(new DocumentUploadRequest
			{
				EntityType = EntityRegistry.Employee, EntityId = employeeId, FileName = safeName, ContentType = type,
				Metadata = HrFileRules.ScanMetadata, Reason = reason,
			}, content, ct);
			return r.Ok ? (HrFileRules.ScanUrl(r.DocumentId), null) : (null, r.ReasonCode);
		}

		/// An applicant's photo or document as a platform document on the JOB APPLICATION (the platform authorises the
		/// upload through JobApplicationDocumentOwnerResolver). Returns the delivery URL the row keeps.
		public async Task<(string? Url, string? Error)> StoreApplicantFileAsync(int applicationId, bool isPhoto, string fileName, Stream content, long length, CancellationToken ct = default)
		{
			if (applicationId <= 0) return (null, "unknown_application");
			string type, name;
			MemoryStream? buffer = null;
			try
			{
				Stream body = content;
				if (isPhoto)
				{
					if (length <= 0 || length > HrFileRules.MaxPhotoBytes) return (null, "The photo must be an image of up to 5 MB");
					buffer = new MemoryStream();
					await content.CopyToAsync(buffer, ct);
					var kind = HrFileRules.SniffImage(buffer.GetBuffer().AsSpan(0, (int)Math.Min(12, buffer.Length)));
					if (kind is not { } k) return (null, "The photo must be a JPEG, PNG, WebP or AVIF image");
					buffer.Position = 0; body = buffer; type = k.ContentType; name = "applicant-" + applicationId + k.Ext;
				}
				else
				{
					if (length <= 0 || length > HrFileRules.MaxScanBytes) return (null, "The file must be up to 15 MB");
					type = HrFileRules.ScanContentType(fileName) ?? "";
					if (type.Length == 0) return (null, "Only PDF, image or Word files are accepted");
					name = Path.GetFileName(fileName);
				}
				var r = await _docs.UploadAsync(new DocumentUploadRequest
				{
					EntityType = EntityRegistry.JobApplication, EntityId = applicationId, FileName = name, ContentType = type,
					Metadata = isPhoto ? HrFileRules.ApplicantPhotoMetadata : HrFileRules.ApplicantDocMetadata,
					Reason = isPhoto ? "Applicant photo" : "Applicant document",
				}, body, ct);
				return r.Ok ? (HrFileRules.ApplicantFileUrl(r.DocumentId), null) : (null, r.ReasonCode);
			}
			finally { buffer?.Dispose(); }
		}

		/// HR3: the medical certificate an employee attaches to THEIR OWN leave request, as a platform document on the
		/// LEAVE REQUEST (one per request — a second sick leave's certificate is not a new version of the first). The
		/// platform authorises it through LeaveRequestDocumentOwnerResolver: attaching is employee-request, self-only.
		public async Task<(long? DocumentId, string? Error)> StoreLeaveCertificateAsync(int leaveRequestId, string fileName, Stream content, long length, CancellationToken ct = default)
		{
			if (leaveRequestId <= 0) return (null, "unknown_request");
			var problem = HrFileRules.CheckScan(fileName, length);
			if (problem != null) return (null, problem);
			var r = await _docs.UploadAsync(new DocumentUploadRequest
			{
				EntityType = EntityRegistry.LeaveRequest, EntityId = leaveRequestId, FileName = Path.GetFileName(fileName),
				ContentType = HrFileRules.ScanContentType(fileName)!, Metadata = HrFileRules.LeaveCertificateMetadata, Reason = "Medical certificate",
			}, content, ct);
			return r.Ok ? (r.DocumentId, null) : (null, r.ReasonCode);
		}

		/// The certificate documents of these requests in this company: request id → document id (the latest).
		public async Task<Dictionary<int, long>> LeaveCertificatesAsync(int companyId, IEnumerable<int> requestIds, CancellationToken ct = default)
		{
			var ids = requestIds.Distinct().ToList();
			if (ids.Count == 0) return new();
			var rows = await _db.Set<PlatformDocument>().AsNoTracking()
				.Where(d => d.CompanyID == companyId && d.EntityType == EntityRegistry.LeaveRequest && ids.Contains(d.EntityId)
					&& d.Metadata == HrFileRules.LeaveCertificateMetadata && d.Status == "Active")
				.Select(d => new { d.EntityId, d.Id }).ToListAsync(ct);
			return rows.GroupBy(x => x.EntityId).ToDictionary(g => g.Key, g => g.Max(x => x.Id));
		}

		/// A certificate document of this company: the leave request it belongs to (the caller decides who may read it).
		public Task<int?> LeaveRequestOfCertificateAsync(int companyId, long documentId, CancellationToken ct = default)
			=> _db.Set<PlatformDocument>().AsNoTracking()
				.Where(d => d.Id == documentId && d.CompanyID == companyId && d.EntityType == EntityRegistry.LeaveRequest && d.Metadata == HrFileRules.LeaveCertificateMetadata)
				.Select(d => (int?)d.EntityId).FirstOrDefaultAsync(ct);

		/// An applicant file's current bytes. The CALLER has passed the recruitment gate; this checks the document is an
		/// applicant file of an application in that same company.
		public async Task<(Stream Content, string ContentType, string FileName)?> OpenApplicantFileAsync(int companyId, long documentId, CancellationToken ct = default)
		{
			var ok = await _db.Set<PlatformDocument>().AsNoTracking().AnyAsync(d => d.Id == documentId && d.CompanyID == companyId
				&& d.EntityType == EntityRegistry.JobApplication && (d.Metadata == HrFileRules.ApplicantPhotoMetadata || d.Metadata == HrFileRules.ApplicantDocMetadata), ct);
			return ok ? await OpenCurrentBytesAsync(companyId, documentId, ct) : null;
		}

		/// The current version's bytes of a document in this company, read by its own key: for callers that have already
		/// established the document is theirs to read (the copy-on-hire, the gated routes).
		public async Task<(Stream Content, string ContentType, string FileName)?> OpenCurrentBytesAsync(int companyId, long documentId, CancellationToken ct = default)
		{
			var doc = await _db.Set<PlatformDocument>().AsNoTracking().Where(d => d.Id == documentId && d.CompanyID == companyId)
				.Select(d => new { d.Id, d.CurrentVersionId }).FirstOrDefaultAsync(ct);
			if (doc?.CurrentVersionId is not long vid) return null;
			var v = await _db.Set<PlatformDocumentVersion>().AsNoTracking()
				.Where(x => x.Id == vid && x.CompanyID == companyId && x.DocumentId == doc.Id)
				.Select(x => new { x.StorageKey, x.ContentType, x.FileName }).FirstOrDefaultAsync(ct);
			if (v == null || !StorageKey.TryParse(v.StorageKey, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			return s == null ? null : (s, v.ContentType, v.FileName);
		}

		/// The document id inside one of this store's delivery URLs (/People/HrFile/{id}, /People/ApplicantFile/{id}).
		public static long? DocumentIdOf(string? url)
		{
			if (string.IsNullOrWhiteSpace(url)) return null;
			var tail = url.Split('?')[0].TrimEnd('/');
			var slash = tail.LastIndexOf('/');
			return slash >= 0 && long.TryParse(tail[(slash + 1)..], out var id) ? id : null;
		}

		/// A scan's current bytes. The CALLER has already passed the HR employee-view gate; this checks the document is
		/// an HR scan on an employee of that same company, so an id from a URL never reaches any other file.
		public async Task<(Stream Content, string ContentType, string FileName)?> OpenScanAsync(int companyId, long documentId, CancellationToken ct = default)
		{
			var doc = await _db.Set<PlatformDocument>().AsNoTracking()
				.Where(d => d.Id == documentId && d.CompanyID == companyId && d.EntityType == EntityRegistry.Employee && d.Metadata == HrFileRules.ScanMetadata)
				.Select(d => new { d.Id, d.CurrentVersionId }).FirstOrDefaultAsync(ct);
			if (doc?.CurrentVersionId is not long vid) return null;
			var v = await _db.Set<PlatformDocumentVersion>().AsNoTracking()
				.Where(x => x.Id == vid && x.CompanyID == companyId && x.DocumentId == doc.Id)
				.Select(x => new { x.StorageKey, x.ContentType, x.FileName }).FirstOrDefaultAsync(ct);
			if (v == null || !StorageKey.TryParse(v.StorageKey, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			return s == null ? null : (s, v.ContentType, v.FileName);
		}

		/// The photo's current bytes for any signed-in member of the employee's own company. The document must be THIS
		/// employee's photo in THAT company — an id from a URL never reaches another record's file.
		public async Task<(Stream Content, string ContentType)?> OpenPhotoAsync(int companyId, int employeeId, CancellationToken ct = default)
		{
			var doc = await PhotoDocAsync(companyId, employeeId, ct);
			if (doc?.CurrentVersionId is not long vid) return null;
			var v = await _db.Set<PlatformDocumentVersion>().AsNoTracking()
				.Where(x => x.Id == vid && x.CompanyID == companyId && x.DocumentId == doc.Id)
				.Select(x => new { x.StorageKey, x.ContentType }).FirstOrDefaultAsync(ct);
			if (v == null || !StorageKey.TryParse(v.StorageKey, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			return s == null ? null : (s, v.ContentType);
		}
	}
}

namespace CrossBuy.BL.Hr
{
	// Who owns an applicant's files: the application's company. Recruitment's own authority (the action every
	// recruitment screen gates on) decides who may view or add them; Manage is the narrowest HR action.
	public sealed class JobApplicationDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.JobApplication;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.JobApplications.AsNoTracking().Where(a => a.ID == entityId && entityId > 0).Select(a => (int?)a.CompanyID).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId) => PermissionTarget.ForEntity(EntityRegistry.JobApplication, entityId);
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.View or DocumentAction.Download or DocumentAction.Upload
				or DocumentAction.Replace or DocumentAction.Delete => CrossBuy.BL.HrActions.PerformanceManage,
			_ => CrossBuy.BL.HrActions.ConfidentialView,
		};
	}

	// Who owns a leave request's files (HR3 medical certificates): the requester's company. ATTACHING is employee-request,
	// which HR answers purely on "is the target the caller" — so only the requester attaches, to their own request, and no
	// HR role widens that. Reading is employee-view on the requester (HR and the people over them); replacing or deleting
	// is employee-manage. The delivery route adds the request's own approvers.
	public sealed class LeaveRequestDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.LeaveRequest;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.LeaveRequests.AsNoTracking().Where(r => r.ID == entityId && entityId > 0)
				.Join(db.Employee.AsNoTracking(), r => r.EmployeeID, e => e.ID, (r, e) => (int?)e.EmpCompanyID).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId)
		{
			// The interface is synchronous; the requester is one indexed key lookup.
			var requester = db.LeaveRequests.AsNoTracking().Where(r => r.ID == entityId).Select(r => (int?)r.EmployeeID).FirstOrDefault();
			return requester is int e ? PermissionTarget.ForSubjectEmployee(e, companyId) : PermissionTarget.ForEntity(EntityRegistry.LeaveRequest, entityId);
		}
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.Upload or DocumentAction.Submit => CrossBuy.BL.HrActions.EmployeeRequest,
			DocumentAction.View or DocumentAction.Download => CrossBuy.BL.HrActions.EmployeeView,
			DocumentAction.Replace or DocumentAction.Delete => CrossBuy.BL.HrActions.EmployeeManage,
			_ => CrossBuy.BL.HrActions.ConfidentialView,
		};
	}
}
