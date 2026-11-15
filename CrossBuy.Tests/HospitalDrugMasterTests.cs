using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H133: the drug master's rules (pure) and its table. A record is refused when any of these is true:
	//   · it has no generic name;
	//   · its Tall Man spelling changes letters, not only their case;
	//   · it has a strength with no unit;
	//   · it has an ATC code that is not an ATC code;
	//   · it is off the list with no reason written.
	// A record is "incomplete" until it has a generic name, strength, form, route and ATC. Allergies and interactions
	// are matched on the generic as well as the prescribed name.
	// ==========================================================================================
	public class HospitalDrugMasterTests
	{
		private static HospitalDrugInput Valid() => new() { ItemId = 1, GenericName = "هيدرالازين", GenericNameEn = "hydralazine", TallMan = "hydrALAZINE",
			Strength = 25, StrengthUnit = "mg", Form = HospitalDrugForm.Tablet, Routes = new() { "PO", "PO", " IV " }, Atc = " c02db02 " };

		[Fact]
		public void A_sound_record_is_cleaned_and_accepted()
		{
			var i = Valid();
			Assert.Null(HospitalDrugRules.Validate(i));
			Assert.Equal(("C02DB02", HospitalFormularyStatus.Formulary), (i.Atc, i.FormularyStatus));
			Assert.Equal(new[] { "PO", "IV" }, i.Routes!.ToArray());
		}

		[Theory]
		[InlineData("generic", "Enter the generic name")]
		[InlineData("tallman", "The Tall Man name must spell the generic name — only the letter case changes")]
		[InlineData("unit", "Choose the strength's unit")]
		[InlineData("zero", "The strength must be above zero")]
		[InlineData("form", "Choose the dosage form from the list")]
		[InlineData("route", "Choose the routes from the list")]
		[InlineData("atc", "The ATC code is a letter, then two digits, then letters and digits — like A02BC01")]
		[InlineData("restricted", "Write who may prescribe it, or why it is off the list")]
		[InlineData("schedule", "Choose the controlled schedule")]
		[InlineData("ven", "Choose V, E or N")]
		public void An_unsound_record_is_refused(string what, string error)
		{
			var i = Valid();
			switch (what)
			{
				case "generic": i.GenericName = " "; i.GenericNameEn = null; break;
				case "tallman": i.TallMan = "hydrOXYzine"; break;
				case "unit": i.StrengthUnit = null; break;
				case "zero": i.Strength = 0; break;
				case "form": i.Form = "Pill"; break;
				case "route": i.Routes = new() { "XX" }; break;
				case "atc": i.Atc = "C2DB"; break;
				case "restricted": i.FormularyStatus = HospitalFormularyStatus.Restricted; break;
				case "schedule": i.ControlledSchedule = "Opioid"; break;
				case "ven": i.Ven = "X"; break;
			}
			Assert.Equal(error, HospitalDrugRules.Validate(i)?.Error);
		}

		[Fact]
		public void The_english_generic_alone_is_enough_and_fills_the_arabic()
		{
			var i = new HospitalDrugInput { GenericNameEn = "heparin", TallMan = "HEParin" };
			Assert.Null(HospitalDrugRules.Validate(i));
			Assert.Equal("heparin", i.GenericName);
		}

		[Theory]
		[InlineData("A", true)]
		[InlineData("A02", true)]
		[InlineData("A02BC01", true)]
		[InlineData("A2", false)]
		[InlineData("A02BC1", false)]
		[InlineData("a02", false)]   // the code is upper case; the input is upper-cased before the check
		public void An_atc_code_at_any_level(string code, bool ok) => Assert.Equal(ok, HospitalDrugRules.IsAtc(code));

		[Fact]
		public void A_record_is_complete_with_generic_strength_form_route_and_atc()
		{
			Assert.Equal(new[] { HospitalDrugRules.MissingRecord }, HospitalDrugRules.Missing(null).ToArray());
			var d = new HospitalDrug { GenericName = "x" };
			Assert.Equal(new[] { "strength", "form", "route", "atc" }, HospitalDrugRules.Missing(d).ToArray());
			d.Strength = 1; d.Form = HospitalDrugForm.Tablet; d.Routes = "PO"; d.Atc = "N02BE01";
			Assert.Empty(HospitalDrugRules.Missing(d));
			Assert.Equal("N", HospitalDrugRules.AtcGroup(d.Atc));
		}

		[Fact]
		public void Allergies_and_interactions_are_matched_on_the_generic_too()
		{
			var d = new HospitalDrug { GenericName = "أموكسيسيلين", GenericNameEn = "amoxicillin" };
			Assert.Equal("Augmentin 1 g أموكسيسيلين amoxicillin", HospitalDrugRules.MatchText("Augmentin 1 g", d));
			Assert.Equal("Augmentin 1 g", HospitalDrugRules.MatchText("Augmentin 1 g", null));
			Assert.Equal(new[] { "Amoxicillin" }, HospitalPharmacyService.AllergiesIn(HospitalDrugRules.MatchText("Augmentin 1 g", d), "Amoxicillin").ToArray());
		}

		private static Microsoft.EntityFrameworkCore.Metadata.IModel Model() => HospitalSliceParity.Model(HospitalDrugModel.Configure);
		private static string Slice() => HospitalSliceParity.Slice("hospital_h133_drug_master.sql");

		[Fact]
		public void The_model_names_the_table_its_slice_creates() => HospitalSliceParity.NamesItsTable(Model(), Slice(), typeof(HospitalDrug), "HospitalDrugs");

		[Fact]
		public void Every_column_is_read_in_the_type_its_slice_creates() => HospitalSliceParity.ColumnsMatch(Model(), Slice(), typeof(HospitalDrug), "HospitalDrugs");
	}
}
