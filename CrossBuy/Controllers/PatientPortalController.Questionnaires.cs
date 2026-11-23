using CrossBuy.BL.Hospital;
using Microsoft.AspNetCore.Mvc;

namespace CrossBuy.Controllers
{
	// HOSPITAL H250 — the patient's side of questionnaires: what the clinic sent to the account's people, and answering.
	public partial class PatientPortalController
	{
		private HospitalQuestionnaireService? _qn;
		private HospitalQuestionnaireService Qn => _qn ??= new HospitalQuestionnaireService(_db, _access);

		[HttpGet("Questionnaires")]
		public async Task<IActionResult> Questionnaires(int? id, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return NotAPatient();
			await PrepareAsync(me, "questionnaires", ct);
			var people = (await Booking.ClinicalPeopleAsync(me.Ctx, me.Account.ID, ct)).Select(p => p.PatientId).ToList();
			var list = await Qn.ForPeopleAsync(me.Ctx.CompanyId, people, ct);
			ViewBag.Open = list.FirstOrDefault(x => x.ID == id && x.Status == CrossBuy.Models.Context.Hospital.HospitalQuestionnaireStatus.Pending);
			return View(list);
		}

		[HttpPost("Questionnaires/{id:int}"), ValidateAntiForgeryToken]
		public async Task<IActionResult> QuestionnaireAnswer(int id, [FromBody] HospitalQuestionnaireAnswerInput input, CancellationToken ct = default)
		{
			if (await MeAsync(ct) is not { } me) return Unauthorized();
			var people = (await Booking.ClinicalPeopleAsync(me.Ctx, me.Account.ID, ct)).Select(p => p.PatientId).ToList();
			return Json(await Qn.AnswerAsync(me.Ctx.CompanyId, me.Account.ID, people, id, input ?? new(), DateTime.UtcNow, ct));
		}
	}
}
