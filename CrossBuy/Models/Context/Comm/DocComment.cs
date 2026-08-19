namespace CrossBuy.Models.Context.Comm
{
    // Communication Hub P5 — a comment on any document (generalizes Crm.Activity). Renders as a timeline
    // partial that drops into any document view via (EntityType, EntityId).
    public class DocComment : BaseEntity
    {
        public int Id { get; set; }
        public int CompanyID { get; set; }
        public string EntityType { get; set; } = "";
        public int EntityId { get; set; }
        public string Body { get; set; } = "";
        public DateTime? DeletedAt { get; set; }
    }
}
