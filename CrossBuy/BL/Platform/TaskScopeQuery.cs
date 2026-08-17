using CrossBuy.Models.Context.Tasks;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform
{
    // Stage 1 Batch C.1 — the ONE place an AccessScope becomes a task query.
    //
    // WHY THIS FILE EXISTS AT ALL. Before it, the Own/Team/Company → predicate mapping lived only in a comment
    // above `TasksAccessService.ResolveScopeAsync`. A comment cannot be tested, and worse, it invited every
    // future consumer (a task list, a dashboard count, a Unified Inbox) to write the translation again — and the
    // way that goes wrong is well known and expensive:
    //
    //     var all = await db.TaskItems.ToListAsync();                       // the whole company, in memory
    //     var mine = all.Where(t => access.CanAsync(ctx, "read", For(t))    // one permission evaluation PER ROW
    //                                     .Result).ToList();
    //
    // That shape is a correctness problem before it is a performance problem: the permission cost grows with
    // the data, every row of the company is materialised before anything is authorized, and a filter applied in
    // memory can be silently skipped by a later `.Where` that runs on the server. Scope resolution happens
    // ONCE, and the answer becomes ONE server-side predicate — that is what this file enforces.
    //
    // It returns IQueryable deliberately: paging, sorting and counting compose ON TOP of the predicate and
    // still execute in SQL. Nothing here calls ToListAsync.
    public static class TaskScopeQuery
    {
        // Apply a resolved scope to a task query as a single predicate.
        //
        // `callerEmployeeId` is the CALLER'S employee id from the resolved BusinessContext — never a value from
        // the request. It is only consulted for `Own`, where the scope itself carries no principal list.
        public static IQueryable<TaskItem> WithinScope(
            this IQueryable<TaskItem> source, AccessScope scope, int? callerEmployeeId)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(scope);

            // An empty scope yields an empty RESULT, not an unfiltered query. `Where(_ => false)` keeps the
            // return type composable so a caller that goes on to Count()/Skip() still gets zero rather than
            // everything — the failure mode if this returned `source` untouched.
            if (scope.IsEmpty) return source.Where(_ => false);

            switch (scope.Breadth)
            {
                case AccessBreadth.Company:
                {
                    int companyId = RequireCompany(scope);
                    return source.Where(t => t.CompanyId == companyId);
                }

                case AccessBreadth.Team:
                {
                    int companyId = RequireCompany(scope);

                    // Already company-intersected by IOrgHierarchy before it reached the scope. Copied to a
                    // local List so EF translates Contains to an IN (...) against a parameter list rather than
                    // closing over the scope object.
                    var principals = scope.PrincipalIds?.ToList() ?? new List<int>();
                    if (principals.Count == 0) return source.Where(_ => false);

                    // Assignee OR creator, both tested against the SAME team set. Creator matters for
                    // agreement with CanAsync: a supervisor who created a task keeps access to it, and because
                    // the team set always contains the caller themself, `principals.Contains(CreatedBy)` covers
                    // "I created it" and "one of my reports created it" in one predicate.
                    return source.Where(t => t.CompanyId == companyId
                        && (principals.Contains(t.AssigneeEmployeeId) || principals.Contains(t.CreatedByEmployeeId)));
                }

                case AccessBreadth.Own:
                {
                    int companyId = RequireCompany(scope);
                    if (callerEmployeeId is not > 0) return source.Where(_ => false);
                    int me = callerEmployeeId.Value;
                    return source.Where(t => t.CompanyId == companyId
                        && (t.AssigneeEmployeeId == me || t.CreatedByEmployeeId == me));
                }

                // ---- the two breadths this entity CANNOT express ----
                //
                // Branch: TaskItem has no BranchId column. There is no branch anchor on the row, so a
                // branch-scoped set could only be produced by joining through the assignee's current branch —
                // which is a DIFFERENT rule (where the person works TODAY, not where the task belongs) and
                // would silently reassign visibility whenever someone transfers. `TasksAccessService` therefore
                // never returns Branch. If some future caller hands one in, that is a bug in the caller, and
                // the two ways to "handle" it are both wrong: widening to Company grants more than was asked,
                // narrowing to Own grants less and hides the mistake. So it throws. See ADR-029.
                case AccessBreadth.Branch:
                    throw new NotSupportedException(
                        "TaskItem carries no BranchId, so a Branch-breadth scope cannot be translated into a task " +
                        "predicate. Resolve a Company or Team scope instead — see TaskScopeQuery.");

                // CrossCompany is reachable only through ICompanyIsolationBypass, which is authorized, reasoned
                // and audited at its own gateway. Honouring it here would drop the company predicate with none
                // of that, which is precisely the hole the bypass exists to keep closed.
                case AccessBreadth.CrossCompany:
                    throw new NotSupportedException(
                        "A CrossCompany scope is not translated here. Cross-company reads go through " +
                        "ICompanyIsolationBypass, which records who authorized them and why.");

                default:
                    return source.Where(_ => false);
            }
        }

        // A company-scoped breadth with no company is a resolution defect, not a request for everything.
        private static int RequireCompany(AccessScope scope)
            => scope.CompanyId is > 0
                ? scope.CompanyId.Value
                : throw new InvalidOperationException(
                    $"An AccessScope of breadth {scope.Breadth} carries no CompanyId; refusing to build an " +
                    "unscoped task query.");
    }
}
