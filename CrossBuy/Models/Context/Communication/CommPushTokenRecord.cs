namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // CommPushTokens — one registered device endpoint for one employee. (Module 21, gap G3.)
    //
    // THE ENTITY IS NOT THE CONTRACT. CrossBuy.Models.Communication.CommPushToken is the frozen,
    // adapter-facing shape and is what ICommPushTokenStore hands out; this type is the row. They are
    // deliberately different: the row carries revocation evidence and audit columns that an adapter
    // has no business seeing, and keeping them separate is what stops a token's history leaking into
    // a payload. The two names differ for the same reason — a file that uses both must be able to say
    // which it means.
    //
    // A TOKEN IS A CREDENTIAL. Anyone holding one can push to that device, so three rules live in the
    // shape rather than in the callers (the same three deploy/sql/comm_push_tokens.sql states):
    //   * It is never projected into a read model, a DTO for a screen, or a report.
    //   * Revocation is a SOFT retire. A deleted row cannot be told apart from one never created, and
    //     "when did this device stop working" is the question that is actually asked.
    //   * Uniqueness is per company and filtered on live rows, so a device that re-registers after
    //     revocation inserts a new row instead of overwriting the evidence.
    //
    // NO FOREIGN KEY TO Employee, for the reason communication_platform_slice_001.sql states for every
    // employee id in this platform: a hard FK would make a device registration block an HR cleanup, and
    // EmployeeId is already company-scoped by the predicate every read applies.
    // =============================================================================================
    public class CommPushTokenRecord
    {
        public long Id { get; set; }

        // Leads every index. Two companies may legitimately hold the same employee id, and a token is
        // only ever resolved inside one.
        public int CompanyID { get; set; }
        public int EmployeeId { get; set; }

        // CommPushPlatform — "web" | "android" | "ios". Constrained in the database by
        // CK_CommPushTokens_Platform as well as here, because an adapter must never be handed a
        // platform it has no transport for.
        public string Platform { get; set; } = "";

        // The provider's device token. A WebPush endpoint is a URL and is the long case.
        public string Token { get; set; } = "";

        public DateTime? RegisteredAt { get; set; }
        public DateTime? LastSeenAt { get; set; }

        // Set when the provider reports the token dead, or the employee signs the device out.
        public DateTime? RevokedAt { get; set; }
        public string? RevokedReason { get; set; }

        public int? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
    }
}
