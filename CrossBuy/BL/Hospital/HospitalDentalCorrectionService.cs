using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H184 — dental work marked done by mistake, taken back (from the dental review: "a completed line has no
	// correction path; the bill keeps the charge"). Marking a line done completes its chart mark and charges the patient
	// (H53). Taking it back reverses all three, in one transaction, and leaves a trail:
	//   · the line returns to planned (out of its session), its chart mark to planned;
	//   · the bill: an OPEN bill's line is removed; a CLOSED bill stays as posted and a credit note (H148) reverses the
	//     revenue and VAT against the receivable. Refused when the insurer paid part of it (the claim corrects it), when the
	//     bill was closed in a foreign currency, or when the patient has already paid it (the cashier settles first);
	//   · the signed session that recorded it gets a correction addendum (H177) naming the work and the reason;
	//   · a HospitalDentalCorrections row keeps what the line was and why it was taken back.
	// ==========================================================================================
	public sealed record HospitalDentalCorrectionRow(HospitalDentalCorrection Row, string Procedure, string ProcedureEn, string By);

	public static class HospitalDentalCorrectionRules
	{
		public const string NotDone = "Only work marked done is taken back";
		public const string Paid = "The patient has already paid this bill: the cashier settles the amount first, then take the work back";

		public static string? Refusal(string status, string? reason)
		{
			if (status != HospitalDentalLineStatus.Completed) return NotDone;
			if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5) return "Write why the work is taken back";
			return null;
		}

		/// The session's correction addendum: what was not done, in the language of the person who writes it (an addendum
		/// is free text, like one typed by hand).
		public static string AddendumText(string procedure, int? tooth, bool arabic = false) => arabic
			? "لم يُنجز — سُجّل خطأً: " + procedure + (tooth is int ta ? " (السن " + ta + ")" : "")
			: "Not done — recorded by mistake: " + procedure + (tooth is int t ? " (tooth " + t + ")" : "");
	}

	public sealed class HospitalDentalCorrectionService
	{
		private readonly CrossDbContext _db;
		private readonly IHospitalAccessService _access;
		private readonly HospitalBillingService _billing;
		private readonly IHospitalLedger _ledger;

		public HospitalDentalCorrectionService(CrossDbContext db, IHospitalAccessService access, HospitalBillingService billing, IHospitalLedger ledger)
		{ _db = db; _access = access; _billing = billing; _ledger = ledger; }

		public async Task<IReadOnlyList<HospitalDentalCorrectionRow>> ListAsync(BusinessContext ctx, int planId, CancellationToken ct = default)
		{
			try
			{
				var rows = await _db.Set<HospitalDentalCorrection>().AsNoTracking().Where(x => x.CompanyId == ctx.CompanyId && x.PlanId == planId).OrderByDescending(x => x.At).ToListAsync(ct);
				if (rows.Count == 0) return Array.Empty<HospitalDentalCorrectionRow>();
				var sids = rows.Select(r => r.ServiceId).Distinct().ToList();
				var svc = await _db.HospitalServices.AsNoTracking().Where(s => s.CompanyId == ctx.CompanyId && sids.Contains(s.ID)).Select(s => new { s.ID, s.Name, s.NameEn }).ToListAsync(ct);
				var eids = rows.Select(r => r.ByEmployeeId).Distinct().ToList();
				var people = await _db.Employee.AsNoTracking().Where(e => eids.Contains(e.ID)).Select(e => new { e.ID, e.FullName, e.FullNameEn }).ToListAsync(ct);
				return rows.Select(r =>
				{
					var s = svc.FirstOrDefault(x => x.ID == r.ServiceId);
					var p = people.FirstOrDefault(x => x.ID == r.ByEmployeeId);
					return new HospitalDentalCorrectionRow(r, s?.Name ?? "—", s?.NameEn ?? s?.Name ?? "—", p == null ? "—" : DisplayName.Of(p.FullName ?? "", p.FullNameEn));
				}).ToList();
			}
			catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208) { return Array.Empty<HospitalDentalCorrectionRow>(); }
		}

		public async Task<HospitalResult> UndoAsync(BusinessContext ctx, int lineId, string? reason, int? employeeId, DateTime nowUtc, CancellationToken ct = default)
		{
			if (!await _access.CanAsync(ctx, HospitalActions.Consult, null, ct) || employeeId is not int me) return HospitalResult.Fail("forbidden");
			var line = await _db.Set<HospitalDentalPlanLine>().AsNoTracking().FirstOrDefaultAsync(l => l.ID == lineId && l.CompanyId == ctx.CompanyId, ct);
			if (line == null) return HospitalResult.Fail("not_found");
			if (HospitalDentalCorrectionRules.Refusal(line.Status, reason) is string why) return HospitalResult.Fail(why);
			var said = reason!.Trim()[..Math.Min(300, reason.Trim().Length)];
			var patientId = await _db.Set<HospitalDentalPlan>().Where(p => p.ID == line.PlanId && p.CompanyId == ctx.CompanyId).Select(p => p.PatientId).FirstAsync(ct);

			// the bill line it was charged on, and whether its bill is still open
			HospitalBillLine? bl = line.BillLineId is int blId ? await _db.HospitalBillLines.FirstOrDefaultAsync(x => x.ID == blId && x.CompanyId == ctx.CompanyId, ct) : null;
			HospitalBill? bill = bl == null ? null : await _db.HospitalBills.AsNoTracking().FirstOrDefaultAsync(b => b.ID == bl.BillId && b.CompanyId == ctx.CompanyId, ct);
			bool closed = bill != null && bill.Status != HospitalBillStatus.Open;
			HospitalBillingSettings? books = null; decimal creditQty = 0;
			if (closed)
			{
				var view = (await _billing.BillAsync(ctx, bill!.ID, nowUtc, ct))!;
				if (view.Foreign) return HospitalResult.Fail("The bill was closed in a foreign currency: correct it at billing");
				bool insured = await (from cl in _db.Set<HospitalBillCoverageLine>().AsNoTracking()
									  join cv in _db.Set<HospitalBillCoverage>().AsNoTracking() on cl.CoverageId equals cv.ID
									  where cv.CompanyId == ctx.CompanyId && cv.BillId == bill.ID && cl.BillLineId == bl!.ID && cl.InsurerAmount > 0
									  select cl.ID).AnyAsync(ct);
				if (insured) return HospitalResult.Fail("The insurer paid part of this work: correct it through the insurance claim");
				var credited = await _db.Set<HospitalCreditNoteLine>().AsNoTracking().Where(c => c.CompanyId == ctx.CompanyId && c.BillLineId == bl!.ID).SumAsync(c => (decimal?)c.Quantity, ct) ?? 0;
				creditQty = bl!.Quantity - credited;
				books = await _billing.SettingsAsync(ctx, ct);
				if (books.ReceivableAccountId == null || books.RevenueAccountId == null) return HospitalResult.Fail("Map the receivable and revenue accounts in billing settings");
				if (bl.Taxable && books.VatAccountId == null) return HospitalResult.Fail("Map the output VAT account in billing settings");
			}

			await using var tx = await ScopedTx.BeginOrJoinAsync(_db);
			int n = await _db.Set<HospitalDentalPlanLine>().Where(l => l.ID == lineId && l.CompanyId == ctx.CompanyId && l.Status == HospitalDentalLineStatus.Completed).ExecuteUpdateAsync(u => u
				.SetProperty(l => l.Status, HospitalDentalLineStatus.Planned).SetProperty(l => l.CompletedAt, (DateTime?)null).SetProperty(l => l.CompletedByEmployeeId, (int?)null)
				.SetProperty(l => l.SessionId, (int?)null).SetProperty(l => l.BillLineId, (int?)null), ct);
			if (n != 1) { await tx.RollbackAsync(); return HospitalResult.Fail(HospitalDentalService.ConcurrentChange); }
			await _db.Set<HospitalDentalFinding>().Where(f => f.CompanyId == ctx.CompanyId && f.PlanLineId == lineId && f.VoidedAt == null)
				.ExecuteUpdateAsync(u => u.SetProperty(f => f.Status, HospitalDentalMark.Planned).SetProperty(f => f.CompletedAt, (DateTime?)null), ct);
			var row = new HospitalDentalCorrection
			{
				CompanyId = ctx.CompanyId, PatientId = patientId, PlanId = line.PlanId, LineId = line.ID, ServiceId = line.ServiceId, Tooth = line.Tooth, Fee = line.Fee,
				CompletedAt = line.CompletedAt, CompletedByEmployeeId = line.CompletedByEmployeeId, SessionId = line.SessionId, BillLineId = line.BillLineId,
				Reason = said, At = nowUtc, ByEmployeeId = me,
			};
			_db.Add(row);
			await _db.SaveChangesAsync(ct);

			if (bl != null && bill != null)
			{
				if (!closed)
				{
					// an open bill has posted nothing: its line goes
					row.Reversed = Math.Round(bl.Quantity * bl.UnitPrice, 4);
					_db.HospitalBillLines.Remove(bl);
					await _db.SaveChangesAsync(ct);
				}
				else if (creditQty > 0)
				{
					// H148: the closed bill stays as posted; a credit note reverses this line's revenue and VAT
					var (net, vat) = HospitalReturnRules.Money(creditQty, bl.UnitPrice, bl.Taxable, bl.VatRatePercent);
					string Next(IEnumerable<string> issued) { int max = 0; foreach (var x in issued) if (x.StartsWith("CN-") && int.TryParse(x.AsSpan(3), out var v) && v > max) max = v; return "CN-" + (max + 1).ToString("000000"); }
					var note = new HospitalCreditNote
					{
						CompanyId = ctx.CompanyId, BillId = bill.ID, PatientId = patientId, SourceType = HospitalCreditNoteSource.DentalCorrection, SourceId = row.ID,
						Net = net, Vat = vat, Total = net + vat, Reason = said, At = nowUtc, ByEmployeeId = me,
						NoteNo = Next(await _db.Set<HospitalCreditNote>().Where(x => x.CompanyId == ctx.CompanyId).Select(x => x.NoteNo).ToListAsync(ct)),
					};
					if (note.Total > 0)
					{
						_db.Add(note);
						await _db.SaveChangesAsync(ct);
						var revenue = bl.RevenueAccountId ?? books!.RevenueAccountId!.Value;
						_db.Add(new HospitalCreditNoteLine { CompanyId = ctx.CompanyId, CreditNoteId = note.ID, BillLineId = bl.ID, Quantity = creditQty, UnitPrice = bl.UnitPrice, Net = net, Vat = vat, RevenueAccountId = revenue });
						var je = new List<HospitalLedgerLine>();
						if (net > 0) je.Add(new HospitalLedgerLine(revenue, net, 0, note.NoteNo));
						if (vat > 0) je.Add(new HospitalLedgerLine(books!.VatAccountId!.Value, vat, 0, note.NoteNo));
						je.Add(new HospitalLedgerLine(books!.ReceivableAccountId!.Value, 0, note.Total, note.NoteNo));
						var (ok, err, jid) = await _ledger.PostAsync(ctx, nowUtc, "إشعار دائن " + note.NoteNo + " — تصحيح عمل أسنان على الفاتورة " + bill.BillNo,
							"Credit note " + note.NoteNo + " — dental work taken back on bill " + bill.BillNo, "HospitalCreditNote", note.ID, je, ct);
						if (!ok) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail("The ledger refused the posting: {0}", err ?? ""); }
						note.JournalEntryId = jid; row.CreditNoteId = note.ID; row.Reversed = note.Total;
						await _db.SaveChangesAsync(ct);
					}
				}
				// money already taken stays with the cashier: an overpaid bill is settled there first
				var after = (await _billing.BillAsync(ctx, bill.ID, nowUtc, ct))!;
				if (after.Totals.Due < 0) { await tx.RollbackAsync(); _db.ChangeTracker.Clear(); return HospitalResult.Fail(HospitalDentalCorrectionRules.Paid); }
			}

			// the signed session that recorded the work: a correction addendum (H177) — the session itself never changes
			if (line.SessionId is int sid)
			{
				bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
				var svc = await _db.HospitalServices.AsNoTracking().Where(s => s.ID == line.ServiceId && s.CompanyId == ctx.CompanyId).Select(s => new { s.Name, s.NameEn }).FirstOrDefaultAsync(ct);
				var proc = svc == null ? "" : ar || string.IsNullOrWhiteSpace(svc.NameEn) ? svc.Name : svc.NameEn;
				_db.Add(new HospitalClinicalAddendum
				{
					CompanyId = ctx.CompanyId, PatientId = patientId, Subject = HospitalAddendumSubject.DentalSession, SubjectId = sid, Kind = HospitalAddendumKind.Correction,
					Text = HospitalDentalCorrectionRules.AddendumText(proc, line.Tooth, ar), Reason = said, CreatedAt = nowUtc, CreatedByEmployeeId = me,
				});
				await _db.SaveChangesAsync(ct);
			}
			await tx.CommitAsync();
			return HospitalResult.Success(line.PlanId);
		}
	}
}
