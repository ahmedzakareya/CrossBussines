namespace CrossBuy.Models.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-031) — the UNIVERSAL ENTITY REFERENCE (module 33).
    //
    // This is the one type that makes "every business entity supports comments without duplicating code"
    // true rather than aspirational. Every table in this platform is keyed by (EntityType, EntityId) and
    // every service takes this record — so there is no per-module comment service, no per-module timeline
    // service, and no per-module notification producer. Onboarding Accounting, Inventory, CRM, HR, Tasks,
    // Projects, Manufacturing and Support comments (modules 36-43) is therefore CONFIGURATION plus a
    // registry code, not code (see ICommEntitySurface).
    //
    // EntityCode is ALWAYS a canonical IEntityRegistry code. It is not validated in the constructor —
    // this is a model type with no DI — but every service entry point validates it through
    // ICommEntitySurface before a row is written, and rejects an unregistered code. That mirrors what
    // DocCommentService learned in Slice-003: free-text EntityType was "the one surviving instance of the
    // problem ADR-002 exists to prevent".
    // =============================================================================================
    public sealed record CommEntityRef
    {
        public CommEntityRef(string entityCode, int entityId)
        {
            EntityCode = (entityCode ?? "").Trim();
            EntityId = entityId;
        }

        public string EntityCode { get; }
        public int EntityId { get; }

        // Shape-only check. It does NOT prove the code is registered — only ICommEntitySurface can, because
        // only it has the registry. A caller that skips the surface check gets a rejected write, not a row.
        public bool IsWellFormed => !string.IsNullOrWhiteSpace(EntityCode) && EntityId > 0;

        // Stable string identity, used for dedup keys and audit detail. Deliberately the same
        // "Code#id" shape the timeline and event monitor already print, so an operator reading a comm
        // audit row and a kernel event row sees the same notation.
        public string Key => EntityCode + "#" + EntityId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string ToString() => Key;

        // Parses the Key form back. Returns false on anything malformed — never throws, because this is
        // used on stored audit/dedup strings which may predate a vocabulary change.
        public static bool TryParse(string? key, out CommEntityRef? reference)
        {
            reference = null;
            if (string.IsNullOrWhiteSpace(key)) return false;
            int hash = key.LastIndexOf('#');
            if (hash <= 0 || hash == key.Length - 1) return false;
            if (!int.TryParse(key.AsSpan(hash + 1), out int id) || id <= 0) return false;
            reference = new CommEntityRef(key.Substring(0, hash), id);
            return reference.IsWellFormed;
        }
    }
}