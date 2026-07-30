namespace CrossBuy.Models.Context.Tasks
{
	// TM-7: auto-task generation. A periodic TaskGeneratorHostedService scans system state against these RULES and creates
	// tasks — operational only (no GL/stock). Rules are toggleable; a per-(rule,source) key prevents duplicates.
	public class TaskAutoRule
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RuleType { get; set; } = "";   // LowStock | OverdueInvoice | WorkOrderQc | DeliveryReady | NewEmployeeOnboard
		public bool IsActive { get; set; } = true;
		public int? DefaultAssigneeEmployeeId { get; set; }   // optional default owner (else the task is unassigned for a manager to route)
		public DateTime CreatedAt { get; set; }
	}

	// one row per (rule, source record) already turned into a task — the double-generation guard (like PosSyncLog.LocalGuid).
	public class TaskAutoLog
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public string RuleKey { get; set; } = "";   // e.g. "WorkOrderQc:123"  (unique per company)
		public int TaskId { get; set; }
		public DateTime CreatedAt { get; set; }
	}
}
