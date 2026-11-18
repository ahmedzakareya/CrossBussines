using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H198 — an analyser's results (HL7 v2 ORU^R01) filed on their order. The rules:
	//   · the order is the one whose accession number the message names (OBR-2, else OBR-3); a verified order is locked;
	//     a specimen never collected is refused; a collected one is received in the lab as the device has it;
	//   · each result is placed on an analyte of the order's test — by the device's code linked on the devices screen,
	//     else by the analyte's own code; a result not final (OBX-11 other than F or C), with an unknown code, or in
	//     other units than the analyte's is not filed and is named in the message's detail;
	//   · what is filed MERGES with what the bench already entered (a partial panel never clears the rest), and goes
	//     through the bench's own entry (flags, critical values): RESULTED, never verified — the specialist verifies;
	//   · every message is kept as it came, once: the same device and control id again is a duplicate and changes nothing.
	// ==========================================================================================
	public sealed record HospitalDeviceReceipt(int MessageId, string Status, int Applied, string? Detail, string Ack);
	public sealed record HospitalDeviceMessageRow(HospitalDeviceMessage Message, string? Patient, string? PatientEn, string? Test, string? TestEn);

	public static class HospitalLabDeviceRules
	{
		public static string Status(int applied, int skipped) =>
			applied == 0 ? HospitalDeviceMessageStatus.Refused : skipped == 0 ? HospitalDeviceMessageStatus.Accepted : HospitalDeviceMessageStatus.Partial;

		public static bool SameUnit(string? device, string? analyte) =>
			string.IsNullOrWhiteSpace(device) || string.IsNullOrWhiteSpace(analyte) || string.Equals(device.Trim(), analyte.Trim(), StringComparison.OrdinalIgnoreCase);
	}

	public sealed class HospitalLabDeviceService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalLabService _lab;
		public HospitalLabDeviceService(CrossDbContext db, IHospitalAccessService access, HospitalLabService lab) { _db = db; _access = access; _lab = lab; }

		private IQueryable<HospitalDeviceMessage> Messages(BusinessContext ctx) => _db.Set<HospitalDeviceMessage>().Where(x => x.CompanyId == ctx.CompanyId);
		private IQueryable<HospitalDeviceCodeMap> Maps(BusinessContext ctx) => _db.Set<HospitalDeviceCodeMap>().Where(x => x.CompanyId == ctx.CompanyId);
		private static string Clip(string? s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s[..n];

		public async Task<HospitalResult> ReceiveAsync(BusinessContext ctx, string? raw, int? employeeId, DateTime nowUtc, CancellationToken ct = default, bool again = false)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Lab, null, ct)) return HospitalResult.Fail("forbidden");
			var (msg, why) = HospitalHl7.ParseOru(raw);
			var row = new HospitalDeviceMessage { CompanyId = ctx.CompanyId, Raw = raw ?? "", ReceivedAt = nowUtc, ReceivedByEmployeeId = employeeId,
				Device = Clip(msg?.Device ?? "?", 60), MessageId = Clip(msg?.MessageId ?? "?", 60) };
			HospitalResult Done(string status, int applied, string? detail)
			{
				row.Status = status; row.Applied = applied; row.Detail = detail == null ? null : Clip(detail, 500);
				_db.Add(row);
				return HospitalResult.Success(0);
			}
			if (msg == null) { Done(HospitalDeviceMessageStatus.Refused, 0, why); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
			if (!again && await Messages(ctx).AnyAsync(x => x.Device == row.Device && x.MessageId == row.MessageId && x.Status != HospitalDeviceMessageStatus.Refused && x.Status != HospitalDeviceMessageStatus.Duplicate, ct))
			{ Done(HospitalDeviceMessageStatus.Duplicate, 0, "Already received: nothing changed"); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }

			var acc = new[] { msg.Placer, msg.Filler }.Where(x => !string.IsNullOrEmpty(x)).ToList();
			var order = await _db.HospitalLabOrders.FirstOrDefaultAsync(o => o.CompanyId == ctx.CompanyId && acc.Contains(o.AccessionNo), ct);
			row.AccessionNo = Clip(msg.Placer ?? msg.Filler, 30); row.LabOrderId = order?.ID;
			string? refuse = order == null ? "No order has this accession number"
				: order.Status == HospitalLabStatus.Verified ? "This result is verified and locked"
				: order.Status == HospitalLabStatus.Ordered ? "The specimen was never collected" : null;
			if (refuse != null) { Done(HospitalDeviceMessageStatus.Refused, 0, refuse); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
			if (order!.Status == HospitalLabStatus.Collected)
			{
				var rec = await _lab.ReceiveAsync(ctx, order.ID, employeeId, nowUtc, ct);   // the device has the specimen: it is in the lab
				if (!rec.Ok) { Done(HospitalDeviceMessageStatus.Refused, 0, rec.Error); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
			}

			var analytes = await _db.HospitalLabAnalytes.AsNoTracking().Where(a => a.CompanyId == ctx.CompanyId && a.ServiceId == order.ServiceId && a.IsActive).ToListAsync(ct);
			var maps = await Maps(ctx).AsNoTracking().Where(m => m.Device == row.Device).ToListAsync(ct);
			var existing = await _db.HospitalLabResults.AsNoTracking().Where(r => r.CompanyId == ctx.CompanyId && r.LabOrderId == order.ID).ToListAsync(ct);
			var values = existing.ToDictionary(r => r.AnalyteId, r => (string?)(r.Value is decimal v ? v.ToString(System.Globalization.CultureInfo.InvariantCulture) : r.TextValue));
			var skipped = new List<string>(); int applied = 0;
			foreach (var o in msg.Results)
			{
				var mapped = maps.FirstOrDefault(m => string.Equals(m.DeviceCode, o.Code, StringComparison.OrdinalIgnoreCase))?.AnalyteId;
				var a = analytes.FirstOrDefault(x => x.ID == mapped) ?? analytes.FirstOrDefault(x => string.Equals(x.Code, o.Code, StringComparison.OrdinalIgnoreCase));
				if (!HospitalHl7.Files(o)) { skipped.Add(o.Code + " (not final)"); continue; }
				if (a == null) { skipped.Add(o.Code + " (unknown code)"); continue; }
				if (!HospitalLabDeviceRules.SameUnit(o.Units, a.Unit)) { skipped.Add(o.Code + " (unit " + o.Units + " → " + a.Unit + ")"); continue; }   // no comma inside: the list is comma-separated
				values[a.ID] = o.Value; applied++;
			}
			if (applied > 0)
			{
				var saved = await _lab.SaveResultsAsync(ctx, order.ID, values, order.Comment, employeeId, nowUtc, ct);
				if (!saved.Ok) { _db.ChangeTracker.Clear(); Done(HospitalDeviceMessageStatus.Refused, 0, saved.Error); await _db.SaveChangesAsync(ct); return HospitalResult.Success(row.ID); }
			}
			Done(HospitalLabDeviceRules.Status(applied, skipped.Count), applied, skipped.Count == 0 ? null : string.Join(", ", skipped));
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(row.ID);
		}

		/// A stored message applied again — after its device codes were linked: a new row, the same rules, no duplicate check.
		public async Task<HospitalResult> ApplyAgainAsync(BusinessContext ctx, int id, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			var m = await Messages(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (m == null) return HospitalResult.Fail("not_found");
			if (m.Status is HospitalDeviceMessageStatus.Accepted or HospitalDeviceMessageStatus.Duplicate) return HospitalResult.Fail("This message was applied in full");
			return await ReceiveAsync(ctx, m.Raw, employeeId, nowUtc, ct, again: true);
		}

		/// The acknowledgement for a stored message.
		public async Task<HospitalDeviceReceipt?> ReceiptAsync(BusinessContext ctx, int id, DateTime nowUtc, CancellationToken ct = default)
		{
			var m = await Messages(ctx).AsNoTracking().FirstOrDefaultAsync(x => x.ID == id, ct);
			if (m == null) return null;
			string code = m.Status is HospitalDeviceMessageStatus.Accepted or HospitalDeviceMessageStatus.Duplicate ? "AA" : m.Status == HospitalDeviceMessageStatus.Partial ? "AE" : "AR";
			return new HospitalDeviceReceipt(m.ID, m.Status, m.Applied, m.Detail, HospitalHl7.Ack(m.Device, m.MessageId, code, m.Detail, nowUtc));
		}

		/// A device's code linked to an analyte (the bench's or the manager's).
		public async Task<HospitalResult> MapAsync(BusinessContext ctx, string? device, string? code, int analyteId, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Lab, null, ct)) return HospitalResult.Fail("forbidden");
			device = device?.Trim(); code = code?.Trim();
			if (string.IsNullOrEmpty(device) || string.IsNullOrEmpty(code) || device.Length > 60 || code.Length > 40) return HospitalResult.Fail("Name the device and its code");
			if (!await _db.HospitalLabAnalytes.AnyAsync(a => a.ID == analyteId && a.CompanyId == ctx.CompanyId, ct)) return HospitalResult.Fail("not_found");
			var m = await Maps(ctx).FirstOrDefaultAsync(x => x.Device == device && x.DeviceCode == code, ct);
			if (m == null) { m = new HospitalDeviceCodeMap { CompanyId = ctx.CompanyId, Device = device, DeviceCode = code, CreatedAt = nowUtc, CreatedByEmployeeId = employeeId }; _db.Add(m); }
			m.AnalyteId = analyteId;
			await _db.SaveChangesAsync(ct);
			return HospitalResult.Success(m.ID);
		}

		public async Task<(IReadOnlyList<HospitalDeviceMessageRow> Rows, int Total, bool Ready)> ListAsync(BusinessContext ctx, string? q, string? status, int page, int pageSize, CancellationToken ct = default)
		{
			try
			{
				var query = from m in Messages(ctx).AsNoTracking()
							join o0 in _db.HospitalLabOrders.AsNoTracking() on m.LabOrderId equals o0.ID into oo
							from o in oo.DefaultIfEmpty()
							join p0 in _db.HospitalPatients.AsNoTracking() on o.PatientId equals p0.ID into pp
							from p in pp.DefaultIfEmpty()
							select new { m, o, p };
				if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.m.Status == status);
				if (!string.IsNullOrWhiteSpace(q)) { var t = q.Trim(); query = query.Where(x => x.m.Device.Contains(t) || x.m.MessageId.Contains(t) || (x.m.AccessionNo != null && x.m.AccessionNo.Contains(t)) || (x.p != null && (x.p.Name.Contains(t) || x.p.Mrn.Contains(t)))); }
				int total = await query.CountAsync(ct);
				var rows = await query.OrderByDescending(x => x.m.ID).Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToListAsync(ct);
				return (rows.Select(x => new HospitalDeviceMessageRow(x.m, x.p?.Name, x.p?.NameEn, x.o?.Name, x.o?.NameEn)).ToList(), total, true);
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 207 or 208) { return (Array.Empty<HospitalDeviceMessageRow>(), 0, false); }
		}

		public async Task<IReadOnlyList<(int Id, string Code, string Name, string NameEn, string Test)>> AnalytesAsync(BusinessContext ctx, CancellationToken ct = default) =>
			(await (from a in _db.HospitalLabAnalytes.AsNoTracking() join s in _db.HospitalServices.AsNoTracking() on a.ServiceId equals s.ID
					where a.CompanyId == ctx.CompanyId && a.IsActive orderby s.Code, a.SortOrder
					select new { a.ID, a.Code, a.Name, a.NameEn, TestCode = s.Code }).ToListAsync(ct)).Select(x => (x.ID, x.Code, x.Name, x.NameEn, x.TestCode)).ToList();
	}
}
