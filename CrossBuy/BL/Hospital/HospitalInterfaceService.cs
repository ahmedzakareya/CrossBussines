using System.Security.Cryptography;
using System.Text;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H215 — the live interface channel: machines talk to the hospital directly, over HTTPS.
	//   · LAB RESULTS: an analyser, or the middleware / interface engine in front of it (which also bridges MLLP),
	//     POSTs an HL7 v2 ORU^R01 and gets the HL7 acknowledgement back — AA accepted, AE error, AR rejected — with the
	//     message's own control id. The message goes through the SAME path as one received at the laboratory's screen
	//     (H198): kept as it came, a duplicate recognised, unknown codes reported, only final results filed.
	//   · IMAGING WORKLIST: a modality worklist broker GETs the scheduled imaging orders, each as its ORM^O01 (H199),
	//     optionally for one modality, and serves them to the machines as their DICOM worklist.
	// Each interface has its own key (random, shown once, stored only as its SHA-256), may do only its one kind of
	// thing, and ACTS FOR a named employee: every message is checked against that person's rights at that moment, so
	// switching the person's role off switches the machine off too. A key that is wrong, inactive or of the other kind
	// gets no answer beyond "refused".
	// ==========================================================================================
	public sealed class HospitalInterfaceInput
	{
		public string? Code { get; set; }
		public string? Name { get; set; }
		public string? Kind { get; set; }
	}

	public sealed record HospitalInterfaceRow(HospitalInterface Interface, HospitalFace? ActsAs);
	public sealed record HospitalWorklistItem(string Accession, string Mrn, string Patient, string Modality, string Exam, DateTime OrderedAt, string Priority, string Hl7);

	public static class HospitalInterfaceRules
	{
		public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
		public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

		public static string? Refusal(HospitalInterfaceInput i)
		{
			var c = i.Code?.Trim() ?? "";
			if (c.Length < 2 || c.Length > 30 || !c.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')) return "Write a code of letters, digits and dashes";
			if (string.IsNullOrWhiteSpace(i.Name) || i.Name.Trim().Length > 100) return "Write the interface's name";
			return string.IsNullOrEmpty(i.Kind) || !HospitalInterfaceKind.All.Contains(i.Kind) ? "Choose what the interface does" : null;
		}

		/// The HL7 acknowledgement for a received message: AA accepted (also a duplicate: nothing more to do), AE when
		/// it was read but some results could not be filed, AR when it was refused.
		public static string Ack(string? controlId, string code, string? text, DateTime nowUtc)
		{
			static string Esc(string? s) => (s ?? "").Replace("\\", "\\E\\").Replace("|", "\\F\\").Replace("^", "\\S\\").Replace("&", "\\T\\").Replace("~", "\\R\\").Replace("\r", " ").Replace("\n", " ");
			string ts = nowUtc.ToString("yyyyMMddHHmmss");
			var msh = "MSH|^~\\&|CROSSBUSINESS|HOSPITAL|||" + ts + "||ACK^R01^ACK|ACK" + nowUtc.ToString("yyyyMMddHHmmssfff") + "|P|2.5";
			var msa = "MSA|" + code + "|" + Esc(controlId) + (string.IsNullOrEmpty(text) ? "" : "|" + Esc(text.Length > 80 ? text[..80] : text));
			return msh + "\r" + msa + "\r";
		}

		public static string AckCode(string status) => status switch
		{
			HospitalDeviceMessageStatus.Accepted or HospitalDeviceMessageStatus.Duplicate => "AA",
			HospitalDeviceMessageStatus.Partial => "AE",
			_ => "AR",
		};

		/// MSH-10 of a raw message, for the acknowledgement of one that could not be read at all.
		public static string? ControlId(string? raw)
		{
			var msh = (raw ?? "").Replace("\r\n", "\r").Replace('\n', '\r').Split('\r').FirstOrDefault(s => s.TrimStart().StartsWith("MSH"))?.TrimStart();
			if (msh == null || msh.Length < 4) return null;
			var f = msh.Split(msh[3]);
			return f.Length > 9 ? f[9].Trim() : null;
		}
	}

	public sealed class HospitalInterfaceService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalInterfaceService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalInterface> Interfaces(int companyId) => _db.Set<HospitalInterface>().Where(x => x.CompanyId == companyId);

		/// The interface a key opens, if it is active and of this kind and code (else null: refused, nothing said why).
		public async Task<HospitalInterface?> ByKeyAsync(string? code, string? key, string kind, DateTime nowUtc, CancellationToken ct = default)
		{
			if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(code) || key.Length > 200) return null;
			string h = HospitalInterfaceRules.Hash(key.Trim());
			var i = await _db.Set<HospitalInterface>().AsNoTracking().FirstOrDefaultAsync(x => x.KeyHash == h, ct);
			if (i == null || !i.IsActive || i.Kind != kind || !string.Equals(i.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
			await _db.Set<HospitalInterface>().Where(x => x.ID == i.ID).ExecuteUpdateAsync(u => u.SetProperty(x => x.LastUsedAt, nowUtc).SetProperty(x => x.Uses, x => x.Uses + 1), ct);
			return i;
		}

		/// The scheduled imaging orders, oldest first, each as its ORM^O01 (for one modality when named).
		public async Task<IReadOnlyList<HospitalWorklistItem>> WorklistAsync(BusinessContext ctx, string? modality, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Rad, null, ct)) return Array.Empty<HospitalWorklistItem>();
			var q = from o in _db.HospitalRadOrders.AsNoTracking()
					join p in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p.ID
					where o.CompanyId == ctx.CompanyId && p.CompanyId == ctx.CompanyId && (o.Status == HospitalRadStatus.Scheduled || o.Status == HospitalRadStatus.Arrived)
					select new { o, p };
			if (!string.IsNullOrWhiteSpace(modality)) q = q.Where(x => x.o.Modality == modality.Trim());
			var rows = await q.OrderBy(x => x.o.OrderedAt).Take(500).ToListAsync(ct);
			var app = await _db.Set<HospitalPacsSettings>().AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId).Select(s => s.ReceivingApp).FirstOrDefaultAsync(ct);
			return rows.Select(x => new HospitalWorklistItem(x.o.AccessionNo, x.p.Mrn, string.IsNullOrWhiteSpace(x.p.NameEn) ? x.p.Name : x.p.NameEn, x.o.Modality,
				string.IsNullOrWhiteSpace(x.o.NameEn) ? x.o.Name : x.o.NameEn, x.o.OrderedAt, x.o.Priority, HospitalPacsRules.Orm(x.o, x.p, app, nowUtc))).ToList();
		}

		// ---- the administrator's list ----

		public async Task<(IReadOnlyList<HospitalInterfaceRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, int page, int size, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return (Array.Empty<HospitalInterfaceRow>(), 0, true);
			try
			{
				var query = Interfaces(ctx.CompanyId).AsNoTracking();
				if (!string.IsNullOrWhiteSpace(q)) { var s = q.Trim(); query = query.Where(x => x.Code.Contains(s) || x.Name.Contains(s)); }
				int total = await query.CountAsync(ct);
				var rows = await query.OrderBy(x => x.Code).Skip((Math.Max(1, page) - 1) * size).Take(size).ToListAsync(ct);
				var faces = await HospitalFaces.OfEmployeesAsync(_db, ctx.CompanyId, rows.Select(r => (int?)r.ActsAsEmployeeId), ct);
				return (rows.Select(r => new HospitalInterfaceRow(r, faces.GetValueOrDefault(r.ActsAsEmployeeId))).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalInterfaceRow>(), 0, false); }
		}

		/// A new interface acting for the administrator who adds it; its key comes back ONCE.
		public async Task<(HospitalResult Result, string? Key)> CreateAsync(BusinessContext ctx, HospitalInterfaceInput input, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct) || employeeId is not int me) return (HospitalResult.Fail("forbidden"), null);
			if (HospitalInterfaceRules.Refusal(input) is string why) return (HospitalResult.Fail(why), null);
			var code = input.Code!.Trim().ToUpperInvariant();
			if (await Interfaces(ctx.CompanyId).AnyAsync(x => x.Code == code, ct)) return (HospitalResult.Fail("This code is already used"), null);
			var key = HospitalInterfaceRules.NewKey();
			var i = new HospitalInterface
			{
				CompanyId = ctx.CompanyId, Code = code, Name = input.Name!.Trim(), Kind = input.Kind!, KeyHash = HospitalInterfaceRules.Hash(key), KeyHint = key[^4..],
				ActsAsEmployeeId = me, IsActive = true, CreatedAt = nowUtc, CreatedByEmployeeId = me,
			};
			_db.Add(i);
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(i.ID), key);
		}

		/// A new key (the old one stops at once); it comes back ONCE.
		public async Task<(HospitalResult Result, string? Key)> RotateAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return (HospitalResult.Fail("forbidden"), null);
			var i = await Interfaces(ctx.CompanyId).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (i == null) return (HospitalResult.Fail("not_found"), null);
			var key = HospitalInterfaceRules.NewKey();
			i.KeyHash = HospitalInterfaceRules.Hash(key); i.KeyHint = key[^4..];
			await _db.SaveChangesAsync(ct);
			return (HospitalResult.Success(i.ID), key);
		}

		public async Task<HospitalResult> ToggleAsync(BusinessContext ctx, int id, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var i = await Interfaces(ctx.CompanyId).FirstOrDefaultAsync(x => x.ID == id, ct);
			if (i == null) return HospitalResult.Fail("not_found");
			i.IsActive = !i.IsActive;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(i.ID);
		}
	}
}
