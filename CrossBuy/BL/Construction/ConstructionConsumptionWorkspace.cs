using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;
namespace CrossBuy.BL.Construction;
public interface IConstructionConsumptionWorkspace
{
 Task<ConstructionConsumptionPage?> ReadAsync(int project,CancellationToken ct=default);
 Task<ConstructionConsumptionPreview> PreviewAsync(int project,ConstructionConsumptionInput input,CancellationToken ct=default);
 Task<ConstructionConsumptionWrite> RegisterAsync(int project,ConstructionConsumptionInput input,CancellationToken ct=default);
}
// Explicit analytical linkage only: stock and accounting remain with their existing writers.
public sealed class ConstructionConsumptionWorkspace(CrossDbContext db,IBusinessContextAccessor contexts,
 IProjectsAccessService access,IConstructionMaterialConsumptionService service):IConstructionConsumptionWorkspace
{
 private async Task<BusinessContext?> Actor(int project,CancellationToken ct)
 {
  var c=await contexts.TryGetCurrentAsync(ct);
  return c is { IsAuthenticated:true,IsExternalPortal:false,EmployeeId:>0,CompanyId:>0 } &&
   db.CompanyScope.FilterCompanyId==c.CompanyId && !db.CompanyScope.AllowsCrossCompany &&
   await access.CanAsync(c,ProjectsActions.BudgetManage,new PermissionTarget{ProjectId=project},ct) &&
   await db.Projects.AsNoTracking().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.ID==project,ct)?c:null;
 }
 public async Task<ConstructionConsumptionPage?> ReadAsync(int project,CancellationToken ct=default)
 {
  var c=await Actor(project,ct);if(c==null)return null;
  var p=await db.Projects.AsNoTracking().SingleAsync(x=>x.CompanyID==c.CompanyId&&x.ID==project,ct);
  var rows=await (from r in db.Set<ConstructionMaterialConsumption>().AsNoTracking()
   join currency in db.Currencies.AsNoTracking() on r.FunctionalCurrencyId equals currency.ID into currencies
   from currency in currencies.DefaultIfEmpty()
   where r.CompanyID==c.CompanyId&&r.ProjectId==project orderby r.ID descending
   select new ConstructionConsumptionRow(r.ID,r.GoodsReceiptLineId,r.IssueMovementId,r.QuantityBase,r.NetBaseCost,currency==null?null:currency.Code,r.CreatedAt)).Take(100).ToListAsync(ct);
  // Candidates are discoverable source facts, not an admission certificate. The writer still verifies the full graph.
  var receipts=await (from line in db.GoodsReceiptLines.AsNoTracking()
   join receipt in db.GoodsReceipts.AsNoTracking() on line.GoodsReceiptId equals receipt.ID
   join order in db.PurchaseOrders.AsNoTracking() on receipt.PurchaseOrderId equals (int?)order.ID
   join movement in db.StockMovements.AsNoTracking() on line.StockMovementId equals (int?)movement.ID
   join entry in db.JournalEntries.AsNoTracking() on movement.JournalEntryId equals (int?)entry.ID
   join item in db.Items.AsNoTracking() on movement.ItemId equals item.ID
   join warehouse in db.Warehouses.AsNoTracking() on movement.WarehouseId equals warehouse.ID
   join currency in db.Currencies.AsNoTracking() on entry.FunctionalCurrencyId equals (int?)currency.ID into currencies
   from currency in currencies.DefaultIfEmpty()
   where receipt.CompanyID==c.CompanyId&&order.CompanyID==c.CompanyId&&order.ProjectId==project&&
    movement.CompanyID==c.CompanyId&&entry.CompanyID==c.CompanyId&&item.CompanyID==c.CompanyId&&warehouse.CompanyID==c.CompanyId&&
    receipt.Status=="Posted"&&entry.Status=="Posted"&&movement.Direction==1&&movement.QtyBase>0
   orderby line.ID descending
   select new ConstructionConsumptionSource(line.ID,receipt.ID,item.Name,item.NameEn,warehouse.Name,warehouse.NameEn,
    movement.QtyBase,movement.TotalCost,currency==null?null:currency.Code)).Take(101).ToListAsync(ct);
  var issues=await (from movement in db.StockMovements.AsNoTracking()
   join entry in db.JournalEntries.AsNoTracking() on movement.JournalEntryId equals (int?)entry.ID
   join item in db.Items.AsNoTracking() on movement.ItemId equals item.ID
   join warehouse in db.Warehouses.AsNoTracking() on movement.WarehouseId equals warehouse.ID
   join currency in db.Currencies.AsNoTracking() on entry.FunctionalCurrencyId equals (int?)currency.ID into currencies
   from currency in currencies.DefaultIfEmpty()
   where movement.CompanyID==c.CompanyId&&entry.CompanyID==c.CompanyId&&item.CompanyID==c.CompanyId&&warehouse.CompanyID==c.CompanyId&&
    movement.Direction==-1&&movement.SourceType=="Issue"&&movement.QtyBase>0&&entry.Status=="Posted"&&entry.SourceType=="Inventory"&&entry.SourceId==movement.ID&&
    db.JournalEntryLines.Any(x=>x.JournalEntryId==entry.ID&&x.ProjectId==project)&&
    !db.JournalEntryLines.Any(x=>x.JournalEntryId==entry.ID&&(x.ProjectId==null||x.ProjectId!=project))&&
    db.Set<ConstructionActualCostAllocation>().Any(x=>x.CompanyID==c.CompanyId&&x.ProjectId==project&&x.IsSealed&&
     db.JournalEntryLines.Any(l=>l.ID==x.JournalEntryLineId&&l.JournalEntryId==entry.ID))&&
    !db.Set<ConstructionMaterialConsumption>().Any(x=>x.CompanyID==c.CompanyId&&x.IssueMovementId==movement.ID)
   orderby movement.ID descending
   select new ConstructionConsumptionSource(movement.ID,entry.ID,item.Name,item.NameEn,warehouse.Name,warehouse.NameEn,
    movement.QtyBase,movement.TotalCost,currency==null?null:currency.Code)).Take(101).ToListAsync(ct);
  return new(project,p.Code,p.Name,p.NameEn,rows,new(){RequestId=Guid.NewGuid()},
   Sources:new(receipts.Take(100).ToArray(),issues.Take(100).ToArray(),receipts.Count>100,issues.Count>100));
 }
 private static bool Valid(ConstructionConsumptionInput i)=>i.ReceiptLineId>0&&i.IssueMovementId>0&&i.RequestId!=Guid.Empty&&
  !string.IsNullOrWhiteSpace(i.Reason)&&i.Reason.Length<=1000;
 public async Task<ConstructionConsumptionPreview> PreviewAsync(int project,ConstructionConsumptionInput input,CancellationToken ct=default)
 {
  var c=await Actor(project,ct);if(c==null)return new(false,"AccessDenied");
  if(!Valid(input))return new(false,"InvalidConsumptionRequest");
  return await service.PreviewAsync(c,project,input.ReceiptLineId,input.IssueMovementId,ct);
 }
 public async Task<ConstructionConsumptionWrite> RegisterAsync(int project,ConstructionConsumptionInput input,CancellationToken ct=default)
 {
  var c=await Actor(project,ct);if(c==null)return new(false,"AccessDenied");
  if(!Valid(input)||input.Version is not {Length:64})return new(false,"InvalidConsumptionRequest");
  return await service.RegisterAsync(c,project,input.ReceiptLineId,input.IssueMovementId,input.Version,input.RequestId,input.Reason,ct);
 }
}
