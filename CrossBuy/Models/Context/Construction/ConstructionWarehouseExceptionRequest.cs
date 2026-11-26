using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;
// Immutable-intent proposal bridge. Destination site is REQUESTED, not inferred historical source data.
public sealed class ConstructionWarehouseExceptionRequest
{
 public int ID {get;set;}
 public int CompanyID {get;set;}
 public int ProjectId {get;set;}
 public int PurchaseOrderId {get;set;}
 public int DraftId {get;set;}
 public int BudgetVersionId {get;set;}
 public int SourceWarehouseId {get;set;}
 public int DestinationSiteId {get;set;}
 public Guid RequestId {get;set;}
 public string RequestHash {get;set;}="";
 public string SourceVersion {get;set;}="";
 public string SourceFactsVersion {get;set;}="";
 public string CreationVersion {get;set;}="";
 public string MappingVersion {get;set;}="";
 public byte[] PolicyToken {get;set;}=Array.Empty<byte>();
 public string Reason {get;set;}="";
 public int CreatedBy {get;set;}
 public DateTime CreatedAt {get;set;}
}
public static class ConstructionWarehouseExceptionRequestModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db,bool sqlServer)
 {
  builder.Entity<ConstructionWarehouseExceptionRequest>(e=>{
   e.ToTable("ConstructionWarehouseExceptionRequests",t=>{if(sqlServer)t.UseSqlOutputClause(false);});e.HasKey(x=>x.ID);
   e.HasAlternateKey(x=>new{x.ID,x.ProjectId,x.CompanyID});
   e.HasIndex(x=>new{x.CompanyID,x.RequestId}).IsUnique();
   e.HasIndex(x=>new{x.CompanyID,x.PurchaseOrderId,x.DestinationSiteId,x.SourceVersion,x.CreatedBy}).IsUnique();
   e.HasOne<ConstructionPurchaseDraft>().WithMany().HasForeignKey(x=>x.DraftId).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x=>new{x.PurchaseOrderId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionSite>().WithMany().HasForeignKey(x=>new{x.DestinationSiteId,x.ProjectId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<Warehouse>().WithMany().HasForeignKey(x=>new{x.SourceWarehouseId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionBudgetVersion>().WithMany().HasForeignKey(x=>new{x.BudgetVersionId,x.ProjectId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();e.Property(x=>x.SourceVersion).HasMaxLength(64).IsRequired();e.Property(x=>x.CreationVersion).HasMaxLength(64).IsRequired();e.Property(x=>x.MappingVersion).HasMaxLength(64).IsRequired();
   e.Property(x=>x.PolicyToken).HasMaxLength(16).IsRequired();e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();
   e.Property(x=>x.SourceFactsVersion).HasMaxLength(64).IsRequired();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
