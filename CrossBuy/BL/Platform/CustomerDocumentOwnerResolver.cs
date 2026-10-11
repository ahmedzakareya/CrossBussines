using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform;

public sealed class CustomerDocumentOwnerResolver(CrossDbContext db) : IDocumentOwnerResolver
{
    public string EntityType => EntityRegistry.Customer;
    public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
        => db.Customers.AsNoTracking().Where(c => c.ID == entityId && entityId > 0)
            .Select(c => (int?)c.CompanyID).FirstOrDefaultAsync(cancellationToken);
    public PermissionTarget TargetFor(int entityId, int companyId)
        => PermissionTarget.ForEntity(EntityRegistry.Customer, entityId);
    public string ModuleActionFor(DocumentAction action) => action switch
    {
        DocumentAction.View or DocumentAction.Download => "read",
        DocumentAction.Upload or DocumentAction.Replace or DocumentAction.Delete => AccountingActions.CustomerDocumentUpload,
        DocumentAction.Manage => "manage",
        _ => "customer-document-unsupported"
    };
}
