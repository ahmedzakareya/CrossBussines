using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;
// Immutable independent consent/refusal. Application requires separate effective-version evidence.
public sealed class ConstructionBudgetTransferDecision
{
 public int ID {get;set;}
 public int CompanyID {get;set;}
 public int ProjectId {get;set;}
 public int TransferRequestId {get;set;}
 public string Outcome {get;set;}="";
 public Guid DecisionId {get;set;}
 public string RequestHash {get;set;}="";
 public string SourceVersion {get;set;}="";
 public string Reason {get;set;}="";
 public int CreatedBy {get;set;}
 public DateTime CreatedAt {get;set;}
}
public static class ConstructionBudgetTransferDecisionModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db,bool sqlServer)
 {
  builder.Entity<ConstructionBudgetTransferDecision>(e=>{
   e.ToTable("ConstructionBudgetTransferDecisions",t=>{if(sqlServer)t.UseSqlOutputClause(false);});
   e.HasKey(x=>x.ID);e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
   e.HasIndex(x=>new{x.CompanyID,x.DecisionId}).IsUnique();e.HasIndex(x=>new{x.CompanyID,x.TransferRequestId}).IsUnique();
   e.HasOne<ConstructionBudgetTransferRequest>().WithMany().HasForeignKey(x=>new{x.TransferRequestId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.Outcome).HasMaxLength(16).IsRequired();e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();
   e.Property(x=>x.SourceVersion).HasMaxLength(64).IsRequired();e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
