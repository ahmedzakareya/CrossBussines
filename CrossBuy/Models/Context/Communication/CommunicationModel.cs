using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-030 §5) — THE SINGLE EF MAPPING ENTRY POINT.
    //
    // WHY THIS FILE EXISTS AT ALL
    //
    // CrossDbContext is a shared file that two other active work streams are editing. CLAUDE.md's selective
    // commit rule exists precisely because of that. So this platform takes the smallest possible footprint in
    // it: ONE line in OnModelCreating —
    //
    //     Communication.CommunicationModel.Configure(builder);
    //
    // and NOT fourteen DbSet properties. The entity types enter the model here, which is all EF needs;
    // services reach them through db.Set<T>() (see CommDb). Every table name, key, length and index therefore
    // lives in a file this work stream owns outright, and a merge conflict with the other tabs is a one-line
    // conflict rather than a fourteen-property one.
    //
    // The real structure ships as deploy/sql/communication_platform_slice_001.sql, because migrations are
    // disabled in this project (CLAUDE.md: "Idempotent SQL in deploy/sql, NOT EF migrations"). This mapping
    // exists so that (a) EF generates exactly the names and indexes that script creates, and (b) a SQLite
    // test host can materialise the whole platform from the model alone — the same two reasons the kernel's
    // BusinessEvents mapping gives for existing.
    //
    // KEEPING THE TWO IN STEP is not left to discipline: CommunicationSchemaParityTests reads the SQL script
    // and asserts every table and index named here appears in it.
    // =============================================================================================
    public static class CommunicationModel
    {
        // Column length budget, declared once. Entity codes match the kernel's 60 so a code that fits
        // BusinessEvents.EntityType fits here; the rest are sized to their frozen vocabularies with room to
        // spare, because widening a column later is cheap and truncating stored data is not.
        public const int EntityCodeLength = 60;
        public const int VocabularyLength = 40;
        public const int DedupKeyLength = 160;
        public const int ThreadKeyLength = 80;
        public const int SubjectLength = 300;
        public const int FileNameLength = 260;
        public const int ContentTypeLength = 180;
        public const int StorageKeyLength = 400;
        public const int LabelLength = 200;
        public const int TemplateKeyLength = 80;
        public const int TitleLength = 300;
        public const int PrincipalKeyLength = 120;

        public static void Configure(ModelBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            ConfigureThreads(builder);
            ConfigureComments(builder);
            ConfigureParticipation(builder);
            ConfigureNotifications(builder);
            ConfigureAudit(builder);
        }

        // ---------------------------------------------------------------------------------------------
        private static void ConfigureThreads(ModelBuilder builder)
        {
            builder.Entity<CommThread>(e =>
            {
                e.ToTable("CommThreads");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.Kind).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.ThreadKey).HasMaxLength(ThreadKeyLength).IsRequired();
                e.Property(x => x.Visibility).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.SubjectAr).HasMaxLength(SubjectLength);
                e.Property(x => x.SubjectEn).HasMaxLength(SubjectLength);
                e.Property(x => x.LockedReason).HasMaxLength(SubjectLength);

                // THE identity of a thread. Unique so "get or create the discussion for this invoice" is a
                // race the DATABASE settles, not the application: two simultaneous first comments on the same
                // record would otherwise create two threads and split the conversation permanently.
                e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.Kind, x.ThreadKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommThreads_Anchor");

                // "Everything happening on this record, newest first" — the anchor read.
                e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.LastActivityAt })
                    .HasDatabaseName("IX_CommThreads_Activity");
            });

            builder.Entity<CommThreadPermission>(e =>
            {
                e.ToTable("CommThreadPermissions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.PrincipalKind).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.PrincipalKey).HasMaxLength(PrincipalKeyLength);
                e.Property(x => x.Level).HasMaxLength(VocabularyLength).IsRequired();

                // The authorization read: every live grant on a thread. Filtered to live rows because a
                // revoked grant must never influence a decision and would otherwise bloat the index forever.
                e.HasIndex(x => new { x.ThreadId, x.PrincipalKind, x.PrincipalId })
                    .HasDatabaseName("IX_CommThreadPermissions_Thread")
                    .HasFilter("[RevokedAt] IS NULL");
            });
        }

        // ---------------------------------------------------------------------------------------------
        private static void ConfigureComments(ModelBuilder builder)
        {
            builder.Entity<CommComment>(e =>
            {
                e.ToTable("CommComments");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.Body).IsRequired();
                e.Property(x => x.BodyFormat).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Visibility).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.DedupKey).HasMaxLength(DedupKeyLength);

                // The thread read: page a conversation in authoring order.
                e.HasIndex(x => new { x.ThreadId, x.Id })
                    .HasDatabaseName("IX_CommComments_Thread");

                // The record read, used by the timeline aggregator without touching CommThreads.
                e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.CreatedAt })
                    .HasDatabaseName("IX_CommComments_Entity");

                // Reply fan-out. Filtered to actual replies — most comments are top-level, so an unfiltered
                // index would be mostly NULLs.
                e.HasIndex(x => x.ParentCommentId)
                    .HasDatabaseName("IX_CommComments_Parent")
                    .HasFilter("[ParentCommentId] IS NOT NULL");

                // Idempotency, per company. Same filtered-unique construction as UX_BusinessEvents_DedupKey,
                // for the same reason: the key is optional, and NULLs must not collide with each other.
                e.HasIndex(x => new { x.CompanyID, x.DedupKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommComments_DedupKey")
                    .HasFilter("[DedupKey] IS NOT NULL");
            });

            builder.Entity<CommCommentRevision>(e =>
            {
                e.ToTable("CommCommentRevisions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Body).IsRequired();
                e.Property(x => x.BodyFormat).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Visibility).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Reason).HasMaxLength(SubjectLength);

                // Unique so a concurrent double-edit cannot write two revision 3s and make the history
                // unorderable.
                e.HasIndex(x => new { x.CommentId, x.RevisionNo })
                    .IsUnique()
                    .HasDatabaseName("UX_CommCommentRevisions_Comment");
            });

            builder.Entity<CommCommentAttachment>(e =>
            {
                e.ToTable("CommCommentAttachments");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.FileName).HasMaxLength(FileNameLength).IsRequired();
                e.Property(x => x.ContentType).HasMaxLength(ContentTypeLength).IsRequired();
                e.Property(x => x.StorageKey).HasMaxLength(StorageKeyLength).IsRequired();
                e.Property(x => x.ThumbnailStorageKey).HasMaxLength(StorageKeyLength);
                e.Property(x => x.PreviewKind).HasMaxLength(VocabularyLength).IsRequired();

                e.HasIndex(x => x.CommentId).HasDatabaseName("IX_CommCommentAttachments_Comment");
                e.HasIndex(x => new { x.CompanyID, x.ThreadId })
                    .HasDatabaseName("IX_CommCommentAttachments_Thread");
            });

            builder.Entity<CommReaction>(e =>
            {
                e.ToTable("CommReactions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.ReactionKey).HasMaxLength(VocabularyLength).IsRequired();

                // One reaction key per person per comment. The DATABASE enforces it, so a double-click cannot
                // produce two "like" rows and a count of 2 from one person.
                e.HasIndex(x => new { x.CommentId, x.EmployeeId, x.ReactionKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommReactions_Unique");
            });
        }

        // ---------------------------------------------------------------------------------------------
        private static void ConfigureParticipation(ModelBuilder builder)
        {
            builder.Entity<CommParticipant>(e =>
            {
                e.ToTable("CommParticipants");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.Role).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Source).HasMaxLength(VocabularyLength).IsRequired();

                // One LIVE participation per (thread, employee). Filtered on RemovedAt so an employee who
                // left and rejoined has one live row and a full history of both.
                e.HasIndex(x => new { x.ThreadId, x.EmployeeId })
                    .IsUnique()
                    .HasDatabaseName("UX_CommParticipants_Live")
                    .HasFilter("[RemovedAt] IS NULL");

                // "What am I following" — the personal read.
                e.HasIndex(x => new { x.CompanyID, x.EmployeeId, x.Role })
                    .HasDatabaseName("IX_CommParticipants_Employee")
                    .HasFilter("[RemovedAt] IS NULL");
            });

            builder.Entity<CommMention>(e =>
            {
                e.ToTable("CommMentions");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.TargetKind).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.TargetKey).HasMaxLength(PrincipalKeyLength);
                e.Property(x => x.LabelAr).HasMaxLength(LabelLength);
                e.Property(x => x.LabelEn).HasMaxLength(LabelLength);

                e.HasIndex(x => x.CommentId).HasDatabaseName("IX_CommMentions_Comment");

                // The same target must not be mentioned twice in one comment — the service dedupes the union
                // of parsed and explicit mentions, and this index is what makes that guarantee real rather
                // than best-effort. TargetKey is in the key so two different Role mentions still fit.
                e.HasIndex(x => new { x.CommentId, x.TargetKind, x.TargetId, x.TargetKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommMentions_PerComment");
            });

            builder.Entity<CommMentionRecipient>(e =>
            {
                e.ToTable("CommMentionRecipients");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.ViaKind).HasMaxLength(VocabularyLength).IsRequired();

                // "Where have I been mentioned", newest first (module 15) — one indexed read.
                e.HasIndex(x => new { x.CompanyID, x.EmployeeId, x.Id })
                    .HasDatabaseName("IX_CommMentionRecipients_Employee");

                // A group and a direct mention in the same comment must resolve to ONE row per employee, or
                // the mention badge counts them twice.
                e.HasIndex(x => new { x.MentionId, x.EmployeeId })
                    .IsUnique()
                    .HasDatabaseName("UX_CommMentionRecipients_Unique");

                // Unread mention badge.
                e.HasIndex(x => new { x.CompanyID, x.EmployeeId, x.ReadAt })
                    .HasDatabaseName("IX_CommMentionRecipients_Unread")
                    .HasFilter("[ReadAt] IS NULL");
            });

            builder.Entity<CommReadReceipt>(e =>
            {
                e.ToTable("CommReadReceipts");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();

                e.HasIndex(x => new { x.ThreadId, x.EmployeeId })
                    .IsUnique()
                    .HasDatabaseName("UX_CommReadReceipts_Unique");
            });
        }

        // ---------------------------------------------------------------------------------------------
        private static void ConfigureNotifications(ModelBuilder builder)
        {
            builder.Entity<CommNotification>(e =>
            {
                e.ToTable("CommNotifications");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.TemplateKey).HasMaxLength(TemplateKeyLength).IsRequired();
                e.Property(x => x.Category).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Priority).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.LegacyType).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.TitleAr).HasMaxLength(TitleLength).IsRequired();
                e.Property(x => x.TitleEn).HasMaxLength(TitleLength).IsRequired();
                e.Property(x => x.Url).HasMaxLength(StorageKeyLength);
                e.Property(x => x.DedupKey).HasMaxLength(DedupKeyLength).IsRequired();

                // TRUE idempotency, per company — not the legacy service's unread-noise guard. A retried
                // transaction produces zero extra rows whether or not the first was read.
                e.HasIndex(x => new { x.CompanyID, x.DedupKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommNotifications_DedupKey");

                // The inbox read: unread first, newest first.
                e.HasIndex(x => new { x.CompanyID, x.RecipientEmployeeId, x.ReadAt, x.Id })
                    .HasDatabaseName("IX_CommNotifications_Inbox");
            });

            builder.Entity<CommNotificationDelivery>(e =>
            {
                e.ToTable("CommNotificationDeliveries");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Channel).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Status).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.DedupKey).HasMaxLength(DedupKeyLength).IsRequired();
                e.Property(x => x.ExternalReference).HasMaxLength(LabelLength);

                // One delivery per (notification, channel). Without this, a retried DECIDE step would create
                // a second Pending row and the recipient would get two emails.
                e.HasIndex(x => new { x.NotificationId, x.Channel })
                    .IsUnique()
                    .HasDatabaseName("UX_CommNotificationDeliveries_Channel");

                // THE CLAIM INDEX. Column order matches the claim predicate (Status first, then the counters
                // and clocks it compares), and it is FILTERED to rows that can still be worked — a Sent row
                // leaves the index entirely, so the queue index stays small no matter how much is delivered.
                // Exactly the construction comm_outbox_slice_003.sql chose for IX_CommMessages_Dispatch.
                e.HasIndex(x => new { x.Status, x.Attempts, x.ClaimedAt, x.UpdatedAt })
                    .HasDatabaseName("IX_CommNotificationDeliveries_Dispatch")
                    .HasFilter("[Status] <> 'Sent' AND [Status] <> 'Skipped'");
            });

            builder.Entity<CommNotificationPreference>(e =>
            {
                e.ToTable("CommNotificationPreferences");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Category).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Channel).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.Mode).HasMaxLength(VocabularyLength).IsRequired();

                e.HasIndex(x => new { x.CompanyID, x.EmployeeId, x.Category, x.Channel })
                    .IsUnique()
                    .HasDatabaseName("UX_CommNotificationPreferences_Unique");
            });
        }

        // ---------------------------------------------------------------------------------------------
        private static void ConfigureAudit(ModelBuilder builder)
        {
            builder.Entity<CommAuditEntry>(e =>
            {
                e.ToTable("CommAuditEntries");
                e.HasKey(x => x.Id);
                e.Property(x => x.Id).ValueGeneratedOnAdd();
                e.Property(x => x.Action).HasMaxLength(VocabularyLength).IsRequired();
                e.Property(x => x.EventType).HasMaxLength(VocabularyLength);
                e.Property(x => x.EntityType).HasMaxLength(EntityCodeLength).IsRequired();
                e.Property(x => x.Visibility).HasMaxLength(VocabularyLength);
                e.Property(x => x.DedupKey).HasMaxLength(DedupKeyLength);

                // "Everything that happened to this record", the audit read.
                e.HasIndex(x => new { x.CompanyID, x.EntityType, x.EntityId, x.Id })
                    .HasDatabaseName("IX_CommAuditEntries_Entity");

                e.HasIndex(x => new { x.CompanyID, x.ThreadId, x.Id })
                    .HasDatabaseName("IX_CommAuditEntries_Thread");

                // "Everything this person did" — the investigation read.
                e.HasIndex(x => new { x.CompanyID, x.ActorEmployeeId, x.Id })
                    .HasDatabaseName("IX_CommAuditEntries_Actor");

                // Joins a comm audit row to the kernel event row from the same request.
                e.HasIndex(x => x.CorrelationId)
                    .HasDatabaseName("IX_CommAuditEntries_Correlation")
                    .HasFilter("[CorrelationId] IS NOT NULL");

                e.HasIndex(x => new { x.CompanyID, x.DedupKey })
                    .IsUnique()
                    .HasDatabaseName("UX_CommAuditEntries_DedupKey")
                    .HasFilter("[DedupKey] IS NOT NULL");
            });
        }

        // Every table this platform owns, in deployment order (parents before children). Read by
        // CommunicationSchemaParityTests against deploy/sql/communication_platform_slice_001.sql, so the
        // script and the model cannot drift apart unnoticed.
        public static readonly IReadOnlyList<string> TableNames = new[]
        {
            "CommThreads",
            "CommThreadPermissions",
            "CommComments",
            "CommCommentRevisions",
            "CommCommentAttachments",
            "CommReactions",
            "CommParticipants",
            "CommMentions",
            "CommMentionRecipients",
            "CommReadReceipts",
            "CommNotifications",
            "CommNotificationDeliveries",
            "CommNotificationPreferences",
            "CommAuditEntries",
        };
    }
}