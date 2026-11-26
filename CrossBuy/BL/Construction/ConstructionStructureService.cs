using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CrossBuy.BL.Construction;

public sealed record StructureResult(bool Ok,string Code,int? Id=null,byte[]? Version=null);
public sealed record ConstructionReferenceOption(int Id,string Code,string Name,string? NameEn);
public sealed record ConstructionStructureWorkspace(ConstructionStructureSettings? Settings,
    List<WbsNode> Nodes,List<ConstructionCostCode> CostCodes,List<ConstructionSite> Sites,
    List<ConstructionReferenceOption> Warehouses,List<ConstructionReferenceOption> Members);
public sealed record WbsDraft(int? ParentId,string Code,string Name,string? NameEn,string ProgressMethod,
    bool OwnsBudget,bool OwnsCost,int Sequence,DateTime? StartDate,DateTime? EndDate,
    int? ResponsibleEmployeeId,byte[]? ParentVersion=null);
public sealed record CostCodeDraft(int? ParentId,string Code,string Name,string? NameEn,
    string Category,bool AllowDirectPosting,int? DefaultAccountId,byte[]? ParentVersion=null);
public sealed record SiteDraft(string Code,string Name,string? NameEn,string? Address,int? WarehouseId);

public interface IConstructionWarehouseAccess
{
    Task<bool> CanBindAsync(BusinessContext context,int warehouseId,CancellationToken ct=default);
}

public sealed class ConstructionWarehouseAccess(InventoryAccessService inventory):IConstructionWarehouseAccess
{
    public Task<bool> CanBindAsync(BusinessContext context,int warehouseId,CancellationToken ct=default)
        =>inventory.CanUseWarehouseAsync(context,warehouseId,ct);
}

public interface IConstructionStructureService
{
    Task<ConstructionStructureWorkspace?> GetAsync(BusinessContext context,int projectId,CancellationToken ct=default);
    Task<StructureResult> ConfigureAsync(BusinessContext context,int maxDepth,string reason,byte[]? expectedVersion=null,CancellationToken ct=default);
    Task<StructureResult> CreateNodeAsync(BusinessContext context,int projectId,WbsDraft draft,string reason,CancellationToken ct=default);
    Task<StructureResult> RenameNodeAsync(BusinessContext context,int projectId,int nodeId,string name,string? nameEn,byte[] expectedVersion,string reason,CancellationToken ct=default);
    Task<StructureResult> TransitionNodeAsync(BusinessContext context,int projectId,int nodeId,string status,byte[] expectedVersion,string reason,CancellationToken ct=default);
    Task<StructureResult> CreateCostCodeAsync(BusinessContext context,CostCodeDraft draft,string reason,CancellationToken ct=default);
    Task<StructureResult> CreateSiteAsync(BusinessContext context,int projectId,SiteDraft draft,string reason,CancellationToken ct=default);
}

