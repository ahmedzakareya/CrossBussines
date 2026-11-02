using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL.Platform
{
    // Compatibility entry point. HR owns the permission mapping in the resolver
    // registered by Program; the platform must not retain a second copy of it.
    public sealed class EmployeeDocumentOwnerResolver : IDocumentOwnerResolver
    {
        private readonly CrossBuy.BL.EmployeeDocumentOwnerResolver _owner;
        public EmployeeDocumentOwnerResolver(CrossDbContext db)
            => _owner = new CrossBuy.BL.EmployeeDocumentOwnerResolver(db);
        public string EntityType => _owner.EntityType;
        public Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
            => _owner.OwningCompanyIdAsync(entityId, cancellationToken);
        public PermissionTarget TargetFor(int entityId, int companyId)
            => _owner.TargetFor(entityId, companyId);
        public string ModuleActionFor(DocumentAction action) => _owner.ModuleActionFor(action);
    }
}
