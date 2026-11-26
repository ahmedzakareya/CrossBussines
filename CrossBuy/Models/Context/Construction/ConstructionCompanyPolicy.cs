using Microsoft.EntityFrameworkCore;
namespace CrossBuy.Models.Context.Construction;
public sealed class ConstructionCompanyPolicy
{
 public int CompanyID {get;set;}
 public string PolicyKey {get;set;}="";
 public string Mode {get;set;}="";
 public string Reason {get;set;}="";
 public int UpdatedBy {get;set;}
 public DateTime UpdatedAt {get;set;}
 public byte[] ConcurrencyToken {get;set;}=Guid.NewGuid().ToByteArray();
}
public static class ConstructionCompanyPolicies
{
 public static readonly IReadOnlyDictionary<string,string[]> Choices=new Dictionary<string,string[]>{
  ["BudgetTransfer"]=["Disabled","IndependentApproval"],
  ["CrossSiteWarehouse"]=["Restricted","ApprovedException"],
  ["CostForecast"]=["Disabled","ManualAuthorOwned"]};
 public static bool Valid(string key,string mode)=>Choices.TryGetValue(key,out var choices)&&choices.Contains(mode);
}
public static class ConstructionCompanyPolicyModel
{
 public static void Configure(ModelBuilder builder,CrossDbContext db)
 {
  builder.Entity<ConstructionCompanyPolicy>(e=>{
   e.ToTable("ConstructionCompanyPolicies");e.HasKey(x=>new{x.CompanyID,x.PolicyKey});
   e.Property(x=>x.PolicyKey).HasMaxLength(40);e.Property(x=>x.Mode).HasMaxLength(40).IsRequired();
   e.Property(x=>x.Reason).HasMaxLength(1000).IsRequired();e.Property(x=>x.ConcurrencyToken).HasMaxLength(16).IsRequired().IsConcurrencyToken().ValueGeneratedNever();
   e.HasQueryFilter(x=>db.CompanyScope.AllowsCrossCompany||x.CompanyID==db.CompanyScope.FilterCompanyId);
  });
 }
}
