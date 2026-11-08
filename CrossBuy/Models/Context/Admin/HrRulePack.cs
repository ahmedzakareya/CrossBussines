namespace CrossBuy.Models.Context.Admin
{
	// HR country rule pack (deploy/sql/hr_rule_packs.sql). CompanyID NULL = a country template; otherwise the company's
	// own editable copy. The rules are one JSON document (CrossBuy.BL.Hr.HrRules) so a new country is data, not code.
	public class HrRulePack
	{
		public int ID { get; set; }
		public int? CompanyID { get; set; }
		public string CountryCode { get; set; } = "KW";
		public string NameAr { get; set; } = "";
		public string NameEn { get; set; } = "";
		public string RulesJson { get; set; } = "{}";
		public int Version { get; set; } = 1;
		public DateTime? EffectiveFrom { get; set; }
		public DateTime UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }
	}
}
