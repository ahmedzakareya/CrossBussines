using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;

// Immutable provenance for materialising an independently approved transfer.
public sealed class ConstructionBudgetTransferApplication
{
 public int ID {get;set;}
 public int CompanyID {get;set;}
 public int ProjectId {get;set;}
 public int TransferRequestId {get;set;}
 public int TransferDecisionId {get;set;}
 public int OriginalBudgetVersionId {get;set;}
 public int ResultBudgetVersionId {get;set;}
 public Guid ApplicationId {get;set;}
 public string RequestHash {get;set;}="";
 public string SourceVersion {get;set;}="";
 public string Reason {get;set;}="";
 public int AppliedBy {get;set;}
 public DateTime AppliedAt {get;set;}
}
public static class ConstructionBudgetTransferApplicationModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db,bool sqlServer)
 {
  builder.Entity<ConstructionBudgetTransferApplication>(e=>{
   e.ToTable("ConstructionBudgetTransferApplications",t=>{if(sqlServer)t.UseSqlOutputClause(false);});
   e.HasKey(x=>x.ID);
   e.HasIndex(x=>new{x.CompanyID,x.ApplicationId}).IsUnique();
   e.HasIndex(x=>new{x.CompanyID,x.TransferDecisionId}).IsUnique();
   e.HasIndex(x=>new{x.CompanyID,x.TransferRequestId}).IsUnique();
   e.HasIndex(x=>new{x.CompanyID,x.ResultBudgetVersionId}).IsUnique();
   e.HasOne<ConstructionBudgetTransferRequest>().WithMany().HasForeignKey(x=>new{x.TransferRequestId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetTransferDecision>().WithMany().HasForeignKey(x=>new{x.TransferDecisionId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x=>new{x.OriginalBudgetVersionId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x=>new{x.ResultBudgetVersionId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();
   e.Property(x=>x.SourceVersion).HasMaxLength(64).IsRequired();
   e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
