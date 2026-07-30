using System.ComponentModel.DataAnnotations.Schema;

namespace CrossBuy.Models.Context.Crm
{
	// P3-7 CRM core: Lead → Opportunity (pipeline) → Activities. A qualified lead converts to a Customer (AR);
	// a won opportunity can be linked to a quotation. Greenfield module — no GL impact.
	// P3-7 CRM expansion: marketing campaign — leads/opportunities attribute to it; ROI = won value vs budget.
	public class Campaign
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Channel { get; set; }          // Email | Social | Event | Ads | Referral | Other
		public string Status { get; set; } = "Planned"; // Planned | Active | Completed | Cancelled
		public DateTime? StartDate { get; set; }
		public DateTime? EndDate { get; set; }
		public decimal Budget { get; set; }
		public string? Notes { get; set; }
		public int? OwnerEmployeeId { get; set; }      // CRM 3-1: record owner (for data scope + routing)
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class Lead
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }          // English lead name (shown when UI is not Arabic)
		public string? Company { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? Source { get; set; }          // Website / Referral / Campaign / Walk-in …
		public string? Segment { get; set; }
		public decimal EstimatedValue { get; set; }
		public string Status { get; set; } = "New";   // New | Contacted | Qualified | Converted | Lost
		public int Score { get; set; }                 // CRM 3-7: computed from active scoring rules
		public string? Notes { get; set; }
		public int? CustomerId { get; set; }           // set when the account links to a financial customer
		public int? AccountId { get; set; }            // CRM 3-2: converts to a CrmAccount (not a Customer directly)
		public int? CampaignId { get; set; }           // marketing attribution
		public int? OwnerEmployeeId { get; set; }      // CRM 3-1: record owner
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class Opportunity
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }           // optional English title (shown when UI is not Arabic)
		public int? AccountId { get; set; }            // CRM 3-2: opportunity belongs to a CrmAccount
		public int? CustomerId { get; set; }           // set when the account links to a financial customer (on Won)
		public int? PipelineId { get; set; }           // CRM 3-3: configurable pipeline + stage
		public int? StageId { get; set; }
		public string? WinLossReason { get; set; }
		public int? LeadId { get; set; }
		public string Stage { get; set; } = "Prospecting"; // Prospecting | Qualification | Proposal | Negotiation | Won | Lost
		public decimal Amount { get; set; }
		public int Probability { get; set; }              // 0..100
		public DateTime? ExpectedCloseDate { get; set; }
		public string? Notes { get; set; }
		public int? QuotationId { get; set; }
		public int? CampaignId { get; set; }           // marketing attribution
		public int? OwnerEmployeeId { get; set; }      // CRM 3-1: record owner
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	public class Activity
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Type { get; set; } = "Task";        // Call | Meeting | Email | Task
		public string Subject { get; set; } = "";
		public string? SubjectEn { get; set; }             // optional English subject (shown when UI is not Arabic)
		public DateTime? DueDate { get; set; }
		public bool Done { get; set; }
		public int? LeadId { get; set; }
		public int? OpportunityId { get; set; }
		public int? CustomerId { get; set; }
		public int? OwnerEmployeeId { get; set; }      // CRM 3-1: record owner
		// CRM 3-4: polymorphic link so any entity (Lead/Opportunity/Account/Customer/Contact/...) has a timeline.
		public string? EntityType { get; set; }        // "Lead" | "Opportunity" | "Account" | "Customer" | "Contact"
		public int? EntityId { get; set; }
		public DateTime? ReminderAt { get; set; }      // optional reminder; CrmReminderHostedService notifies the owner
		public bool Reminded { get; set; }
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-1: per-employee CRM role. SalesRep (own records) | SalesManager (team) | Marketing | CrmViewer (read).
	public class CrmUserRole
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int EmployeeId { get; set; }
		public string Role { get; set; } = "";
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-2: the 360° party (company/entity). Independent of the financial Customer; links via CustomerId on Won.
	public class CrmAccount
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Industry { get; set; }
		public string? IndustryEn { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public string? Website { get; set; }
		public string? Address { get; set; }
		public string? Source { get; set; }
		public string? Segment { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public int? CustomerId { get; set; }           // financial Customer link (one Account ↔ one Customer)
		public bool IsActive { get; set; } = true;
		public string? Notes { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		[NotMapped] public List<CrmContact> Contacts { get; set; } = new();   // loaded explicitly; not an EF relationship
	}

	// CRM 3-3: a configurable sales pipeline with ordered stages (probability + won/lost flags per stage).
	public class CrmPipeline
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public bool IsDefault { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
		[NotMapped] public List<CrmPipelineStage> Stages { get; set; } = new();   // loaded explicitly
	}

	public class CrmPipelineStage
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int PipelineId { get; set; }
		public string Name { get; set; } = "";     // code; UI localizes
		public string? NameEn { get; set; }
		public int Sort { get; set; }
		public int Probability { get; set; }
		public bool IsWon { get; set; }
		public bool IsLost { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-3b: a product line on an opportunity (feeds the quotation on convert).
	public class OpportunityProduct
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int OpportunityId { get; set; }
		public int? ItemId { get; set; }
		public string? ItemDescription { get; set; }
		public decimal Qty { get; set; } = 1;
		public decimal UnitPrice { get; set; }
		public decimal DiscountPercent { get; set; }
		public decimal LineTotal { get; set; }
	}

	// CRM 3-2: a person belonging to a CrmAccount.
	public class CrmContact
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int AccountId { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Title { get; set; }
		public string? TitleEn { get; set; }
		public string? Phone { get; set; }
		public string? Email { get; set; }
		public bool IsPrimary { get; set; }
		public int? OwnerEmployeeId { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-5: a member enrolled in a campaign (the marketing audience). Polymorphic — a Lead/Account/Contact.
	// Status tracks the response funnel: Targeted → Sent → Opened → Responded → Converted.
	public class CampaignMember
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int CampaignId { get; set; }
		public string EntityType { get; set; } = "";   // "Lead" | "Account" | "Contact"
		public int EntityId { get; set; }
		public string? MemberName { get; set; }         // display snapshot
		public string Status { get; set; } = "Targeted";
		public DateTime? RespondedAt { get; set; }
		public string? Notes { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-5: a reusable static marketing list (named segment). Members can be pushed into a campaign.
	public class CrmMarketingList
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }      // optional English description (shown when UI is not Arabic)
		public bool IsActive { get; set; } = true;
		public int? OwnerEmployeeId { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
		[NotMapped] public List<CrmListMember> Members { get; set; } = new();   // loaded explicitly
	}

	public class CrmListMember
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public int ListId { get; set; }
		public string EntityType { get; set; } = "";   // "Lead" | "Account" | "Contact"
		public int EntityId { get; set; }
		public string? MemberName { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-6: an SLA target per priority — first-response & resolution deadlines (in minutes).
	public class CrmSlaPolicy
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string Priority { get; set; } = "Normal";   // Low | Normal | High | Urgent
		public int FirstResponseMins { get; set; } = 240;
		public int ResolutionMins { get; set; } = 1440;
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-6: a customer-service ticket. SLA due dates are stamped at creation from the matching policy;
	// the conversation/notes reuse the polymorphic Activity timeline (EntityType="Ticket").
	public class CrmTicket
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Subject { get; set; } = "";
		public string? SubjectEn { get; set; }             // optional English subject (shown when UI is not Arabic)
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }         // optional English description
		public int? AccountId { get; set; }
		public int? ContactId { get; set; }
		public int? CustomerId { get; set; }
		public string? Category { get; set; }
		public string? CategoryEn { get; set; }            // optional English category
		public string Priority { get; set; } = "Normal";
		public string Status { get; set; } = "New";        // New | Open | Pending | Resolved | Closed
		public int? OwnerEmployeeId { get; set; }           // assignee (data-scope owner)
		public int? SlaPolicyId { get; set; }
		public DateTime? FirstResponseDueAt { get; set; }
		public DateTime? ResolutionDueAt { get; set; }
		public DateTime? FirstRespondedAt { get; set; }
		public DateTime? ResolvedAt { get; set; }
		public DateTime? ClosedAt { get; set; }
		public string? CreatedBy { get; set; }
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-7: a lead-scoring rule — adds Points when a lead's Field matches Value by Operator.
	public class CrmScoringRule
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public string Name { get; set; } = "";
		public string? NameEn { get; set; }               // optional English name (shown when UI is not Arabic)
		public string Field { get; set; } = "Source";    // Source | Segment | Status | EstimatedValue
		public string Operator { get; set; } = "eq";      // eq | contains | gte
		public string? Value { get; set; }
		public int Points { get; set; }
		public bool IsActive { get; set; } = true;
		public DateTime? CreatedAt { get; set; }
	}

	// CRM 3-7: one settings row per company (routing toggle + score-band thresholds).
	public class CrmSettings
	{
		public int ID { get; set; }
		public int CompanyID { get; set; }
		public bool AutoRouteLeads { get; set; }
		public int HotScore { get; set; } = 50;
		public int WarmScore { get; set; } = 20;
		public DateTime? CreatedAt { get; set; }
	}
}
