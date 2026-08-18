namespace CrossBuy.Models.Context.Comm
{
    // Communication Hub — email (outbox + audit). One row per outgoing message; status drives the folders.
    public class CommMessage : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public string ToAddress { get; set; } = "";
        public string? Cc { get; set; }
        public string Subject { get; set; } = "";
        public string? Body { get; set; }
        public string Status { get; set; } = "Queued";   // Queued | Sent | Failed
        public string? Error { get; set; }
        public DateTime? SentAt { get; set; }
        public int Attempts { get; set; }
        public int? ParentId { get; set; }                // reply/forward source
        public string Kind { get; set; } = "New";         // New | Reply | ReplyAll | Forward
        public bool Starred { get; set; }
        public DateTime? DeletedAt { get; set; }          // Trash

        // ---- Stage 0 (Slice-003) outbox dispatch state, additive + nullable ----
        // CommMessage was already outbox-SHAPED (Status/Attempts/Error/SentAt) but nothing drained it: sending
        // happened synchronously in the request and a Failed row stayed failed forever. These two columns are what
        // a multi-worker dispatcher needs and the table did not have.
        //
        // Status gains "Claimed": Queued -> Claimed -> Sent, or -> Failed -> (retry) -> Claimed.
        // ClaimedAt doubles as the stale-claim clock — a worker that dies holding a row releases it after
        // CommMessageDispatchOptions.StaleClaimMinutes.
        //
        // NOTE: only ClaimedAt is new. UpdatedAt is INHERITED from BaseEntity and is therefore already a column on
        // this table — the dispatcher reuses it as the retry-backoff clock rather than adding a second timestamp
        // that means the same thing. (deploy/sql/comm_outbox_slice_003.sql guards on COL_LENGTH, so it reports
        // UpdatedAt as already present.)
        public DateTime? ClaimedAt { get; set; }
    }

    public class CommAttachment
    {
        public int Id { get; set; }
        public int CommMessageId { get; set; }
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public long Size { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
