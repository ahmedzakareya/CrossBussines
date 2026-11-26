namespace CrossBuy.Models.Communication
{
    // D-05: entity policy narrows authoring; it never grants entity access.
    public sealed class CommEntityPrivacyPolicy
    {
        public string MaximumVisibility { get; set; } = CommVisibility.Internal;
        public string? ExternalPolicyReference { get; set; }
    }
}
