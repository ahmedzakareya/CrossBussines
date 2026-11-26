using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Construction;

// Effective cost-exposure mapping only. Never replaces admission or authorizes a purchase writer.
public sealed class ConstructionReservationExposureRebase
{
    public int ID { get; set; }
    public int CompanyID { get; set; }
    public int ProjectId { get; set; }
    public int ReservationId { get; set; }
    public int RebaseRequestId { get; set; }
    public int TargetBudgetVersionId { get; set; }
    public string SourceVersion { get; set; } = "";
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class ConstructionReservationExposureRebaseModel
{
    public static void Configure(ModelBuilder builder, CrossDbContext db, bool sqlServer)
    {
        builder.Entity<ConstructionReservationExposureRebase>(e =>
        {
            e.ToTable("ConstructionReservationExposureRebases", t => { if (sqlServer) t.UseSqlOutputClause(false); });
            e.HasKey(x => x.ID);
            e.HasAlternateKey(x => new { x.ID, x.ProjectId, x.CompanyID });
            e.HasIndex(x => new { x.CompanyID, x.RequestId }).IsUnique();
            // Only one direct-successor transition is qualified. No implicit multi-generation chain.
            e.HasIndex(x => new { x.CompanyID, x.ReservationId }).IsUnique();
            e.HasOne<ConstructionCommitmentReservation>().WithMany().HasForeignKey(x => new { x.ReservationId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionReservationRebaseRequest>().WithMany().HasForeignKey(x => new { x.RebaseRequestId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x => new { x.TargetBudgetVersionId, x.ProjectId, x.CompanyID })
                .HasPrincipalKey(x => new { x.ID, x.ProjectId, x.CompanyID }).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.SourceVersion).HasMaxLength(64).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            e.HasQueryFilter(x => db.CompanyScope.AllowsCrossCompany || x.CompanyID == db.CompanyScope.FilterCompanyId);
        });
    }
}
