using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // WHICH LINK STORE THIS DEPLOYMENT GETS — DECIDED BY THE DATABASE, NOT BY THE BUILD.
    //
    // THE PROBLEM THIS REPLACES. Until now the choice between EfCommTaskLinkStore and
    // UnprovisionedCommTaskLinkStore was read from CommunicationPlatform:TaskLinksProvisioned, a
    // configuration flag. That made the flag and the CONNECTION STRING a single unit that had to
    // travel together: CommTaskLinks exists on CrossBuyDB2 and not on CrossBuyDev, so a committed
    // `true` without the matching committed connection string hands every clean checkout the Ef store
    // pointed at a database with no table — a SQL-208 on first use, which is precisely the HM-D44
    // shape the default-off flag was introduced to prevent. The connection string is machine-local and
    // excluded from commits, so the flag could never be committed on its own, and the feature's
    // activation lived in a file that cannot be shared.
    //
    // THE FIX IS TO ASK THE DATABASE. CommTaskLinkSchemaProbe already answers "does the database this
    // process is connected to really have CommTaskLinks, with both filtered unique indexes, the check
    // constraint and the foreign key?" It had been registered and uncalled since it was written. This
    // decorator is what finally calls it: one catalogue read per process, cached, and the answer
    // selects the store.
    //
    // THE FLAG SURVIVES, DEMOTED TO A KILL SWITCH. Explicit `false` still forces the feature off
    // without a probe — an operator must be able to disable a feature without a schema change. But the
    // flag can no longer GRANT: absent and `true` both mean "ask the database". So a committed `true`
    // is harmless on an unprovisioned database, which is the whole point.
    //
    // THE CACHING RULE LIVES IN CommSchemaProvisioning, shared with every other optionally-deployed
    // slice in this platform. Read it there — in particular why a failed probe is never remembered.
    // =============================================================================================
    public sealed class CommTaskLinkProvisioning
    {
        private readonly CommSchemaProvisioning _inner;

        public CommTaskLinkProvisioning() : this(() => DateTimeOffset.UtcNow) { }

        // The clock seam, for the cooldown. DI never selects this constructor —
        // Func<DateTimeOffset> is not registered, so the container resolves the parameterless one.
        public CommTaskLinkProvisioning(Func<DateTimeOffset> now)
            => _inner = new CommSchemaProvisioning(CommTaskLinkSchemaProbe.TableName, now);

        public static TimeSpan InconclusiveCooldown => CommSchemaProvisioning.InconclusiveCooldown;

        public string? Reason => _inner.Reason;

        public bool IsProvisioned(ICommTaskLinkSchemaProbe probe, ILogger? log = null)
            => _inner.IsProvisioned(ct => AnswerAsync(probe, ct), log);

        public Task<bool> ResolveAsync(
            ICommTaskLinkSchemaProbe probe, ILogger? log = null, CancellationToken cancellationToken = default)
            => _inner.ResolveAsync(ct => AnswerAsync(probe, ct), log, cancellationToken);

        // The probe's own status type, widened to the shared one. The Inconclusive/absent distinction
        // is carried across deliberately: it is what the shared cache keys its retry rule on, and
        // flattening it here would silently restore the bug that rule exists to prevent.
        private static async Task<CommSchemaAnswer> AnswerAsync(
            ICommTaskLinkSchemaProbe probe, CancellationToken cancellationToken)
        {
            var status = await probe.CheckAsync(cancellationToken);
            return new CommSchemaAnswer
            {
                Provisioned = status.Provisioned,
                Missing = status.Missing,
                Inconclusive = status.Inconclusive,
            };
        }
    }

    // ---------------------------------------------------------------------------------------------
    // THE STORE THAT PICKS A STORE.
    //
    // It owns no persistence of its own: every member delegates to EfCommTaskLinkStore when the schema
    // is there and to UnprovisionedCommTaskLinkStore when it is not. That matters for review — there
    // is still exactly one implementation of each behaviour, and this type adds a decision, not a
    // second way to write a link.
    //
    // THE GATE IS CHECKED ON EVERY MEMBER, not only on IsProvisioned. A caller that skips the gate
    // (TasksController reaches ICommTaskLinkStore directly, TasksController.cs:2051-2052) must still
    // not reach the Ef store against a database without the table — otherwise the decorator would
    // merely move HM-D44 one call deeper.
    // ---------------------------------------------------------------------------------------------
    public sealed class ProbingCommTaskLinkStore : ICommTaskLinkStore
    {
        private static readonly UnprovisionedCommTaskLinkStore Off = new();

        private readonly EfCommTaskLinkStore _persistent;
        private readonly ICommTaskLinkSchemaProbe _probe;
        private readonly CommTaskLinkProvisioning _provisioning;
        private readonly ILogger<ProbingCommTaskLinkStore> _log;

        public ProbingCommTaskLinkStore(
            EfCommTaskLinkStore persistent,
            ICommTaskLinkSchemaProbe probe,
            CommTaskLinkProvisioning provisioning,
            ILogger<ProbingCommTaskLinkStore> log)
        {
            _persistent = persistent;
            _probe = probe;
            _provisioning = provisioning;
            _log = log;
        }

        public bool IsProvisioned => _provisioning.IsProvisioned(_probe, _log);

        private ICommTaskLinkStore Inner => IsProvisioned ? _persistent : (ICommTaskLinkStore)Off;

        public Task<CommTaskLinkRow?> AddAsync(
            int companyId, int taskId, long threadId, long? commentId, string origin, int? createdBy,
            CancellationToken cancellationToken = default)
            => Inner.AddAsync(companyId, taskId, threadId, commentId, origin, createdBy, cancellationToken);

        public Task<IReadOnlyList<CommTaskLinkRow>> ForThreadAsync(
            int companyId, long threadId, CancellationToken cancellationToken = default)
            => Inner.ForThreadAsync(companyId, threadId, cancellationToken);

        public Task<IReadOnlyList<CommTaskLinkRow>> ForTaskAsync(
            int companyId, int taskId, CancellationToken cancellationToken = default)
            => Inner.ForTaskAsync(companyId, taskId, cancellationToken);

        public Task<CommTaskLinkRow?> GetAsync(
            int companyId, long linkId, CancellationToken cancellationToken = default)
            => Inner.GetAsync(companyId, linkId, cancellationToken);

        public Task<bool> RemoveAsync(
            int companyId, long linkId, int? removedBy, CancellationToken cancellationToken = default)
            => Inner.RemoveAsync(companyId, linkId, removedBy, cancellationToken);
    }
}
