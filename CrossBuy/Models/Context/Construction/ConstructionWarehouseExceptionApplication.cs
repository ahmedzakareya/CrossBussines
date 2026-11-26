using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;
public sealed class ConstructionWarehouseExceptionBinding
{
 public int ID {get;set;} public int CompanyID {get;set;} public int ProjectId {get;set;} public int PurchaseOrderId {get;set;}
 public int ApprovalRequestId {get;set;} public int DecisionId {get;set;} public string SourceFactsVersion {get;set;}="";
 public Guid RequestId {get;set;} public string RequestHash {get;set;}=""; public int CreatedBy {get;set;} public DateTime CreatedAt {get;set;}
}
public sealed class ConstructionWarehouseExceptionApplication
{
 public int ID {get;set;} public int CompanyID {get;set;} public int ProjectId {get;set;} public int PurchaseOrderId {get;set;}
 public int BindingId {get;set;} public int AdmissionId {get;set;} public Guid RequestId {get;set;} public string ReviewVersion {get;set;}="";
 public int CreatedBy {get;set;} public DateTime CreatedAt {get;set;}
}
public static class ConstructionWarehouseExceptionApplicationModel
{
 public static void Configure(ModelBuilder b,CrossDbContext db,bool sql)
 {
  b.Entity<ConstructionWarehouseExceptionBinding>(e=>{
   e.ToTable("ConstructionWarehouseExceptionBindings",t=>{if(sql)t.UseSqlOutputClause(false);});e.HasKey(x=>x.ID);e.HasAlternateKey(x=>new{x.ID,x.PurchaseOrderId,x.ProjectId,x.CompanyID});
   e.HasIndex(x=>new{x.CompanyID,x.RequestId}).IsUnique();e.HasIndex(x=>new{x.CompanyID,x.DecisionId}).IsUnique();e.HasIndex(x=>x.ApprovalRequestId).IsUnique();
   e.HasOne<ConstructionWarehouseExceptionDecision>().WithMany().HasForeignKey(x=>new{x.DecisionId,x.ProjectId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionPurchaseApprovalRequest>().WithMany().HasForeignKey(x=>new{x.ApprovalRequestId,x.PurchaseOrderId,x.ProjectId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.PurchaseOrderId,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.SourceFactsVersion).HasMaxLength(64).IsRequired();e.Property(x=>x.RequestHash).HasMaxLength(64).IsRequired();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
  b.Entity<ConstructionWarehouseExceptionApplication>(e=>{
   e.ToTable("ConstructionWarehouseExceptionApplications",t=>{if(sql)t.UseSqlOutputClause(false);});e.HasKey(x=>x.ID);
   e.HasIndex(x=>new{x.CompanyID,x.RequestId}).IsUnique();e.HasIndex(x=>x.BindingId).IsUnique();e.HasIndex(x=>x.AdmissionId).IsUnique();
   e.HasOne<ConstructionWarehouseExceptionBinding>().WithMany().HasForeignKey(x=>new{x.BindingId,x.PurchaseOrderId,x.ProjectId,x.CompanyID}).HasPrincipalKey(x=>new{x.ID,x.PurchaseOrderId,x.ProjectId,x.CompanyID}).OnDelete(DeleteBehavior.Restrict);
   e.HasOne<ConstructionPurchaseAdmission>().WithMany().HasForeignKey(x=>x.AdmissionId).OnDelete(DeleteBehavior.Restrict);
   e.Property(x=>x.ReviewVersion).HasMaxLength(64).IsRequired();e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
