using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
internal static class ConstructionWarehouseExceptionRequirement
{
 // Explicit intent or proven foreign-project mapping requires the qualified path.
 // This does not invent a destination for historical/unmapped sources.
 internal static async Task<bool> RequiredAsync(CrossDbContext db,int company,int project,int order,CancellationToken ct)
 {
  if(await db.Set<ConstructionWarehouseExceptionRequest>().AsNoTracking().AnyAsync(x=>x.CompanyID==company&&x.ProjectId==project&&x.PurchaseOrderId==order,ct))return true;
  var warehouse=await db.PurchaseOrders.AsNoTracking().Where(x=>x.CompanyID==company&&x.ProjectId==project&&x.ID==order).Select(x=>x.WarehouseId).SingleOrDefaultAsync(ct);
  var id=warehouse.GetValueOrDefault();
  return id>0&&await db.Set<ConstructionSite>().AsNoTracking().AnyAsync(x=>x.CompanyID==company&&x.WarehouseId==id&&x.ProjectId!=project,ct);
 }
}
