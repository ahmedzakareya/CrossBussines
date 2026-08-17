using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch C — the manager hierarchy walk, with the company intersection it always needed.
    //
    // THE BUG THIS EXISTS TO PREVENT
    //
    // `Hierarchical` has NO CompanyID. B1 classified it CrossCompanyOperational for that reason, and it is
    // deliberately excluded from B2's query filters because the org tree is read before a context exists.
    //
    // So a raw descendant walk can return employees of ANOTHER COMPANY. Two implementations already walk it —
    // `CrmAccessService.TeamOwnerIdsAsync` and `LeaveWorkflowService` — and neither intersects the result with
    // the caller's company, because in a single-company install the difference is invisible. On a
    // multi-company install, a manager whose org node happens to sit above a node belonging to company 2
    // would see company 2's records.
    //
    // Batch C reuses the walk (it is verified, cycle-guarded, and already trusted by two modules) and adds
    // the one thing it lacked: every returned employee is confirmed to belong to the caller's company, from
    // the Employee ROW.
    //
    // The existing two callers are deliberately NOT repointed here — that would change working CRM and Leave
    // authorization inside a batch that is adding new modules. It is recorded as a Batch D item instead, and
    // the risk is documented in Architecture Risks rather than quietly carried.
    public interface IOrgHierarchy
    {
        // The manager plus every employee beneath them in the org tree, INTERSECTED with `companyId`.
        // Always contains `managerEmployeeId` itself (a manager can see their own records), so a caller with
        // no reports receives a set of one rather than an empty set that reads as "denied".
        Task<IReadOnlySet<int>> DirectAndIndirectReportsAsync(
            int companyId, int managerEmployeeId, CancellationToken cancellationToken = default);
    }

    public sealed class OrgHierarchy : IOrgHierarchy
    {
        // The node type that means "an employee sits here". Established by CrmAccessService and
        // LeaveWorkflowService, which both filter H_Type == 5 with H_ObjectID = Employee.ID.
        private const int EmployeeNodeType = 5;

        private readonly CrossDbContext _db;
        private readonly ILogger<OrgHierarchy> _log;

        public OrgHierarchy(CrossDbContext db, ILogger<OrgHierarchy> log) { _db = db; _log = log; }

        public async Task<IReadOnlySet<int>> DirectAndIndirectReportsAsync(
            int companyId, int managerEmployeeId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0 || managerEmployeeId <= 0) return new HashSet<int>();

            var team = new HashSet<int> { managerEmployeeId };

            var all = await _db.Hierarchicals.AsNoTracking()
                .Select(h => new { h.H_ID, h.H_Parent, h.H_Type, h.H_ObjectID })
                .ToListAsync(cancellationToken);

            var myNode = all.FirstOrDefault(h => h.H_Type == EmployeeNodeType && h.H_ObjectID == managerEmployeeId);
            if (myNode == null)
            {
                // Not placed in the org tree ⇒ no reports. Returning just self is the same answer
                // CrmAccessService gives, and it is the safe one: an unplaced manager sees only their own.
                return team;
            }

            var childrenByParent = all.Where(h => h.H_Parent.HasValue)
                .GroupBy(h => h.H_Parent!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            var stack = new Stack<int>();
            stack.Push(myNode.H_ID);
            var visited = new HashSet<int>();
            var candidates = new HashSet<int>();

            while (stack.Count > 0)
            {
                var nodeId = stack.Pop();
                if (!visited.Add(nodeId)) continue;               // cycle guard — the tree is user-maintained
                if (!childrenByParent.TryGetValue(nodeId, out var kids)) continue;
                foreach (var k in kids)
                {
                    if (k.H_Type == EmployeeNodeType && k.H_ObjectID.HasValue) candidates.Add(k.H_ObjectID.Value);
                    stack.Push(k.H_ID);
                }
            }

            if (candidates.Count == 0) return team;

            // THE INTERSECTION. Employee.EmpCompanyID is the authority for which company a person belongs to,
            // and it is the whole reason this class exists rather than calling the existing walk.
            var sameCompany = await _db.Employee.AsNoTracking()
                .Where(e => candidates.Contains(e.ID) && e.EmpCompanyID == companyId)
                .Select(e => e.ID)
                .ToListAsync(cancellationToken);

            int dropped = candidates.Count - sameCompany.Count;
            if (dropped > 0)
                // Worth a Warning, not a Debug: an org tree that spans companies is a data condition someone
                // should know about, and silently trimming it would hide the very thing being guarded.
                _log.LogWarning(
                    "Org hierarchy for manager {Manager} in company {Company} reached {Dropped} employee(s) of " +
                    "another company; they were excluded. Hierarchical carries no CompanyID, so the tree can " +
                    "span tenants.", managerEmployeeId, companyId, dropped);

            foreach (var id in sameCompany) team.Add(id);
            return team;
        }
    }
}
