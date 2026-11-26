using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;

// Append-only authored analytical evidence. No budget, reservation, GL or stock authority.
public sealed class ConstructionCostForecastSnapshot
{
 public int ID {get;set;}
 public int CompanyID {get;set;}
 public int ProjectId {get;set;}
 public int BudgetVersionId {get;set;}
 public int CurrencyId {get;set;}
 public int CurrencyDecimals {get;set;}
 public int? PreviousSnapshotId {get;set;}
 public Guid RequestId {get;set;}
 public string RequestHash {get;set;}="";
 public string SourceVersion {get;set;}="";
 public byte[] PolicyToken {get;set;}=Array.Empty<byte>();
 public string Method {get;set;}="";
 public string SnapshotJson {get;set;}="";
 public decimal EstimateAtCompletion {get;set;}
 public decimal BudgetVariance {get;set;}
 public bool HasCommitmentWarning {get;set;}
 public string Reason {get;set;}="";
 public int CreatedBy {get;set;}
 public DateTime CreatedAt {get;set;}
}
public static class ConstructionCostForecastSnapshotModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db,bool sqlServer)
 {
  builder.Entity<ConstructionCostForecastSnapshot>(e=>{
   e.ToTable("ConstructionCostForecastSnapshots",t=>{if(sqlServer)t.UseSqlOutputClause(false);});e.HasKey(x=>x.ID);
   e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
   e.HasIndex(x=>new{x.CompanyID,x.RequestId}).IsUnique();
   e.HasIndex(x=>new{x.CompanyID,x.ProjectId}).IsUnique().HasFilter("[PreviousSnapshotId] IS NULL");
   e.HasIndex(x=>new{x.CompanyID,x.ProjectId,x.PreviousSnapshotId}).IsUnique().HasFilter("[PreviousSnapshotId] IS NOT NULL");
   e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x=>new{x.BudgetVersionId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionCostForecastSnapshot>().WithMany().HasForeignKey(x=>new{x.PreviousSnapshotId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.Method).HasMaxLength(64).IsRequired();e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();
   e.Property(x=>x.SourceVersion).HasMaxLength(64).IsRequired();e.Property(x=>x.PolicyToken).HasMaxLength(16).IsRequired();
   e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();e.Property(x=>x.SnapshotJson).IsRequired();
   e.Property(x=>x.EstimateAtCompletion).HasPrecision(28,8);e.Property(x=>x.BudgetVariance).HasPrecision(28,8);
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
