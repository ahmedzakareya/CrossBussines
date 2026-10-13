namespace CrossBuy.Models.Platform
{
    // =================================================================================================
    // CRM BUSINESS EVENT PAYLOADS — the contracts, not entity snapshots.
    //
    // PKS-001 payload rules apply: identifiers and business facts only. No files, no binary, no
    // secrets, no tokens, no connection strings, and no unrestricted employee data — an owner is
    // carried as an EMPLOYEE ID, never a name, because a name in a permanent audit row is personal
    // data nobody can revoke.
    //
    // WHAT EACH FIELD IS FOR. A payload earns a field when a consumer would otherwise have to go and
    // read the CRM tables to know what happened — and reading them later answers a DIFFERENT
    // question, because by then the row has moved on. The stage a deal moved OUT of exists nowhere
    // after the move except in this payload.
    //
    // Names are the one exception worth stating: LeadName, OpportunityTitle and AccountName are
    // carried because they are how a human recognises the record in a timeline or a task title, and
    // because the title of the work must be readable even to a consumer that may not read CRM.
    // They are business identifiers the tenant typed about a company, not personal data about an
    // employee.
    //
    // ALL AT VERSION 1. A payload version is the SHAPE's version: a consumer that meets a version
    // higher than it knows must refuse rather than guess. Adding an optional field later stays v1;
    // changing or removing one is what makes a v2.
    // =================================================================================================

    /// Lead.Created / Lead.Updated / Lead.Converted.
    public sealed class LeadEventPayload
    {
        public const int Version = 1;

        public string? LeadName { get; init; }
        public string? CompanyName { get; init; }
        public int? OwnerEmployeeId { get; init; }

        /// The lead's status AFTER the fact, and the one it held before it. PreviousStatus is null on
        /// creation, which is the difference between "it started as New" and "it moved to New".
        public string? Status { get; init; }
        public string? PreviousStatus { get; init; }

        public string? Source { get; init; }
        public decimal? EstimatedValue { get; init; }
        public int? CampaignId { get; init; }

        /// Conversion only: the party the lead became, and the primary contact created with it.
        public int? CrmAccountId { get; init; }
        public int? PrimaryContactId { get; init; }

        /// Updated only. Which fields actually moved, so a consumer does not have to diff two reads.
        public string[]? ChangedFields { get; init; }
    }

    /// Opportunity.Created / Updated / StageChanged / Won / Lost.
    public sealed class OpportunityEventPayload
    {
        public const int Version = 1;

        public string? OpportunityTitle { get; init; }
        public int? CrmAccountId { get; init; }
        public int? LeadId { get; init; }
        public int? OwnerEmployeeId { get; init; }

        public int? PipelineId { get; init; }

        /// The stage AFTER the transition, and the stage it left. StageId is null when the tenant has
        /// configured no pipeline and the legacy stage names are in use — StageName is authoritative
        /// in both worlds, which is why both are carried.
        public int? StageId { get; init; }
        public string? StageName { get; init; }
        public int? PreviousStageId { get; init; }
        public string? PreviousStageName { get; init; }

        public int? Probability { get; init; }
        public decimal? Amount { get; init; }
        public DateTime? ExpectedCloseDate { get; init; }

        /// Won and Lost only, and they are the outcome of THIS transition — never a guess derived
        /// from the stage name, which a tenant may rename to anything.
        public bool? IsWon { get; init; }
        public bool? IsLost { get; init; }
        public string? WinLossReason { get; init; }

        /// Won only: the financial customer the account was linked to. Present because the link had
        /// already succeeded when this event was recorded — see CrmService's Won path, where a link
        /// failure rolls the whole transition back and no event is written at all.
        public int? CustomerId { get; init; }

        public string[]? ChangedFields { get; init; }
    }

    /// CrmAccount.Created / CrmAccount.Updated.
    public sealed class CrmAccountEventPayload
    {
        public const int Version = 1;

        public string? AccountName { get; init; }
        public int? OwnerEmployeeId { get; init; }
        public string? Industry { get; init; }
        public string? Segment { get; init; }

        /// Set when the account already links to a financial customer. Never created by an account
        /// write — that link is made on Won, and this field only reports it.
        public int? CustomerId { get; init; }

        /// Set when the account was created BY a lead conversion rather than typed by hand.
        public int? ConvertedFromLeadId { get; init; }

        public string[]? ChangedFields { get; init; }
    }

    /// Lead.FollowUpScheduled / FollowUpCompleted and the Opportunity pair.
    ///
    /// THE ONE CRM FACT THAT IS WORK. Everything else CRM records is something that HAPPENED — a
    /// lead was edited, a deal moved, an account was created. A CRM Activity of type "Task" with an
    /// owner and a due date is the only place the model says: THIS PERSON must do THIS by THIS DATE.
    /// That sentence is what a Platform Task is, so that fact — and only that fact — becomes one.
    ///
    /// The event is recorded against the LEAD or the OPPORTUNITY the activity is about, not against
    /// the activity, because those are the registered entities and because the follow-up is a fact
    /// about the deal. ActivityId is carried so the work can be identified per follow-up: a lead may
    /// have many, and keying the task on the lead alone would silently collapse them into one.
    public sealed class CrmFollowUpEventPayload
    {
        public const int Version = 1;

        public required int ActivityId { get; init; }
        public string? Subject { get; init; }
        public string? SubjectEn { get; init; }

        /// The activity's own type — "Task" for the work case. Carried so a consumer can tell what
        /// kind of follow-up this was without reading CRM.
        public string? ActivityType { get; init; }

        /// WHO. The activity's owner, and the only assignee a CRM-created task may have.
        public int? OwnerEmployeeId { get; init; }

        /// BY WHEN. The activity's own due date. A follow-up event is never recorded without one, so
        /// no consumer ever has to invent a deadline — which is the whole reason this field is
        /// required rather than optional in practice.
        public DateTime? DueDate { get; init; }

        /// The record the follow-up is about, repeated from the envelope so a payload read on its own
        /// is still self-describing.
        public string? EntityCode { get; init; }
        public int? EntityId { get; init; }
        public string? EntityName { get; init; }
    }
}
