using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H106 — a field alert that cannot wait, with no database: it fires exactly as the screen shows it (at or
	// above, at or below, either side, a panel's first number); an empty or unreadable answer never fires; notifying
	// or opening a template needs the alert rule; the key that makes it once per form and field.
	// ==========================================================================================
	public class HospitalFieldAlertTests
	{
		private static readonly HospitalClinicalTemplateField Item9 = new() { ID = 77, AlertAbove = 1, AlertNotify = true, AlertTemplateId = 9 };

		[Fact]
		public void It_fires_as_the_screen_shows_it()
		{
			Assert.True(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["77"] = "1" }));      // PHQ-9 item 9: "several days"
			Assert.True(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["77"] = "3" }));
			Assert.False(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["77"] = "0" }));
			Assert.False(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["77"] = "" }));
			Assert.False(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["77"] = "x" }));
			Assert.False(HospitalFieldAlertRules.Fires(Item9, new Dictionary<string, string> { ["78"] = "3" }));      // another field
			var iop = new HospitalClinicalTemplateField { ID = 5, AlertAbove = 30 };
			Assert.True(HospitalFieldAlertRules.Fires(iop, new Dictionary<string, string> { ["5.L"] = "32" }));     // either eye
			var low = new HospitalClinicalTemplateField { ID = 6, AlertBelow = 90 };
			Assert.True(HospitalFieldAlertRules.Fires(low, new Dictionary<string, string> { ["6"] = "88/60" }));   // a panel's first number
			Assert.False(HospitalFieldAlertRules.Fires(new HospitalClinicalTemplateField { ID = 7 }, new Dictionary<string, string> { ["7"] = "9" }));   // no rule
		}

		[Fact]
		public void Notifying_needs_the_alert_rule_and_is_once_per_form_and_field()
		{
			Assert.Equal("Notifying or opening a template needs the alert rule first",
				HospitalTemplateRules.FieldRefusal(new HospitalTemplateFieldInput { ConceptId = 1, Section = "S", SectionEn = "S", AlertNotify = true }, new HospitalConcept { ID = 1, IsActive = true }, Array.Empty<HospitalClinicalTemplateField>(), null));
			Assert.NotEqual(HospitalFieldAlertRules.Key(1, 77), HospitalFieldAlertRules.Key(2, 77));
		}
	}
}
