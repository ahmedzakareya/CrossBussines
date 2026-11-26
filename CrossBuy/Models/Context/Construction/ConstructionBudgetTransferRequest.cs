using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;

// An immutable request is evidence of intent, never approval or spending authority.
public sealed class ConstructionBudgetTransferRequest
{
 public int ID {get;set;}
 public int CompanyID {get;set;}
 public int ProjectId {get;set;}
 public int BudgetVersionId {get;set;}
 public int FromLineId {get;set;}
 public int ToLineId {get;set;}
 public decimal Amount {get;set;}
 public decimal FromBefore {get;set;}
 public decimal ToBefore {get;set;}
 public decimal FromAfter {get;set;}
 public decimal ToAfter {get;set;}
 public Guid RequestId {get;set;}
 public string RequestHash {get;set;}="";
 public string SourceVersion {get;set;}="";
 public byte[] PolicyToken {get;set;}=Array.Empty<byte>();
 public string Reason {get;set;}="";
 public int CreatedBy {get;set;}
 public DateTime CreatedAt {get;set;}
}
public static class ConstructionBudgetTransferRequestModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db,bool sqlServer)
 {
  builder.Entity<ConstructionBudgetTransferRequest>(e=>{
   e.ToTable("ConstructionBudgetTransferRequests",t=>{if(sqlServer)t.UseSqlOutputClause(false);});
   e.HasKey(x=>x.ID);e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
   e.HasIndex(x=>new{x.CompanyID,x.RequestId}).IsUnique();
   e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x=>new{x.BudgetVersionId,x.ProjectId,x.CompanyID})
    .HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetLine>().WithMany().HasForeignKey(x=>x.FromLineId).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetLine>().WithMany().HasForeignKey(x=>x.ToLineId).OnDelete(DeleteBehavior.Restrict);
   foreach(var name in new[]{nameof(ConstructionBudgetTransferRequest.Amount),nameof(ConstructionBudgetTransferRequest.FromBefore),nameof(ConstructionBudgetTransferRequest.ToBefore),nameof(ConstructionBudgetTransferRequest.FromAfter),nameof(ConstructionBudgetTransferRequest.ToAfter)})e.Property<decimal>(name).HasPrecision(28,8);
   e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();e.Property(x=>x.SourceVersion).HasMaxLength(64).IsRequired();
   e.Property(x=>x.PolicyToken).HasMaxLength(16).IsRequired();e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
