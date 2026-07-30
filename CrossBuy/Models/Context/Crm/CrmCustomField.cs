namespace CrossBuy.Models.Context.Crm
{
	// CRM 3-7b — user-defined custom fields on CRM entities. No GL impact.
	public class CrmCustomField
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string EntityType { get; set; } = "Lead";    // Lead | Opportunity | Account | Activity
		public string FieldKey { get; set; } = "";           // stable key
		public string Label { get; set; } = "";
		public string? LabelEn { get; set; }
		public string FieldType { get; set; } = "Text";      // Text | Number | Date | Select | Checkbox
		public string? Options { get; set; }                 // CSV for Select (Arabic)
		public string? OptionsEn { get; set; }               // CSV for Select (English; shown when UI is not Arabic)
		public bool Required { get; set; }
		public int SortOrder { get; set; }
		public bool IsActive { get; set; } = true;
	}

	public class CrmCustomFieldValue
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int FieldId { get; set; }
		public string EntityType { get; set; } = "";
		public int EntityId { get; set; }
		public string? Value { get; set; }
	}
}
