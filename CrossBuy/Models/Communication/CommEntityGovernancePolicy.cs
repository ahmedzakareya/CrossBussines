namespace CrossBuy.Models.Communication;

// D-06/D-07 declarations do not activate an archive or erasure worker.
public sealed class CommEntityGovernancePolicy
{
    public int? RetentionDays { get; set; }
    public int? ArchiveAfterDays { get; set; }
    public bool LegalHold { get; set; }
    public string ArchiveBehavior => "ManualOnly";
    public string DeletionBehavior => "NoPhysicalDeletion";
    public string AttachmentRetention => "PreserveWithComment";
    public bool RequiresEditHistory => true;
    public bool RequiresDeletionHistory => true;
    public bool RequiresAttachmentAudit => true;
    public bool RequiresImmutableHistory => true;
    public bool RequiresReadReceipts => true;

    public void Validate()
    {
        if (RetentionDays is <= 0 || ArchiveAfterDays is <= 0 ||
            (RetentionDays.HasValue && ArchiveAfterDays > RetentionDays))
            throw new CommValidationException("governance_policy_invalid",
                "Retention and archive declarations must be positive and internally consistent.");
    }

    public void RequireMutable()
    {
        Validate();
        if (LegalHold)
            throw new CommValidationException("entity_legal_hold",
                "The entity's communication history is on legal hold.");
    }
}
