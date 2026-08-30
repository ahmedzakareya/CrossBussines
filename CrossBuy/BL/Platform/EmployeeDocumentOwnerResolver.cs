using CrossBuy.Models.Context;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // =============================================================================================
    // The first owner resolver, and the reference shape for every family that follows.
    //
    // It is deliberately thin. It knows two things about an Employee — which company owns row N, and
    // what HR calls the authority a document verb needs — and it holds no rule of its own. Whether
    // this caller may see this employee's file is HrAccessService's answer, reached through the module
    // contract, exactly as it would be for any other HR question. That separation is what keeps the
    // document platform from slowly acquiring a second copy of every module's permission model.
    //
    // WHY EMPLOYEE FIRST. /uploads/hr-docs is the proven exposure: EmployeeDocument and
    // HrDocumentAttachment both hang off an employee, and both are readable today by anyone signed in
    // who knows the URL. It is also the family where ownership is unambiguous, so it can be closed
    // without waiting for the document domain to be built.
    //
    // THE ACTION MAP IS THE INTERESTING PART. Reading a document about a person is employee-view, not
    // the module's generic `read`: `read` means "HR screens exist for you", which is a far weaker claim
    // than "you may look at this person's record". Writing is employee-manage. Manage is
    // confidential-view rather than employee-manage, because the elevated tier the resolver asks for
    // when a document is Confidential or Restricted is about SEEING protected content, and HR already
    // separates that tier — an HR officer may administer an employee without being entitled to their
    // disciplinary file.
    // =============================================================================================
    public sealed class EmployeeDocumentOwnerResolver : IDocumentOwnerResolver
    {
        private readonly CrossDbContext _db;

        public EmployeeDocumentOwnerResolver(CrossDbContext db) { _db = db; }

        public string EntityType => EntityRegistry.Employee;

        public async Task<int?> OwningCompanyIdAsync(int entityId, CancellationToken cancellationToken = default)
        {
            if (entityId <= 0) return null;

            // The company predicate is NOT applied here on purpose: this method answers "who owns it",
            // and the resolver compares that against the caller. Filtering by the caller's company here
            // would turn a foreign row into "not found" one layer too early, and the relation-mismatch
            // refusal — the one that catches a document pointing at another tenant's employee — would
            // never fire because it would never see the real owner.
            return await _db.Employee.AsNoTracking()
                .Where(e => e.ID == entityId)
                .Select(e => (int?)e.EmpCompanyID)
                .FirstOrDefaultAsync(cancellationToken);
        }

        public PermissionTarget TargetFor(int entityId, int companyId)
            => PermissionTarget.ForSubjectEmployee(entityId, companyId);

        public string ModuleActionFor(DocumentAction action) => action switch
        {
            DocumentAction.View or DocumentAction.Download => HrActions.EmployeeView,
            DocumentAction.Upload or DocumentAction.Replace or DocumentAction.Delete => HrActions.EmployeeManage,
            DocumentAction.Manage => HrActions.ConfidentialView,

            // A verb this resolver has not classified must not fall through to something permissive.
            // ConfidentialView is the narrowest action HR publishes, so an unmapped verb ends up
            // demanding the MOST authority rather than the least.
            _ => HrActions.ConfidentialView,
        };
    }
}