// Structural dimensions only. This service never writes GL, stock, payroll, or a financial document.
// Initial mutations use the existing non-bootstrap financial management right. Dedicated restructure
// authority and impact preview remain required before any relocation operation can be introduced.
public sealed class ConstructionStructureService(CrossDbContext db,IProjectsAccessService access,
    IConstructionWarehouseAccess warehouses,IConstructionAuditService audit):IConstructionStructureService
{
    private bool InCompany(BusinessContext c)=>c.CompanyId>0&&c.EmployeeId is >0&&c.IsAuthenticated&&db.CompanyScope.FilterCompanyId==c.CompanyId;
    private static bool Text(string? s,int maximum)=>!string.IsNullOrWhiteSpace(s)&&s.Trim().Length<=maximum;
    private static bool OptionalText(string? s,int maximum)=>s==null||s.Trim().Length<=maximum;
    private static bool Match(byte[]? a,byte[] b)=>a is {Length:>0}&&a.SequenceEqual(b);
    private static StructureResult Refuse(string code)=>new(false,code);

    private async Task<bool> Allowed(BusinessContext c,int? projectId,string action,CancellationToken ct)
    {
        if(!InCompany(c)||!await access.CanAsync(c,action,projectId.HasValue?new PermissionTarget{ProjectId=projectId}:null,ct))return false;
        return !projectId.HasValue||await db.Projects.AsNoTracking().AnyAsync(x=>x.ID==projectId&&x.CompanyID==c.CompanyId,ct);
    }

    public async Task<ConstructionStructureWorkspace?> GetAsync(BusinessContext c,int projectId,CancellationToken ct=default)
    {
        if(!await Allowed(c,projectId,ProjectsActions.BudgetView,ct))return null;
        var candidates=await db.Warehouses.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.IsActive)
            .OrderBy(x=>x.Code).Select(x=>new ConstructionReferenceOption(x.ID,x.Code,x.Name,x.NameEn)).ToListAsync(ct);
        var permittedWarehouses=new List<ConstructionReferenceOption>();
        foreach(var candidate in candidates)
            if(await warehouses.CanBindAsync(c,candidate.Id,ct))permittedWarehouses.Add(candidate);
        var now=DateTime.UtcNow;
        var memberIds=db.ProjectMembers.AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.IsActive&&(x.LeftAt==null||x.LeftAt>now)).Select(x=>x.EmployeeId);
        var members=await db.Employee.AsNoTracking().Where(x=>x.EmpCompanyID==c.CompanyId&&x.IsActive&&memberIds.Contains(x.ID))
            .OrderBy(EmployeeNames.Display()).Select(x=>new ConstructionReferenceOption(x.ID,"",x.FullName,x.FullNameEn)).ToListAsync(ct);
        return new(await db.Set<ConstructionStructureSettings>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId,ct),
            await db.Set<WbsNode>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId).OrderBy(x=>x.Depth).ThenBy(x=>x.Sequence).ThenBy(x=>x.ID).ToListAsync(ct),
            await db.Set<ConstructionCostCode>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId).OrderBy(x=>x.Code).ToListAsync(ct),
            await db.Set<ConstructionSite>().AsNoTracking().Where(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId).OrderBy(x=>x.Code).ToListAsync(ct),
            permittedWarehouses,members);
    }

    private void Record(BusinessContext c,int? project,string entity,int id,string? oldValue,string newValue,string reason,bool created)
        =>audit.Record(new[]{ConstructionAuditDraft.Text(c.CompanyId,project,entity,id,null,"Structure",oldValue,newValue,
            created?ConstructionChangeKind.Created:ConstructionChangeKind.Updated,reason,c.EmployeeId,nameof(ConstructionStructureService),Guid.NewGuid())});
    private void Rotate(object entity)
    {
        if(!db.Database.IsSqlServer())db.Entry(entity).Property("RowVersion").CurrentValue=Guid.NewGuid().ToByteArray();
    }

    private async Task<StructureResult> Execute(BusinessContext c,int? project,string reason,
        Func<Task<StructureResult>> command,CancellationToken ct)
    {
        if(!await Allowed(c,project,ProjectsActions.BudgetManage,ct))return Refuse("AccessDenied");
        if(!Text(reason,1000))return Refuse("ReasonRequired");
        // Do not enlist or save someone else's pending writes on this shared scoped DbContext.
        if(db.Database.CurrentTransaction!=null||db.ChangeTracker.HasChanges())return Refuse("CleanUnitOfWorkRequired");
        try
        {
            await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);
            var result=await command();
            if(result.Ok)await tx.CommitAsync(ct);
            else{await tx.RollbackAsync(ct);db.ChangeTracker.Clear();}
            return result;
        }
        catch(DbUpdateConcurrencyException){db.ChangeTracker.Clear();return Refuse("ConcurrencyConflict");}
        catch(Exception ex)when(KnownSqlError(ex)!=null)
        {db.ChangeTracker.Clear();return Refuse(KnownSqlError(ex)!);}
    }

    private static string? KnownSqlError(Exception ex)
    {
        for(Exception? e=ex;e!=null;e=e.InnerException)
            if(e is Microsoft.Data.SqlClient.SqlException sql)return sql.Number switch
            {
                1205 or 1222=>"ConcurrencyConflict",2601 or 2627=>"DuplicateCode",547=>"InvalidReference",
                51101=>"DepthBelowExistingNodes",51102=>"DepthLimitExceeded",51103=>"InvalidHierarchy",
                51104=>"RestructureWorkflowRequired",51105=>"InvalidStatusTransition",51106=>"InvalidDefaultAccount",_=>null
            };
        return null;
    }

    public Task<StructureResult> ConfigureAsync(BusinessContext c,int maxDepth,string reason,byte[]? expectedVersion=null,CancellationToken ct=default)
        =>Execute(c,null,reason,async()=>
    {
        if(maxDepth is <1 or >32)return Refuse("InvalidDepthSetting");
        if(await db.Set<WbsNode>().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.Depth>maxDepth,ct))return Refuse("DepthBelowExistingNodes");
        var settings=await db.Set<ConstructionStructureSettings>().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId,ct);
        var created=settings==null;
        if(!created&&!Match(expectedVersion,settings!.RowVersion))return Refuse("ConcurrencyConflict");
        var before=created?null:JsonSerializer.Serialize(new{settings!.MaxWbsDepth,settings.Reason});
        if(created){settings=new(){CompanyID=c.CompanyId};db.Add(settings);}
        settings!.MaxWbsDepth=maxDepth;settings.Reason=reason.Trim();settings.UpdatedBy=c.EmployeeId!.Value;settings.UpdatedAt=DateTime.UtcNow;Rotate(settings);
        Record(c,null,nameof(ConstructionStructureSettings),c.CompanyId,before,JsonSerializer.Serialize(new{MaxWbsDepth=maxDepth}),reason,created);
        await db.SaveChangesAsync(ct);return new(true,"Saved",c.CompanyId,settings.RowVersion.ToArray());
    },ct);

    public Task<StructureResult> CreateNodeAsync(BusinessContext c,int projectId,WbsDraft input,string reason,CancellationToken ct=default)
        =>Execute(c,projectId,reason,async()=>
    {
        if(!Text(input.Code,50)||!Text(input.Name,200)||!OptionalText(input.NameEn,200)||!ConstructionStructureVocabulary.IsProgressMethod(input.ProgressMethod))return Refuse("InvalidNode");
        if(input.Sequence<0||(input.StartDate.HasValue&&input.EndDate.HasValue&&input.EndDate<input.StartDate))return Refuse("InvalidNodeDatesOrOrder");
        var settings=await db.Set<ConstructionStructureSettings>().AsNoTracking().SingleOrDefaultAsync(x=>x.CompanyID==c.CompanyId,ct);
        if(settings==null)return Refuse("StructureNotConfigured");
        WbsNode? parent=null;
        if(input.ParentId.HasValue)
        {
            parent=await db.Set<WbsNode>().SingleOrDefaultAsync(x=>x.ID==input.ParentId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId,ct);
            if(parent==null||parent.Status is "Archived" or "Completed")return Refuse("InvalidParent");
            if(!Match(input.ParentVersion,parent.RowVersion))return Refuse("ConcurrencyConflict");
        }
        var depth=(parent?.Depth??0)+1;if(depth>settings.MaxWbsDepth)return Refuse("DepthLimitExceeded");
        if(input.ResponsibleEmployeeId.HasValue&&!await db.Employee.AsNoTracking().AnyAsync(x=>x.ID==input.ResponsibleEmployeeId&&x.EmpCompanyID==c.CompanyId,ct))return Refuse("InvalidResponsibleEmployee");
        var node=new WbsNode{CompanyID=c.CompanyId,ProjectId=projectId,ParentId=parent?.ID,Code=input.Code.Trim(),Name=input.Name.Trim(),NameEn=input.NameEn?.Trim(),
            Depth=depth,Path="pending",Sequence=input.Sequence,ProgressMethod=input.ProgressMethod,OwnsBudget=input.OwnsBudget,OwnsCost=input.OwnsCost,
            StartDate=input.StartDate,EndDate=input.EndDate,ResponsibleEmployeeId=input.ResponsibleEmployeeId,CreatedAt=DateTime.UtcNow,CreatedBy=c.EmployeeId!.Value};
        db.Add(node);await db.SaveChangesAsync(ct);
        node.Path=(parent?.Path??"/")+node.ID+"/";Rotate(node);
        Record(c,projectId,nameof(WbsNode),node.ID,null,JsonSerializer.Serialize(new{node.Code,node.Name,node.NameEn,node.Path,node.Depth,node.ProgressMethod,node.OwnsBudget,node.OwnsCost}),reason,true);
        await db.SaveChangesAsync(ct);return new(true,"Created",node.ID,node.RowVersion.ToArray());
    },ct);

    public Task<StructureResult> RenameNodeAsync(BusinessContext c,int projectId,int nodeId,string name,string? nameEn,byte[] expectedVersion,string reason,CancellationToken ct=default)
        =>Execute(c,projectId,reason,async()=>
    {
        if(!Text(name,200)||!OptionalText(nameEn,200))return Refuse("InvalidName");
        var node=await db.Set<WbsNode>().SingleOrDefaultAsync(x=>x.ID==nodeId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId,ct);
        if(node==null||node.Status=="Archived")return Refuse("NodeNotEditable");
        if(!Match(expectedVersion,node.RowVersion))return Refuse("ConcurrencyConflict");
        var before=JsonSerializer.Serialize(new{node.Name,node.NameEn});node.Name=name.Trim();node.NameEn=nameEn?.Trim();Rotate(node);
        Record(c,projectId,nameof(WbsNode),node.ID,before,JsonSerializer.Serialize(new{node.Name,node.NameEn}),reason,false);
        await db.SaveChangesAsync(ct);return new(true,"Saved",node.ID,node.RowVersion.ToArray());
    },ct);

    public Task<StructureResult> TransitionNodeAsync(BusinessContext c,int projectId,int nodeId,string status,byte[] expectedVersion,string reason,CancellationToken ct=default)
        =>Execute(c,projectId,reason,async()=>
    {
        var node=await db.Set<WbsNode>().SingleOrDefaultAsync(x=>x.ID==nodeId&&x.CompanyID==c.CompanyId&&x.ProjectId==projectId,ct);
        if(node==null)return Refuse("NodeNotFound");
        if(!Match(expectedVersion,node.RowVersion))return Refuse("ConcurrencyConflict");
        if(!ConstructionStructureVocabulary.CanTransition(node.Status,status))return Refuse("InvalidStatusTransition");
        if(status is "Completed" or "Archived"&&await db.Set<WbsNode>().AnyAsync(x=>x.CompanyID==c.CompanyId&&x.ProjectId==projectId&&x.ParentId==nodeId&&x.Status!="Completed"&&x.Status!="Archived",ct))return Refuse("OpenChildNodes");
        var before=node.Status;node.Status=status;Rotate(node);
        Record(c,projectId,nameof(WbsNode),node.ID,before,status,reason,false);await db.SaveChangesAsync(ct);
        return new(true,"Saved",node.ID,node.RowVersion.ToArray());
    },ct);

    public Task<StructureResult> CreateCostCodeAsync(BusinessContext c,CostCodeDraft input,string reason,CancellationToken ct=default)
        =>Execute(c,null,reason,async()=>
    {
        if(!Text(input.Code,50)||!Text(input.Name,200)||!OptionalText(input.NameEn,200)||!ConstructionStructureVocabulary.IsCostCategory(input.Category))return Refuse("InvalidCostCode");
        if(input.ParentId.HasValue)
        {
            var parent=await db.Set<ConstructionCostCode>().SingleOrDefaultAsync(x=>x.ID==input.ParentId&&x.CompanyID==c.CompanyId&&x.Category==input.Category&&x.IsActive,ct);
            if(parent==null)return Refuse("InvalidParent");if(!Match(input.ParentVersion,parent.RowVersion))return Refuse("ConcurrencyConflict");
        }
        if(input.DefaultAccountId.HasValue&&!await db.Accounts.AsNoTracking().AnyAsync(x=>x.ID==input.DefaultAccountId&&x.CompanyID==c.CompanyId&&x.IsActive&&x.IsPostable,ct))return Refuse("InvalidDefaultAccount");
        var code=new ConstructionCostCode{CompanyID=c.CompanyId,ParentId=input.ParentId,Code=input.Code.Trim(),Name=input.Name.Trim(),NameEn=input.NameEn?.Trim(),Category=input.Category,AllowDirectPosting=input.AllowDirectPosting,DefaultAccountId=input.DefaultAccountId};
        db.Add(code);await db.SaveChangesAsync(ct);
        Record(c,null,nameof(ConstructionCostCode),code.ID,null,JsonSerializer.Serialize(new{code.Code,code.Name,code.NameEn,code.Category,code.ParentId,code.DefaultAccountId,code.AllowDirectPosting}),reason,true);
        await db.SaveChangesAsync(ct);return new(true,"Created",code.ID,code.RowVersion.ToArray());
    },ct);

    public Task<StructureResult> CreateSiteAsync(BusinessContext c,int projectId,SiteDraft input,string reason,CancellationToken ct=default)
        =>Execute(c,projectId,reason,async()=>
    {
        if(!Text(input.Code,50)||!Text(input.Name,200)||!OptionalText(input.NameEn,200)||!OptionalText(input.Address,1000))return Refuse("InvalidSite");
        if(input.WarehouseId.HasValue&&(!await db.Warehouses.AsNoTracking().AnyAsync(x=>x.ID==input.WarehouseId&&x.CompanyID==c.CompanyId&&x.IsActive,ct)||!await warehouses.CanBindAsync(c,input.WarehouseId.Value,ct)))return Refuse("WarehouseAccessDenied");
        var site=new ConstructionSite{CompanyID=c.CompanyId,ProjectId=projectId,Code=input.Code.Trim(),Name=input.Name.Trim(),NameEn=input.NameEn?.Trim(),Address=input.Address?.Trim(),WarehouseId=input.WarehouseId};
        db.Add(site);await db.SaveChangesAsync(ct);
        Record(c,projectId,nameof(ConstructionSite),site.ID,null,JsonSerializer.Serialize(new{site.Code,site.Name,site.NameEn,site.WarehouseId}),reason,true);
        await db.SaveChangesAsync(ct);return new(true,"Created",site.ID,site.RowVersion.ToArray());
    },ct);
}
