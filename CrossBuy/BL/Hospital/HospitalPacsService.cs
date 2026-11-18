using System.Text;
using System.Text.RegularExpressions;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H199 — the imaging archive (PACS). The machines send their pictures to the archive, not to us; what joins
	// the two is the order's ACCESSION NUMBER. So:
	//   · the order screen opens the study in the archive's web viewer — the viewer address is a template naming the
	//     study by {accession} (and optionally the patient by {mrn}), each value URL-encoded, never pasted raw;
	//   · the order is handed to the archive's worklist broker as an HL7 v2 ORM^O01 (new order) carrying the
	//     accession number, the patient, the exam and its modality — so the machine's worklist shows the right patient
	//     and the pictures come back under the right accession;
	//   · an address that is not http(s), has no {accession}, or names a placeholder we do not fill is refused.
	// ==========================================================================================
	public sealed record HospitalPacsView(string? ViewerUrl, string? ReceivingApp, bool Ready)
	{
		public bool Connected => !string.IsNullOrWhiteSpace(ViewerUrl);
	}

	public static class HospitalPacsRules
	{
		public const string Accession = "{accession}";
		public const string Mrn = "{mrn}";
		private static readonly string[] Known = { Accession, Mrn };

		/// Why the viewer address cannot be kept, or null. An empty address is allowed: it disconnects the viewer.
		public static string? CheckViewer(string? url)
		{
			if (string.IsNullOrWhiteSpace(url)) return null;
			url = url.Trim();
			if (url.Length > 500) return "The viewer address is too long";
			if (!url.Contains(Accession, StringComparison.Ordinal)) return "The viewer address must name the study by {0}";
			foreach (Match m in Regex.Matches(url, @"\{[^}]*\}"))
				if (!Known.Contains(m.Value, StringComparer.Ordinal)) return "The viewer address names an unknown placeholder";
			var probe = url.Replace(Accession, "X", StringComparison.Ordinal).Replace(Mrn, "X", StringComparison.Ordinal);
			if (!Uri.TryCreate(probe, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
				return "The viewer address must be a web address (http or https)";
			return null;
		}

		/// Why the receiving name cannot be kept, or null. HL7 delimiters would break the message it is written into.
		public static string? CheckReceiver(string? app)
		{
			if (string.IsNullOrWhiteSpace(app)) return null;
			app = app.Trim();
			if (app.Length > 60 || app.IndexOfAny(new[] { '|', '^', '~', '\\', '&', '\r', '\n' }) >= 0) return "The receiving system's name may not hold HL7 delimiters";
			return null;
		}

		/// The study's address in the viewer, or null when no viewer is set.
		public static string? ViewerLink(string? template, string accession, string? mrn) =>
			string.IsNullOrWhiteSpace(template) ? null
				: template.Trim().Replace(Accession, Uri.EscapeDataString(accession ?? ""), StringComparison.Ordinal)
					.Replace(Mrn, Uri.EscapeDataString(mrn ?? ""), StringComparison.Ordinal);

		/// HL7 v2 escaping of a text field (the delimiters become escape sequences; a line break becomes a space).
		public static string Esc(string? s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			var b = new StringBuilder(s.Length + 8);
			foreach (var c in s)
				b.Append(c switch { '\\' => "\\E\\", '|' => "\\F\\", '^' => "\\S\\", '&' => "\\T\\", '~' => "\\R\\", '\r' or '\n' => " ", _ => c.ToString() });
			return b.ToString();
		}

		public static string Priority(string? p) => p switch { HospitalLabPriority.Stat => "S", HospitalLabPriority.Urgent => "A", _ => "R" };

		/// The person's name as HL7 wants it — family^given: the first word is the given name, the rest the family.
		public static string PersonName(string? name)
		{
			var parts = (name ?? "").Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			return parts.Length switch { 0 => "", 1 => Esc(parts[0]), _ => Esc(parts[1]) + "^" + Esc(parts[0]) };
		}

		/// A new imaging order (ORM^O01, v2.5) for the archive's worklist broker.
		public static string Orm(HospitalRadOrder o, HospitalPatient p, string? receivingApp, DateTime nowUtc)
		{
			string ts = nowUtc.ToString("yyyyMMddHHmmss"), ordered = o.OrderedAt.ToString("yyyyMMddHHmmss"), acc = Esc(o.AccessionNo), prio = Priority(o.Priority);
			string name = PersonName(string.IsNullOrWhiteSpace(p.NameEn) ? p.Name : p.NameEn);
			var msh = new[] { "MSH", "^~\\&", "CROSSBUSINESS", "HOSPITAL", Esc(receivingApp), "", ts, "", "ORM^O01^ORM_O01", "ORM" + nowUtc.ToString("yyyyMMddHHmmssfff"), "P", "2.5", "", "", "", "", "", "UNICODE UTF-8" };
			var pid = new[] { "PID", "1", "", Esc(p.Mrn) + "^^^HOSP^MR", "", name, "", p.BirthDate?.ToString("yyyyMMdd") ?? "", p.Sex == HospitalPatientSex.Female ? "F" : p.Sex == HospitalPatientSex.Male ? "M" : "U" };
			var pv1 = new[] { "PV1", "1", o.EncounterId == null ? "O" : "U" };
			var orc = new[] { "ORC", "NW", acc, acc, "", "SC", "", "^^^" + ordered + "^^" + prio, "", ordered };
			var obr = new string[28];
			Array.Fill(obr, "");
			obr[0] = "OBR"; obr[1] = "1"; obr[2] = acc; obr[3] = acc; obr[4] = Esc(o.Code) + "^" + Esc(string.IsNullOrWhiteSpace(o.NameEn) ? o.Name : o.NameEn) + "^L";
			obr[7] = ordered; obr[18] = acc; obr[19] = acc; obr[24] = Esc(o.Modality); obr[27] = "^^^^^" + prio;
			return string.Join("\r", new[] { msh, pid, pv1, orc, obr }.Select(s => string.Join("|", s))) + "\r";
		}
	}

	public sealed class HospitalPacsService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		public HospitalPacsService(CrossDbContext db, IHospitalAccessService access) { _db = db; _access = access; }

		private IQueryable<HospitalPacsSettings> Settings(BusinessContext ctx) => _db.Set<HospitalPacsSettings>().Where(x => x.CompanyId == ctx.CompanyId);

		public async Task<HospitalPacsView> GetAsync(BusinessContext ctx, CancellationToken ct = default)
		{
			try
			{
				var s = await Settings(ctx).AsNoTracking().FirstOrDefaultAsync(ct);
				return new HospitalPacsView(s?.ViewerUrl, s?.ReceivingApp, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException) { return new HospitalPacsView(null, null, false); }   // the slice is not applied yet
		}

		public async Task<HospitalResult> SaveAsync(BusinessContext ctx, string? viewerUrl, string? receivingApp, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Manage, null, ct)) return HospitalResult.Fail("forbidden");
			var why = HospitalPacsRules.CheckViewer(viewerUrl) ?? HospitalPacsRules.CheckReceiver(receivingApp);
			if (why != null) return HospitalResult.Fail(why, HospitalPacsRules.Accession);   // the one refusal with a {0} names the placeholder
			var s = await Settings(ctx).FirstOrDefaultAsync(ct);
			if (s == null) { s = new HospitalPacsSettings { CompanyId = ctx.CompanyId }; _db.Add(s); }
			s.ViewerUrl = string.IsNullOrWhiteSpace(viewerUrl) ? null : viewerUrl.Trim();
			s.ReceivingApp = string.IsNullOrWhiteSpace(receivingApp) ? null : receivingApp.Trim();
			s.UpdatedAt = nowUtc; s.UpdatedByEmployeeId = employeeId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(s.ID);
		}

		/// The order's ORM^O01, or null when the order is not this company's or the reader may not handle imaging.
		public async Task<(string Accession, string Message)?> OrderMessageAsync(BusinessContext ctx, int orderId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Rad, null, ct)) return null;
			var o = await _db.HospitalRadOrders.AsNoTracking().FirstOrDefaultAsync(x => x.ID == orderId && x.CompanyId == ctx.CompanyId, ct);
			if (o == null) return null;
			var p = await _db.HospitalPatients.AsNoTracking().FirstOrDefaultAsync(x => x.ID == o.PatientId && x.CompanyId == ctx.CompanyId, ct);
			if (p == null) return null;
			var s = await GetAsync(ctx, ct);
			return (o.AccessionNo, HospitalPacsRules.Orm(o, p, s.ReceivingApp, nowUtc));
		}
	}
}
