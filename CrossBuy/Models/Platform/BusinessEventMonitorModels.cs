namespace CrossBuy.Models.Platform
{
    // Stage 0 Batch B — read models for the Business Event Monitor.
    //
    // The monitor is an OPERATIONS screen: support, audit troubleshooting and dispatch diagnosis. It is read-only
    // over BusinessEvents and can re-queue a single consumer's dispatch row through IEventDispatchStore. It can
    // never edit an event, edit a payload, set a status directly, or reset a completed consumer.

    public sealed class BusinessEventMonitorFilter
    {
        public int? CompanyId { get; init; }          // ignored unless the caller may cross companies
        public int? BranchId { get; init; }
        public string? EntityType { get; init; }      // validated against IEntityRegistry
        public int? EntityId { get; init; }
        public string? EventType { get; init; }
        public string? Consumer { get; init; }        // validated against BusinessEventConsumers.Registered
        public string? DispatchStatus { get; init; }  // validated against BusinessEventDispatchStatus
        public DateTime? DateFrom { get; init; }
        public DateTime? DateTo { get; init; }
        public Guid? CorrelationId { get; init; }
        public Guid? EventUid { get; init; }
        public bool? HasError { get; init; }
        public int? MinAttempts { get; init; }

        public int Page { get; init; } = 1;
        public int PageSize { get; init; } = 25;

        public const int MaxPageSize = 100;

        // Normalised copy — the service never trusts raw input for paging.
        public BusinessEventMonitorFilter Normalised() => new()
        {
            CompanyId = CompanyId, BranchId = BranchId, EntityType = Trim(EntityType), EntityId = EntityId,
            EventType = Trim(EventType), Consumer = Trim(Consumer), DispatchStatus = Trim(DispatchStatus),
            DateFrom = DateFrom, DateTo = DateTo, CorrelationId = CorrelationId, EventUid = EventUid,
            HasError = HasError, MinAttempts = MinAttempts,
            Page = Page < 1 ? 1 : Page,
            PageSize = PageSize < 1 ? 25 : (PageSize > MaxPageSize ? MaxPageSize : PageSize),
        };
        private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    // One row = one (event, consumer) pair, because that is the unit an operator acts on.
    public sealed class BusinessEventMonitorRow
    {
        public required long EventId { get; init; }
        public required Guid EventUid { get; init; }
        public required string EventType { get; init; }
        public required string EntityType { get; init; }
        public required int EntityId { get; init; }
        public string? EntityLabel { get; init; }        // resolved through IEntityRegistry when possible
        public string? EntityUrl { get; init; }          // null when the type has no screen
        public required int CompanyId { get; init; }
        public int? BranchId { get; init; }
        public int? ActorEmployeeId { get; init; }
        public string? ActorName { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? CompletedAt { get; init; }      // ALL consumers done — distinct from the row's own status
        public required string Visibility { get; init; }

        public long? DispatchId { get; init; }
        public string? Consumer { get; init; }
        public string? DispatchStatus { get; init; }
        public int Attempts { get; init; }
        public DateTime? UpdatedAt { get; init; }
        public string? ErrorSummary { get; init; }       // first line, truncated for the grid
        public bool HasError => !string.IsNullOrEmpty(ErrorSummary);

        // Server-computed so the view never re-implements the rules.
        public bool CanRetry { get; init; }
        public bool RetryNeedsOverride { get; init; }

        // English prose, for logs and the diagnostics JSON. NOT for the screen.
        public string? RetryBlockedReason { get; init; }

        // A STABLE CODE for the same fact, so the view can localise it. The reason strings were English
        // sentences built in the service, which the screen then rendered verbatim — a breach of the project's
        // "every user-facing string via Resources" rule. The code is the resource key suffix
        // (SharedResources: `evt_block_<code>`); the prose above stays for operators reading logs.
        public string? RetryBlockedCode { get; init; }
    }

    public sealed class BusinessEventMonitorPage
    {
        public required IReadOnlyList<BusinessEventMonitorRow> Rows { get; init; }
        public required int Total { get; init; }
        public required int Page { get; init; }
        public required int PageSize { get; init; }
        public int Pages => Total == 0 ? 1 : (int)Math.Ceiling(Total / (double)PageSize);
        public required BusinessEventMonitorSummary Summary { get; init; }

        // The configured retry ceiling. Surfaced so the "Exhausted" summary card can APPLY itself as a filter
        // (Failed + MinAttempts = MaxAttempts) instead of the view hard-coding a number that would silently
        // disagree with BusinessEventDispatchOptions the moment it is tuned.
        public int MaxAttempts { get; init; }
    }

    public sealed class BusinessEventMonitorSummary
    {
        public int Pending { get; init; }
        public int Claimed { get; init; }
        public int Failed { get; init; }
        public int Done { get; init; }
        public int Exhausted { get; init; }      // Failed AND Attempts >= MaxAttempts
        public int TotalEvents { get; init; }    // distinct events in the filtered range
    }

    public sealed class BusinessEventDetailsViewModel
    {
        public required BusinessEventMonitorRow Header { get; init; }
        public required IReadOnlyList<BusinessEventMonitorRow> Consumers { get; init; }

        // Formatted JSON, or null when the caller may not see this payload.
        public string? Payload { get; init; }
        public bool PayloadMasked { get; init; }

        // English prose for logs/JSON; the view localises from PayloadMaskVisibility instead.
        public string? PayloadMaskReason { get; init; }

        // The visibility tier that caused the mask (`Restricted` / `System`), so the view can render a
        // localised explanation rather than echoing an English sentence built in the service.
        public string? PayloadMaskVisibility { get; init; }
        public bool PayloadTruncated { get; init; }
        public int PayloadBytes { get; init; }
        public string? FullError { get; init; }
    }
}
