using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// D-21: absence of a row is an unconfigured company, never an implicit Warning policy.
public sealed class ConstructionBudgetPolicy
{
    public int CompanyID { get; set; }
    public string Mode { get; set; } = "";
    public string Reason { get; set; } = "";
    public int? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] ConcurrencyToken { get; set; } = Guid.NewGuid().ToByteArray();
}

public static class ConstructionBudgetModes
{
    public const string Warning = "Warning";
    public const string ApprovalRequired = "ApprovalRequired";
    public const string HardBlock = "HardBlock";
    public static bool IsKnown(string? mode) => mode is Warning or ApprovalRequired or HardBlock;
}

public static class ConstructionBudgetPolicyModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db)
    {
        builder.Entity<ConstructionBudgetPolicy>(e =>
        {
            e.ToTable("ConstructionBudgetPolicies");
            e.HasKey(x => x.CompanyID);
            e.Property(x => x.CompanyID).ValueGeneratedNever();
            e.Property(x => x.Mode).HasMaxLength(30).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
