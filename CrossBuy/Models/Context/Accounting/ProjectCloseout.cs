namespace CrossBuy.Models.Context.Accounting
{
    // =============================================================================================
    // PROJECT CLOSEOUT — closing a project is an EVENT, not a column.
    //
    // WHY THIS IS ITS OWN TABLE rather than four columns on Project.
    //
    // A project can be closed, reopened because a late invoice arrived, and closed again. Columns on
    // Project record only the LAST of those, so the second close silently overwrites the evidence of
    // the first: who closed it in March, on what grounds, and why it had to be reopened in April all
    // disappear. A closeout is a decision somebody took, and decisions accumulate.
    //
    // It also keeps `Project` untouched. That entity is read by nearly every screen in the module, and
    // adding columns to it means every one of those queries selects a column that does not exist until
    // the DDL is executed. A new table cannot break a query nobody has written yet.
    //
    // Project.Status still moves to Completed on close - that column already exists and is what the
    // existing screens read - but the EVIDENCE lives here.
    // =============================================================================================
    public class ProjectCloseout
    {
        public int ID { get; set; }

        /// The isolation key. Never nullable and never defaulted.
        public int CompanyID { get; set; }

        public int ProjectId { get; set; }

        // ---- the close ----
        public DateTime ClosedAt { get; set; }

        /// Server-side actor, taken from the resolved BusinessContext. Never a posted value.
        public int ClosedBy { get; set; }

        /// Mandatory. A closeout with no stated grounds is an audit trail that records only that
        /// somebody had the right to do it.
        public string Reason { get; set; } = "";

        /// A snapshot of what the readiness check saw at the moment of closing, so a later reader can
        /// tell whether the project closed clean or closed with warnings that were accepted.
        public string? ReadinessNote { get; set; }

        // ---- the reopen, when there is one ----
        //
        // Nullable rather than a second table: a close is reopened at most once, and the row that was
        // closed is exactly the row that gets reopened. A NEW close after a reopen writes a NEW row,
        // which is what keeps the history readable as a sequence.
        public DateTime? ReopenedAt { get; set; }
        public int? ReopenedBy { get; set; }
        public string? ReopenReason { get; set; }

        /// True while this closeout is the operative one. A reopened closeout stays in the table as
        /// history and stops being current.
        public bool IsCurrent { get; set; } = true;
    }
}
