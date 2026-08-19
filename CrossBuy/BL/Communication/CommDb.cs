using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §5) — typed access to this platform's tables.
    //
    // WHY NOT DbSet PROPERTIES ON CrossDbContext
    //
    // Because CrossDbContext is a shared file with two other work streams editing it. The entity types are
    // registered by CommunicationModel.Configure, which is all EF needs; `db.Set<T>()` then works exactly as
    // a DbSet property would. This class exists only so the services read `Comm.Comments` instead of
    // `_db.Set<CommComment>()` fourteen times per file — it adds no behaviour and holds no state beyond the
    // context it wraps.
    //
    // It is a STRUCT over the context, not a registered service: it has no lifetime of its own, and making it
    // injectable would put a second thing in DI whose lifetime must match the context's exactly.
    // =============================================================================================
    public readonly struct CommDb
    {
        private readonly CrossDbContext _db;

        public CommDb(CrossDbContext db) => _db = db ?? throw new ArgumentNullException(nameof(db));

        public CrossDbContext Context => _db;

        public DbSet<CommThread> Threads => _db.Set<CommThread>();
        public DbSet<CommThreadPermission> ThreadPermissions => _db.Set<CommThreadPermission>();
        public DbSet<CommComment> Comments => _db.Set<CommComment>();
        public DbSet<CommCommentRevision> Revisions => _db.Set<CommCommentRevision>();
        public DbSet<CommCommentAttachment> Attachments => _db.Set<CommCommentAttachment>();
        public DbSet<CommReaction> Reactions => _db.Set<CommReaction>();
        public DbSet<CommParticipant> Participants => _db.Set<CommParticipant>();
        public DbSet<CommMention> Mentions => _db.Set<CommMention>();
        public DbSet<CommMentionRecipient> MentionRecipients => _db.Set<CommMentionRecipient>();
        public DbSet<CommReadReceipt> ReadReceipts => _db.Set<CommReadReceipt>();
        public DbSet<CommNotification> Notifications => _db.Set<CommNotification>();
        public DbSet<CommNotificationDelivery> Deliveries => _db.Set<CommNotificationDelivery>();
        public DbSet<CommNotificationPreference> Preferences => _db.Set<CommNotificationPreference>();
        public DbSet<CommAuditEntry> Audit => _db.Set<CommAuditEntry>();

        public Task<int> SaveAsync(CancellationToken cancellationToken = default)
            => _db.SaveChangesAsync(cancellationToken);

        // True when the caller has already opened a transaction. Read (never asserted) by the services that
        // write several tables in one operation: they enrol in the ambient transaction when one exists and
        // open their own when it does not, so a comment plus its mentions plus its audit rows always share
        // one fate. Same enrol-never-own discipline BusinessEventService follows, except that service REFUSES
        // to run without one because its caller is always a financial transaction; a comment has no such
        // caller, so this platform owns the transaction when nobody else does.
        public bool HasAmbientTransaction => _db.Database.CurrentTransaction != null;
    }
}