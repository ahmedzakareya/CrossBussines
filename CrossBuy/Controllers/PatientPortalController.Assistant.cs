using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H266 — the patient's assistant: a question about the account's own file, answered from the same
	// services (and so the same proxy scope) the portal's pages use. No model and no data leave the hospital.
	public partial class PatientPortalController
	{
		public sealed class PortalAssistantInput { public string? Question { get; set; } }

		[HttpPost("Assistant"), ValidateAntiForgeryToken]
		public async Task<IActionResult> Assistant([FromBody] PortalAssistantInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var q = (input?.Question ?? "").Trim();
			if (q.Length > 300) q = q[..300];
			var intent = HospitalPortalAssistantRules.Intent(q);
			// read only what this intent needs
			IReadOnlyList<HospitalPortalVisit> visits = intent == HospitalPortalAssistantIntent.NextVisit ? await Booking.VisitsAsync(me.Ctx, me.Account.ID, NowLocal, ct) : Array.Empty<HospitalPortalVisit>();
			IReadOnlyList<HospitalPortalResult> results = intent == HospitalPortalAssistantIntent.Results ? await Records.ResultsAsync(me.Ctx, me.Account.ID, ct) : Array.Empty<HospitalPortalResult>();
			IReadOnlyList<HospitalPortalBill> bills = intent == HospitalPortalAssistantIntent.Bills ? await Records.BillsAsync(me.Ctx, me.Account.ID, DateTime.UtcNow, ct) : Array.Empty<HospitalPortalBill>();
			IReadOnlyList<HospitalPortalMedicine> meds = intent == HospitalPortalAssistantIntent.Medicines ? await Records.MedicinesAsync(me.Ctx, me.Account.ID, NowLocal, ct) : Array.Empty<HospitalPortalMedicine>();
			bool ar = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var a = HospitalPortalAssistantRules.Answer(intent, visits, results, bills, meds, ar, NowLocal);
			return base.Json(new
			{
				ok = true, intent = a.Intent, title = _t[a.TitleKey].Value,
				lines = a.Lines.Select(l => _t[l.Key, l.Args].Value).ToList(),
				link = a.Link == null ? null : Url.Action(a.Link), linkLabel = a.LinkKey == null ? null : _t[a.LinkKey].Value,
			});
		}
	}
}
