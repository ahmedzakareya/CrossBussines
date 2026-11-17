using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CrossBuy.Tests.HospitalPartialDispenseTests;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H152: formulary governance. What must never happen:
	//   · a restricted or non-formulary medicine leaving the pharmacy without the committee's approval for its line;
	//   · the person who asked deciding their own request; a refusal with no reason; an "alternative" that is itself off
	//     the formulary;
	//   · a request decided twice, or asked twice for the same line while one is open.
	// ==========================================================================================
	public class HospitalFormularyGovernanceTests
	{
		private static async Task Govern(World w, int itemId, string status, string note = "Infectious diseases consult only")
		{
			w.Host.Seed.Add(new HospitalDrug { CompanyId = 1, ItemId = itemId, GenericName = "دواء " + itemId, GenericNameEn = "drug " + itemId, FormularyStatus = status,
				RestrictionNote = status == HospitalFormularyStatus.Formulary ? null : note, UpdatedAt = Now });
			await w.Host.Seed.SaveChangesAsync();
		}

		[Fact]
		public void The_rules_refuse_a_thin_reason_a_self_decision_and_a_silent_refusal()
		{
			Assert.True(HospitalFormularyRules.Governed(HospitalFormularyStatus.Restricted));
			Assert.True(HospitalFormularyRules.Governed(HospitalFormularyStatus.NonFormulary));
			Assert.False(HospitalFormularyRules.Governed(HospitalFormularyStatus.Formulary));
			Assert.False(HospitalFormularyRules.Governed(HospitalFormularyStatus.Unavailable));   // withdrawn: never dispensed, not asked
			Assert.Equal("Write the clinical reason the patient needs this medicine", HospitalFormularyRules.RefuseAsk("needed"));
			Assert.Equal("The person who asked cannot decide the request", HospitalFormularyRules.RefuseDecision(true, null, 7, 7));
			Assert.Equal("Write why the committee refuses it", HospitalFormularyRules.RefuseDecision(false, " ", 7, 9));
			Assert.Null(HospitalFormularyRules.RefuseDecision(true, null, 7, 9));
		}

		[Fact]
		public async Task A_restricted_medicine_leaves_only_after_the_committee_approves_its_line()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _h = w.Host;
			await Govern(w, amox, HospitalFormularyStatus.Restricted);
			var gov = new HospitalFormularyGovernanceService(w.Host.Db, w.Access);
			var lines = await Lines(w, rx.ID);

			Assert.Equal("{0} needs the formulary committee's approval for this patient", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);
			// the paracetamol alone may go: the gate is per line given now
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now, default, new Dictionary<int, decimal> { [lines[para].ID] = 10, [lines[amox].ID] = 0 })).Ok);

			Assert.Equal("Write the clinical reason the patient needs this medicine", (await gov.AskAsync(Ctx(), rx.ID, lines[amox].ID, "needed", 7, Now)).Error);
			Assert.Equal("This medicine is on the formulary: no approval is needed", (await gov.AskAsync(Ctx(), rx.ID, lines[para].ID, "Penicillin-resistant strain on culture", 7, Now)).Error);
			var asked = await gov.AskAsync(Ctx(), rx.ID, lines[amox].ID, "Penicillin-resistant strain on culture", 7, Now);
			Assert.True(asked.Ok, asked.Error);
			Assert.Equal("This medicine already has a request for this prescription", (await gov.AskAsync(Ctx(), rx.ID, lines[amox].ID, "Penicillin-resistant strain on culture", 7, Now)).Error);
			Assert.Equal("{0} needs the formulary committee's approval for this patient", (await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Error);   // pending is not approved

			Assert.Equal("The person who asked cannot decide the request", (await gov.DecideAsync(Ctx(), asked.Id!.Value, true, null, null, 7, Now)).Error);
			Assert.True((await gov.DecideAsync(Ctx(9), asked.Id!.Value, true, "Approved for this course", null, 9, Now)).Ok);
			Assert.Equal("This request was already decided", (await gov.DecideAsync(Ctx(9), asked.Id!.Value, false, "changed our mind", null, 9, Now)).Error);
			Assert.True((await w.Pharmacy.DispenseAsync(Ctx(), rx.ID, 7, "u7", Now)).Ok);
			Assert.Equal(HospitalDispenseStatus.Dispensed, (await w.Host.Db.HospitalDispenses.AsNoTracking().SingleAsync()).Status);
		}

		[Fact]
		public async Task A_refusal_names_a_formulary_alternative_and_the_line_can_be_asked_again()
		{
			var (w, rx, para, amox) = await Prescribed();
			using var _h = w.Host;
			await Govern(w, amox, HospitalFormularyStatus.NonFormulary, "Not stocked: use the formulary penicillin");
			var gov = new HospitalFormularyGovernanceService(w.Host.Db, w.Access);
			var lines = await Lines(w, rx.ID);
			var id = (await gov.AskAsync(Ctx(), rx.ID, lines[amox].ID, "Patient stable on it from outside", 7, Now)).Id!.Value;

			Assert.Equal("Write why the committee refuses it", (await gov.DecideAsync(Ctx(9), id, false, null, para, 9, Now)).Error);
			Assert.Equal("The alternative must be a formulary medicine", (await gov.DecideAsync(Ctx(9), id, false, "Use the formulary one", para, 9, Now)).Error);   // para has no master record
			await Govern(w, para, HospitalFormularyStatus.Formulary);
			Assert.True((await gov.DecideAsync(Ctx(9), id, false, "Use the formulary one", para, 9, Now)).Ok);
			var r = (await gov.ForDispenseAsync(Ctx(), rx.ID))[lines[amox].ID];
			Assert.Equal((HospitalFormularyRequestStatus.Refused, para), (r.Status, r.AlternativeItemId!.Value));
			Assert.True((await gov.AskAsync(Ctx(), rx.ID, lines[amox].ID, "New culture: the alternative failed", 7, Now)).Ok);   // a refusal does not close the line
			Assert.Equal("not_found", (await gov.AskAsync(Ctx(7, 2), rx.ID, lines[amox].ID, "another company asking", 7, Now)).Error);
			var (rows, total) = await gov.ListAsync(Ctx(), null, HospitalFormularyRequestStatus.Pending, 1, 25);
			Assert.Equal(1, total);
			Assert.Equal("FR-000002", rows[0].R.RequestNo);
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalPharmacyModel.Configure);

		[Fact]
		public void The_requests_table_matches_its_slice()
		{
			var slice = HospitalSliceParity.Slice("hospital_h152_formulary_requests.sql");
			HospitalSliceParity.NamesItsTable(Model(), slice, typeof(HospitalFormularyRequest), "HospitalFormularyRequests");
			HospitalSliceParity.ColumnsMatch(Model(), slice, typeof(HospitalFormularyRequest), "HospitalFormularyRequests");
		}
	}
}
