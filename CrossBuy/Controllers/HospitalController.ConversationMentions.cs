using CrossBuy.BL;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// The @-picker's endpoint for hospital record conversations, KEPT APART from HospitalController.Conversations because
	// it stands on CommMentionCandidates, which is still uncommitted Communication work in the shared tree: this file is
	// committed with it, so the committed record conversations build on their own (the panel hides the picker when
	// its url does not resolve).
	public partial class HospitalController
	{
		/// The @-picker's list: the company directory (CommMentionCandidates, the rule every module shares), behind the
		/// same record gate. Who is finally notified is still the platform's decision: it drops anyone who may not read it.
		[HospitalReceptionAllowed]
		[HttpGet("Records/ConversationMentions")]
		[HospitalPerm(HospitalActions.Read)]
		public async Task<IActionResult> RecordConversationMentions(string? entity, int id, string? q, CancellationToken ct = default)
		{
			var ctx = await RecordGateAsync(entity, id, ct);
			if (ctx == null) return Json(Array.Empty<object>());
			var rows = await CrossBuy.BL.Communication.CommMentionCandidates.SearchAsync(_db, ctx.CompanyId, ctx.EmployeeId, q,
				arabic: System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar", cancellationToken: ct);
			return Json(rows.Select(r => new { id = r.Id, name = r.Name, avatar = r.Avatar }));
		}
	}
}
