using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H90 — the mortuary, with no database: the compartment codes, the time-kept warnings at their edges, what
	// holds a body and what a transfer lifts, what a receipt and a release need, and the tables against their slice.
	// ==========================================================================================
	public class HospitalMorgueTests
	{
		private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);
		private static HospitalMorgueCase C(double hours = 5, int? patient = 1, bool police = false, string autopsy = HospitalAutopsyStatus.None) =>
			new() { Status = HospitalMorgueStatus.InStorage, ReceivedAt = Now.AddHours(-hours), PatientId = patient, MedicoLegal = police, AutopsyStatus = autopsy };

		[Fact]
		public void Compartment_codes_expand()
		{
			Assert.Equal(new[] { "A1", "A2", "A3" }, HospitalMorgueService.ExpandCodes("a1-3"));
			Assert.Equal(new[] { "A1", "A2", "F1" }, HospitalMorgueService.ExpandCodes("A1-A2, f1"));
			Assert.Null(HospitalMorgueService.ExpandCodes("A5-A2"));
			Assert.Null(HospitalMorgueService.ExpandCodes("A 1"));
			Assert.Null(HospitalMorgueService.ExpandCodes("A1-A200"));
		}

		[Fact]
		public void Time_kept_warnings_at_their_edges()
		{
			Assert.Empty(HospitalMorgueRules.Warnings(C(71.9), true, Now));
			Assert.Equal(new[] { HospitalMorgueWarning.Over72h }, HospitalMorgueRules.Warnings(C(72), true, Now));
			Assert.Equal(new[] { HospitalMorgueWarning.OverWeek }, HospitalMorgueRules.Warnings(C(168), true, Now));
			Assert.Contains(HospitalMorgueWarning.Unidentified, HospitalMorgueRules.Warnings(C(patient: null, police: true), false, Now));
			Assert.Contains(HospitalMorgueWarning.NoCertificate, HospitalMorgueRules.Warnings(C(), false, Now));
			var gone = C(200); gone.Status = HospitalMorgueStatus.Released; gone.ReleasedAt = Now;
			Assert.Empty(HospitalMorgueRules.Warnings(gone, true, Now));
		}

		[Fact]
		public void What_holds_a_body_and_what_a_transfer_lifts()
		{
			Assert.Empty(HospitalMorgueRules.Holds(C(), true, false));
			Assert.Equal(new[] { HospitalMorgueWarning.PoliceHold, HospitalMorgueWarning.Autopsy }, HospitalMorgueRules.Holds(C(police: true, autopsy: HospitalAutopsyStatus.Requested), true, false));
			var cleared = C(police: true); cleared.PoliceClearedAt = Now;
			Assert.Empty(HospitalMorgueRules.Holds(cleared, true, false));
			Assert.Equal(new[] { HospitalMorgueWarning.NoCertificate }, HospitalMorgueRules.Holds(C(), false, false));
			Assert.Empty(HospitalMorgueRules.Holds(C(patient: null, police: true, autopsy: HospitalAutopsyStatus.Requested), false, true));   // forensic transfer
		}

		[Fact]
		public void A_release_needs_a_named_receiver_and_the_belongings()
		{
			HospitalMorgueReleaseInput R() => new() { ReleasedTo = "Ahmad", Relation = "Son", IdNo = "123", BelongingsHandedOver = true };
			Assert.Null(HospitalMorgueRules.ReleaseRefusal(C(), R(), true, true));
			var r = R(); r.Relation = null; Assert.Equal("Write the receiver's relation to the deceased", HospitalMorgueRules.ReleaseRefusal(C(), r, true, false));
			r = R(); r.IdNo = " "; Assert.Equal("Write the receiver's identity number", HospitalMorgueRules.ReleaseRefusal(C(), r, true, false));
			r = R(); r.BelongingsHandedOver = false; Assert.Equal("Hand the belongings over with the body", HospitalMorgueRules.ReleaseRefusal(C(), r, true, true));
			Assert.Null(HospitalMorgueRules.ReleaseRefusal(C(), r, true, false));    // nothing to hand over
			Assert.Equal("Something still holds the body: lift it first", HospitalMorgueRules.ReleaseRefusal(C(police: true), R(), true, false));
			var t = new HospitalMorgueReleaseInput { Transfer = true, ReleasedTo = "Forensic officer", IdNo = "9" };
			Assert.Equal("Write where the body is transferred", HospitalMorgueRules.ReleaseRefusal(C(police: true), t, false, false));
			t.Destination = "Forensic medicine"; Assert.Null(HospitalMorgueRules.ReleaseRefusal(C(police: true), t, false, false));
			var gone = C(); gone.Status = HospitalMorgueStatus.Released;
			Assert.Equal("This body is no longer in the mortuary", HospitalMorgueRules.ReleaseRefusal(gone, R(), true, false));
		}

		[Fact]
		public void A_receipt_needs_a_certificate_or_a_description_and_a_tag()
		{
			Assert.Null(HospitalMorgueRules.ReceiveRefusal(new() { DeathId = 1, TagNo = "T-1" }));
			Assert.Null(HospitalMorgueRules.ReceiveRefusal(new() { Description = "Male, about 40", TagNo = "T-2" }));
			Assert.Equal("Choose the death certificate, or describe an unknown body", HospitalMorgueRules.ReceiveRefusal(new() { TagNo = "T-3" }));
			Assert.Equal("Write the body tag number", HospitalMorgueRules.ReceiveRefusal(new() { DeathId = 1 }));
			Assert.Equal("Each belonging needs a name and a quantity", HospitalMorgueRules.ReceiveRefusal(new() { DeathId = 1, TagNo = "T", Belongings = new() { new() { Item = "Watch", Quantity = 0 } } }));
		}

		[Fact]
		public void The_tables_match_their_slice_and_the_register_moves_in_a_merge()
		{
			var s = HospitalSliceParity.Slice("hospital_h90_morgue.sql");
			var m = HospitalSliceParity.Model(HospitalMorgueModel.Configure);
			foreach (var (t, n) in new[] { (typeof(HospitalMorgueCompartment), "HospitalMorgueCompartments"), (typeof(HospitalMorgueCase), "HospitalMorgueCases"), (typeof(HospitalMorgueBelonging), "HospitalMorgueBelongings") })
			{
				HospitalSliceParity.NamesItsTable(m, s, t, n);
				HospitalSliceParity.ColumnsMatch(m, s, t, n);
			}
			Assert.Contains(HospitalPatientMergeRules.PatientTables, x => x.Entity == typeof(HospitalMorgueCase));
			foreach (var x in HospitalMorgueKind.All.Concat(HospitalMorgueStatus.All).Concat(HospitalAutopsyStatus.All)) Assert.Contains("'" + x + "'", s);
			Assert.Equal(new[] { "HospitalDeaths", "HospitalPatients" }, HospitalSliceParity.CheckedPrerequisites(s).OrderBy(x => x));
		}
	}
}
