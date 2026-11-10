using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H31 — an imaging order's pictures. The decisions are HospitalRadImageRules' (no database).
	// H35: each picture is a DOCUMENT on the HospitalRadOrder record of the central document platform (owner ruling:
	// every image goes to the unified attachments); HospitalRadImages is the order's list of them (caption, who, the
	// signed lock). A picture attached before H35 is read from its old key until MoveLegacyAsync moves it. Staff read with `read`; the radiographer (`rad`) or the radiologist
	// (`rad-report`) attaches and removes, until the report is signed. A patient reads only the pictures of their own
	// (or their family's) orders that the doctor released to the portal.
	// ==========================================================================================

	public sealed record HospitalRadImageRow(int Id, int RadOrderId, string FileName, string? Caption, DateTime UploadedAt);

	public static class HospitalRadImageRules
	{
		public const int MaxBytes = 15 * 1024 * 1024;
		public static readonly IReadOnlyList<string> Types = new[] { "image/jpeg", "image/png", "image/webp" };

		/// The type the bytes really are (their signature), or null — never the browser's word for it.
		public static string? Sniff(ReadOnlySpan<byte> head)
		{
			if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return "image/jpeg";
			if (head.Length >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47 && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return "image/png";
			if (head.Length >= 12 && head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P') return "image/webp";
			return null;
		}

		/// Why this picture cannot be attached, or null: an order still open (a signed report's pictures are its
		/// record), a real image, at most 15 MB.
		public static string? UploadRefusal(string orderStatus, long size, string? sniffed)
		{
			if (orderStatus == HospitalRadStatus.Signed) return "A signed report's images are locked";
			if (size <= 0 || size > MaxBytes) return "An image is up to 15 MB";
			if (sniffed == null) return "Only JPEG, PNG or WebP images are attached";
			return null;
		}

		public static string? RemoveRefusal(string orderStatus) => orderStatus == HospitalRadStatus.Signed ? "A signed report's images are locked" : null;
	}

	/// H35: who may do what to an imaging order's documents — read to view, `rad` (which every radiologist also
	/// holds) to attach; the signed-report lock is the hospital service's, checked before the platform is asked.
	public sealed class HospitalRadOrderDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
	{
		public string EntityType => EntityRegistry.HospitalRadOrder;
		public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
			=> db.HospitalRadOrders.AsNoTracking().Where(o => o.ID == entityId && entityId > 0).Select(o => (int?)o.CompanyId).FirstOrDefaultAsync(cancellationToken);
		public PermissionTarget TargetFor(int entityId, int companyId) => PermissionTarget.ForEntity(EntityRegistry.HospitalRadOrder, entityId);
		public string ModuleActionFor(DocumentAction action) => action switch
		{
			DocumentAction.View or DocumentAction.Download => HospitalActions.Read,
			DocumentAction.Upload or DocumentAction.Replace or DocumentAction.Delete => HospitalActions.Rad,
			DocumentAction.Manage => HospitalActions.Manage,
			_ => "hospital-document-unsupported",
		};
	}

	public sealed class HospitalRadImageService
	{
		public const string Metadata = "{\"hospital\":\"rad-image\"}";   // the platform takes a JSON object
		public const string NoTables = "The imaging pictures table is not created yet (hospital_h31_rad_images.sql)";
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly IDocumentStorage _storage;
		private readonly IPlatformDocumentService _docs;

		public HospitalRadImageService(CrossDbContext db, IHospitalAccessService access, IDocumentStorage storage, IPlatformDocumentService docs)
		{ _db = db; _access = access; _storage = storage; _docs = docs; }

		private Task<bool> Can(BusinessContext ctx, string action, CancellationToken ct) => _access.CanAsync(ctx, action, null, ct);
		private IQueryable<HospitalRadImage> Images(BusinessContext ctx) => _db.Set<HospitalRadImage>().Where(i => i.CompanyId == ctx.CompanyId);

		/// The pictures of these orders, in the order they were attached. Empty when the table is not there yet.
		public async Task<Dictionary<int, IReadOnlyList<HospitalRadImageRow>>> OfOrdersAsync(BusinessContext ctx, IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
		{
			if (orderIds.Count == 0) return new();
			try
			{
				return (await Images(ctx).AsNoTracking().Where(i => orderIds.Contains(i.RadOrderId)).OrderBy(i => i.ID)
						.Select(i => new HospitalRadImageRow(i.ID, i.RadOrderId, i.FileName, i.Caption, i.UploadedAt)).ToListAsync(ct))
					.GroupBy(i => i.RadOrderId).ToDictionary(g => g.Key, g => (IReadOnlyList<HospitalRadImageRow>)g.ToList());
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }
		}

		public async Task<HospitalResult> UploadAsync(BusinessContext ctx, int orderId, Stream content, long size, string? fileName, string? caption, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Rad, ct) && !await Can(ctx, HospitalActions.RadReport, ct)) return HospitalResult.Fail("forbidden");
			var status = await _db.HospitalRadOrders.AsNoTracking().Where(o => o.ID == orderId && o.CompanyId == ctx.CompanyId).Select(o => o.Status).FirstOrDefaultAsync(ct);
			if (status == null) return HospitalResult.Fail("not_found");
			// read the head to know what the bytes are; the whole file is buffered (≤ 15 MB) so nothing half-written is kept
			if (size is <= 0 or > HospitalRadImageRules.MaxBytes) return HospitalResult.Fail(HospitalRadImageRules.UploadRefusal(status, size, "image/jpeg")!);
			var buffer = new MemoryStream();
			await content.CopyToAsync(buffer, ct);
			var type = HospitalRadImageRules.Sniff(buffer.GetBuffer().AsSpan(0, (int)Math.Min(16, buffer.Length)));
			if (HospitalRadImageRules.UploadRefusal(status, buffer.Length, type) is string why) return HospitalResult.Fail(why);
			buffer.Position = 0;
			var name = Path.GetFileName(fileName ?? "");
			name = string.IsNullOrWhiteSpace(name) ? "image" : name[..Math.Min(200, name.Length)];
			var doc = await _docs.UploadAsync(new DocumentUploadRequest
			{
				EntityType = EntityRegistry.HospitalRadOrder, EntityId = orderId, FileName = name, ContentType = type!, Metadata = Metadata, Reason = "Imaging picture",
			}, buffer, ct);   // the platform authorises `rad` on this order and keeps the bytes
			if (!doc.Ok) return HospitalResult.Fail(doc.ReasonCode is "store_failed" ? "The change could not be saved" : "forbidden");
			var row = new HospitalRadImage
			{
				CompanyId = ctx.CompanyId, RadOrderId = orderId, StorageKey = null, DocumentId = doc.DocumentId, FileName = name,
				ContentType = type!, SizeBytes = (int)buffer.Length, Caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim()[..Math.Min(200, caption.Trim().Length)],
				UploadedAt = nowUtc, UploadedByEmployeeId = employeeId,
			};
			try
			{
				_db.Add(row);
				await _db.SaveChangesAsync(ct);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 208 or 207) { _db.ChangeTracker.Clear(); return HospitalResult.Fail(NoTables); }
			return HospitalResult.Success(row.ID);
		}

		public async Task<HospitalResult> RemoveAsync(BusinessContext ctx, int imageId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Rad, ct) && !await Can(ctx, HospitalActions.RadReport, ct)) return HospitalResult.Fail("forbidden");
			var img = await Images(ctx).AsNoTracking().FirstOrDefaultAsync(i => i.ID == imageId, ct);
			if (img == null) return HospitalResult.Fail("not_found");
			var status = await _db.HospitalRadOrders.AsNoTracking().Where(o => o.ID == img.RadOrderId && o.CompanyId == ctx.CompanyId).Select(o => o.Status).FirstAsync(ct);
			if (HospitalRadImageRules.RemoveRefusal(status) is string why) return HospitalResult.Fail(why);
			await Images(ctx).Where(i => i.ID == imageId).ExecuteDeleteAsync(ct);
			// a picture on the platform stays there as the order's history (the platform has no delete); a picture not
			// yet moved takes its private bytes with it
			if (img.DocumentId == null && StorageKey.TryParse(img.StorageKey, out var key)) await _storage.DeleteAsync(key, ct);
			return HospitalResult.Success(imageId);
		}

		/// A picture's bytes for staff (`read`), or null — a missing one and a refused one look the same.
		public async Task<(Stream Content, string Type)?> OpenAsync(BusinessContext ctx, int imageId, CancellationToken ct = default)
		{
			if (!await Can(ctx, HospitalActions.Read, ct)) return null;
			var img = await Images(ctx).AsNoTracking().FirstOrDefaultAsync(i => i.ID == imageId, ct);
			if (img == null) return null;
			if (img.DocumentId is long d)
			{
				var f = await _docs.OpenCurrentAsync(d, ct);   // the platform authorises the reader on this order
				return f == null ? null : (f.Content, img.ContentType);
			}
			return await ReadAsync(img, ct);
		}

		/// A picture's bytes for a patient: only of an order of one of these patients that the doctor released.
		public async Task<(Stream Content, string Type)?> OpenForPatientAsync(BusinessContext ctx, int imageId, IReadOnlyCollection<int> patientIds, CancellationToken ct = default)
		{
			var img = await (from i in Images(ctx).AsNoTracking()
							 join o in _db.HospitalRadOrders.AsNoTracking() on i.RadOrderId equals o.ID
							 where i.ID == imageId && o.CompanyId == ctx.CompanyId && patientIds.Contains(o.PatientId)
							 select i).FirstOrDefaultAsync(ct);
			if (img == null) return null;
			bool released;
			try { released = await _db.Set<HospitalPortalRelease>().AnyAsync(r => r.CompanyId == ctx.CompanyId && r.Source == HospitalPortalResultSource.Radiology && r.OrderId == img.RadOrderId, ct); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { released = false; }
			return released ? await ReadAsync(img, ct) : null;
		}

		/// H35: moves the pictures attached before H35 onto the platform, keeping their caption, author and time, then
		/// drops their old private bytes. A picture whose move fails stays exactly as it was.
		public async Task<IReadOnlyList<(int ImageId, bool Ok, string? Why)>> MoveLegacyAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			var done = new List<(int, bool, string?)>();
			var old = await Images(ctx).AsNoTracking().Where(i => i.DocumentId == null && i.StorageKey != null).OrderBy(i => i.ID).ToListAsync(ct);
			foreach (var img in old)
			{
				if (!StorageKey.TryParse(img.StorageKey, out var key)) { done.Add((img.ID, false, "bad key")); continue; }
				using var buffer = new MemoryStream();
				await using (var s = await _storage.OpenReadAsync(key, ct))
				{
					if (s == null) { done.Add((img.ID, false, "bytes missing")); continue; }
					await s.CopyToAsync(buffer, ct);
				}
				buffer.Position = 0;
				var doc = await _docs.UploadAsync(new DocumentUploadRequest
				{
					EntityType = EntityRegistry.HospitalRadOrder, EntityId = img.RadOrderId, FileName = img.FileName, ContentType = img.ContentType, Metadata = Metadata,
					Reason = "Imaging picture (moved from H31)",
				}, buffer, ct);
				if (!doc.Ok) { done.Add((img.ID, false, doc.ReasonCode)); continue; }
				await Images(ctx).Where(i => i.ID == img.ID).ExecuteUpdateAsync(u => u.SetProperty(i => i.DocumentId, doc.DocumentId).SetProperty(i => i.StorageKey, (string?)null), ct);
				await _storage.DeleteAsync(key, ct);
				done.Add((img.ID, true, null));
			}
			return done;
		}

		/// The orders among these that the doctor released to the portal (whose pictures a patient may see).
		public async Task<HashSet<int>> ReleasedAsync(BusinessContext ctx, IReadOnlyCollection<int> orderIds, CancellationToken ct = default)
		{
			try { return (await _db.Set<HospitalPortalRelease>().AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.Source == HospitalPortalResultSource.Radiology && orderIds.Contains(r.OrderId)).Select(r => r.OrderId).ToListAsync(ct)).ToHashSet(); }
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return new(); }
		}

		/// The bytes by key — the portal's path (it has no employee context, which the platform needs; the caller has
		/// already checked the order is the patient's own and released) and a picture not yet moved.
		private async Task<(Stream Content, string Type)?> ReadAsync(HospitalRadImage img, CancellationToken ct)
		{
			string? raw = img.StorageKey;
			if (img.DocumentId is long d)
				raw = await (from doc in _db.Set<PlatformDocument>().AsNoTracking()
							 join v in _db.Set<PlatformDocumentVersion>().AsNoTracking() on doc.CurrentVersionId equals v.Id
							 where doc.Id == d && doc.CompanyID == img.CompanyId && doc.EntityType == EntityRegistry.HospitalRadOrder && doc.EntityId == img.RadOrderId
							 select v.StorageKey).FirstOrDefaultAsync(ct);
			if (!StorageKey.TryParse(raw, out var key)) return null;
			var s = await _storage.OpenReadAsync(key, ct);
			return s == null ? null : (s, img.ContentType);
		}
	}
}
