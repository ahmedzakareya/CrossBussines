namespace CrossBuy.Models.Context.Crm
{
	// CRM 3-7b(ii) — automation rules (trigger → action). No GL impact.
	// Triggers: LeadCreated | OpportunityStageChanged. Actions: Notify (owner) | CreateActivity.
	public class CrmAutomationRule
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }                          // optional English name (shown when UI is not Arabic)
		public string TriggerType { get; set; } = "LeadCreated";     // LeadCreated | OpportunityStageChanged
		public string? StageFilter { get; set; }                     // OpportunityStageChanged: only when new stage == this (null = any)
		public string ActionType { get; set; } = "CreateActivity";   // Notify | CreateActivity
		// CreateActivity params
		public string? ActivityType { get; set; }                    // Call | Meeting | Email | Task
		public string? Subject { get; set; }
		public string? SubjectEn { get; set; }                       // English subject for the created activity
		public int DueInDays { get; set; }
		// Notify params
		public string? NotifyTitle { get; set; }
		public string? NotifyBody { get; set; }
		public bool IsActive { get; set; } = true;
		public int SortOrder { get; set; }
	}
}
