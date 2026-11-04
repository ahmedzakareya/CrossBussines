namespace CrossBuy.Models.Context.Communication
{
    // =============================================================================================
    // Communication ↔ Tasks — THE EXPLICIT LINK (Phase 3).
    //
    // WHY A TABLE AND NOT A COLUMN. TaskItem.EntityType/EntityId already anchors a task to ONE business
    // record, and that anchor is the task's own meaning — the invoice it is about. Linking a task to a
    // conversation by rewriting that pair would destroy the anchor it already had, and it could only ever
    // hold one link. The requirement is many tasks per conversation AND many conversations per task, which
    // is a relationship, not an attribute.
    //
    // WHY NOT THE AUDIT LOG. CommAuditEntries records that a task was RAISED from a message, and that row
    // is the right place for that fact. But an audit log is append-only and is not an index: a link can be
    // removed, and "which tasks are on this conversation" must be a keyed read, not a scan of history.
    //
    // NOT MAPPED YET, AND THAT IS DELIBERATE. This type is not in CommunicationModel.Configure, because
    // CommunicationSchemaParityTests holds the EF model and deploy/sql/communication_platform_slice_001.sql
    // to each other exactly: a table mapped here and absent from that script fails the build. The script
    // lives under deploy/, which is shared with TAB-1 and is hash-coupled to the SQL manifest. So the DDL
    // is delivered for review and this type waits for it — see ICommTaskLinkStore, whose default
    // implementation refuses rather than pretending the storage is there.
    // =============================================================================================
    public class CommTaskLink
    {
        public long Id { get; set; }

        // Leads every index. Two companies may each link their own task 7 to their own thread 12, and those
        // are different links — without CompanyID first, the first company to create a pair would block the
        // second, which is the same reasoning the Direct-conversation key slice records.
        public int CompanyID { get; set; }

        public int TaskId { get; set; }
        public long ThreadId { get; set; }

        // PROVENANCE, NOT IDENTITY. The message the link came from, when there was one; NULL means the
        // link was made against the conversation as a whole.
        //
        // It is deliberately OUTSIDE the uniqueness rule. The live relationship is (CompanyID, TaskId,
        // ThreadId): a task is either attached to a conversation or it is not, and it cannot be attached
        // five times because five messages mentioned it. Putting CommentId in the key would permit exactly
        // that — duplicate relationships wearing different provenance. The first link wins and keeps its
        // provenance; a later attempt from another message returns the existing row.
        public long? CommentId { get; set; }

        // How the link came to exist: CommTaskLinkOrigin. Kept because "created from this message" and
        // "somebody attached an existing task later" answer different questions in an investigation, and a
        // nullable CommentId alone cannot distinguish them.
        public string Origin { get; set; } = "";

        public int? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }

        // Soft removal. An unlink is a decision somebody made, and a row that vanishes takes the fact that
        // it was ever linked with it — the same reason CommComments soft-delete.
        public DateTime? RemovedAt { get; set; }
        public int? RemovedBy { get; set; }
    }
}
