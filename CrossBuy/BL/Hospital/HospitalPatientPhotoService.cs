using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// The patient's photograph through the CENTRAL DOCUMENT PLATFORM (owner ruling 2026-11-05: every image and
	// attachment goes to the unified attachments). Until now it was a file under wwwroot/uploads/hospital, served as
	// a static path — anonymously, until the gate learned the prefix.
	//
	// The photo is a document on the HospitalPatient record (Metadata marks it as THE photo); a new photo is a new
	// VERSION of it, so the history stays. PhotoPath keeps holding a URL — now the hospital's own action, which reads
	// the document through the platform (the resolver authorises the reader) — so every screen that shows the photo
	// from PhotoPath keeps working unchanged. The patient portal (no employee context, which the platform requires)
	// reads the current version's bytes directly, after it has checked the patient is the account's own.
	// ==========================================================================================
	public sealed class HospitalPatientDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.HospitalPatient;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.HospitalPatients.AsNoTracking().Where(p => p.ID == entityId && entityId > 0).Select(p => (int?)p.CompanyId).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId) => PermissionTarget.ForEntity(EntityRegistry.HospitalPatient, entityId);
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.View or DocumentAction.Download => HospitalActions.Read,
			DocumentAction.Upload or DocumentAction.Replace or DocumentAction.Delete => HospitalActions.Register,
			DocumentAction.Manage => HospitalActions.Manage,
			_ => "hospital-document-unsupported",
		};
	}

	public static class HospitalPatientPhotoRules
	{
		public const string Metadata = "{\"hospital\":\"patient-photo\"}";   // the platform takes a JSON object
		public const long MaxBytes = 5 * 1024 * 1024;

		/// The type from the file's own first bytes (JPEG, PNG or WebP), never from its name.
		public static (string Ext, string ContentType)? Sniff(ReadOnlySpan<byte> h)
		{
			if (h.Length >= 2 && h[0] == 0xFF && h[1] == 0xD8) return (".jpg", "image/jpeg");
			if (h.Length >= 4 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return (".png", "image/png");
			if (h.Length >= 12 && h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && h[8] == 0x57 && h[9] == 0x45 && h[10] == 0x42 && h[11] == 0x50) return (".webp", "image/webp");
			return null;
		}

		/// The URL PhotoPath holds: the hospital's action, the document, and its version (a new photo is a new URL).
		public static string Url(int patientId, long documentId, long versionId) => $"/Hospital/Patient/{patientId}/Photo?d={documentId}&v={versionId}";
	}

	public sealed class HospitalPatientPhotoService
	{
		private readonly CrossDbContext _db;
		private readonly IPlatformDocumentService _docs;
		private readonly IDocumentStorage _storage;

		public HospitalPatientPhotoService(CrossDbContext db, IPlatformDocumentService docs, IDocumentStorage storage)
		{ _db = db; _docs = docs; _storage = storage; }

		private Task<PlatformDocument?> PhotoDocAsync(int companyId, int patientId, CancellationToken ct) =>
			_db.Set<PlatformDocument>().AsNoTracking()
				.Where(d => d.CompanyID == companyId && d.EntityType == EntityRegistry.HospitalPatient && d.EntityId == patientId && d.Metadata == HospitalPatientPhotoRules.Metadata && d.Status == "Active")
				.OrderByDescending(d => d.Id).FirstOrDefaultAsync(ct);

		/// Stores the bytes as the patient's photo (a first upload, or a new version) and returns the URL for PhotoPath.
		/// The platform authorises it (hospital `register` on this patient) and keeps company isolation.
		public async Task<(string? Url, string? Error)> StoreAsync(BusinessContext ctx, int patientId, Stream content, long length, CancellationToken ct = default)
		{
			const string bad = "The photo must be an image of up to 5 MB";
			if (length <= 0 || length > HospitalPatientPhotoRules.MaxBytes) return (null, bad);
			using var buffer = new MemoryStream();
			await content.CopyToAsync(buffer, ct);
			var kind = HospitalPatientPhotoRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)Math.Min(12, buffer.Length)));
			if (kind is not { } k) return (null, bad);
			buffer.Position = 0;
			string name = "patient-" + patientId + k.Ext;
			var existing = await PhotoDocAsync(ctx.CompanyId, patientId, ct);
			var r = existing != null
				? await _docs.ReplaceAsync(existing.Id, buffer, name, k.ContentType, "New patient photo", ct)
				: await _docs.UploadAsync(new DocumentUploadRequest
				{
					EntityType = EntityRegistry.HospitalPatient, EntityId = patientId, FileName = name, ContentType = k.ContentType,
					Metadata = HospitalPatientPhotoRules.Metadata, Reason = "Patient photo",
				}, buffer, ct);
			if (!r.Ok) return (null, r.ReasonCode == DocumentAccessReasons.Allowed ? "The change could not be saved" : "forbidden");
			return (HospitalPatientPhotoRules.Url(patientId, r.DocumentId, r.VersionId), null);
		}

		/// Staff: the photo document's current bytes, through the platform (it authorises the reader). The document
		/// must be this patient's photo — an id from a URL never reaches another record's file.
		public async Task<DocumentContent?> OpenForStaffAsync(BusinessContext ctx, int patientId, long documentId, CancellationToken ct = default)
		{
			bool mine = await _db.Set<PlatformDocument>().AsNoTracking().AnyAsync(d => d.Id == documentId && d.CompanyID == ctx.CompanyId
				&& d.EntityType == EntityRegistry.HospitalPatient && d.EntityId == patientId && d.Metadata == HospitalPatientPhotoRules.Metadata, ct);
			return mine ? await _docs.OpenCurrentAsync(documentId, ct) : null;
		}

		/// The patient portal: the CALLER has already established that this patient is the account's own (or family's).
		/// No employee context exists there, so the current version is read directly — company-filtered, by its own key.
		public async Task<(Stream Content, string ContentType)?> OpenForPortalAsync(int companyId, int patientId, CancellationToken ct = default)
		{
			var doc = await PhotoDocAsync(companyId, patientId, ct);
			if (doc?.CurrentVersionId is not long vid) return null;
			var v = await _db.Set<PlatformDocumentVersion>().AsNoTracking().Where(x => x.Id == vid && x.CompanyID == companyId && x.DocumentId == doc.Id)
				.Select(x => new { x.StorageKey, x.ContentType }).FirstOrDefaultAsync(ct);
			if (v == null || !StorageKey.TryParse(v.StorageKey, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			return s == null ? null : (s, v.ContentType);
		}
	}
}
