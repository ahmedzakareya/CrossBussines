using CrossBuy.BL.Platform;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrossBuy.BL.Communication
{
    // =============================================================================================
    // Communication Platform (ADR-032) — PRINCIPAL EXPANSION: turning "@department:5" or a thread grant to
    // a team into a set of employee ids.
    //
    // ONE resolver serves BOTH mentions (module 3) and thread permissions (modules 23, 24). That is not an
    // optimisation, it is a correctness requirement: if mentions and permissions expanded "the sales
    // department" differently, a note could notify somebody it does not authorize, or authorize somebody it
    // cannot notify. Sharing the expansion makes the two answers the same by construction.
    //
    // EXTENSION POINT: ICommPrincipalSource, one per kind, injected as IEnumerable — the same shape the
    // kernel uses for ILegacyTimelineAdapter and IModulePermissionAdapter. Adding @role support is adding one
    // source and one registration; nothing else in the platform changes.
    //
    // COMPANY INTERSECTION IS MANDATORY IN EVERY SOURCE. The org tree (Hierarchical) carries NO CompanyID —
    // IOrgHierarchy's own comment documents that a raw descendant walk can return employees of another
    // company, and that two existing callers do exactly that. Every source here therefore intersects its
    // result with Employee.EmpCompanyID before returning, and the base class enforces it a second time so a
    // future source cannot forget.
    // =============================================================================================
    public interface ICommPrincipalResolver
    {
        // Expands a principal to the employees it covers, within one company.
        Task<CommPrincipalExpansion> ExpandAsync(
            string kind, int? id, string? key, int companyId, CancellationToken cancellationToken = default);

        // "Is this employee covered by that principal?" — the permission question. Answered through the same
        // expansion so it can never disagree with it.
        Task<bool> CoversAsync(
            string kind, int? id, string? key, int companyId, int employeeId, CancellationToken cancellationToken = default);

        // Bilingual display label for a principal, resolved ONCE at authoring time and then stored — see
        // CommMention.LabelAr for why re-resolving on read would rewrite history.
        Task<(string? LabelAr, string? LabelEn)> LabelAsync(
            string kind, int? id, string? key, int companyId, CancellationToken cancellationToken = default);

        IReadOnlyList<string> SupportedKinds { get; }
    }

    public sealed class CommPrincipalExpansion
    {
        public required bool Supported { get; init; }
        public required IReadOnlyList<int> EmployeeIds { get; init; }
        public string? LabelAr { get; init; }
        public string? LabelEn { get; init; }

        // Why the expansion is empty or unsupported. Carried rather than logged so a mention that reached
        // nobody can explain itself to the author.
        public string? Reason { get; init; }

        public static CommPrincipalExpansion Unsupported(string reason) => new()
        { Supported = false, EmployeeIds = Array.Empty<int>(), Reason = reason };

        public static CommPrincipalExpansion Of(IReadOnlyList<int> ids, string? labelAr = null, string? labelEn = null, string? reason = null) => new()
        { Supported = true, EmployeeIds = ids, LabelAr = labelAr, LabelEn = labelEn, Reason = reason };
    }

    // ---------------------------------------------------------------------------------------------
    // THE EXTENSION POINT.
    // ---------------------------------------------------------------------------------------------
    public interface ICommPrincipalSource
    {
        // One of CommMentionTargetKind / CommPrincipalKind — the two vocabularies deliberately share values.
        string Kind { get; }

        Task<CommPrincipalExpansion> ExpandAsync(
            int? id, string? key, int companyId, CancellationToken cancellationToken = default);
    }

    // ---------------------------------------------------------------------------------------------
    public sealed class CommPrincipalResolver : ICommPrincipalResolver
    {
        private readonly Dictionary<string, ICommPrincipalSource> _sources;
        private readonly CrossDbContext _db;
        private readonly CommunicationPlatformOptions _options;
        private readonly ILogger<CommPrincipalResolver> _log;

        public CommPrincipalResolver(
            IEnumerable<ICommPrincipalSource> sources,
            CrossDbContext db,
            IOptions<CommunicationPlatformOptions> options,
            ILogger<CommPrincipalResolver> log)
        {
            // LAST registration wins for a duplicated kind, which is what lets a deployment REPLACE the
            // built-in Department source by registering its own after ours — the standard ASP.NET Core
            // override idiom. Silently keeping the first would make an override look like it was ignored.
            _sources = new Dictionary<string, ICommPrincipalSource>(StringComparer.Ordinal);
            foreach (var source in sources) _sources[source.Kind] = source;

            _db = db;
            _options = options.Value;
            _log = log;
        }

        public IReadOnlyList<string> SupportedKinds => _sources.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        public async Task<CommPrincipalExpansion> ExpandAsync(
            string kind, int? id, string? key, int companyId, CancellationToken cancellationToken = default)
        {
            if (companyId <= 0)
                // Fail closed. CLAUDE.md: "An unresolved company scope reads no company-scoped data and writes
                // none. Fail closed; never default to a company."
                return CommPrincipalExpansion.Unsupported("no company scope resolved");

            if (!_sources.TryGetValue(kind ?? "", out var source))
                return CommPrincipalExpansion.Unsupported(
                    $"no ICommPrincipalSource is registered for kind '{kind}'. Registered: [{string.Join(", ", SupportedKinds)}].");

            // Per-kind deployment switches. Checked HERE rather than inside each source so the switch cannot be
            // bypassed by a replacement source.
            var gate = GateFor(kind!);
            if (gate != null) return CommPrincipalExpansion.Unsupported(gate);

            var expansion = await source.ExpandAsync(id, key, companyId, cancellationToken);
            if (!expansion.Supported) return expansion;

            // THE SECOND COMPANY INTERSECTION. Every source already intersects; this repeats it so a source
            // written later — by us or by a deployment — cannot leak another company's employees through this
            // resolver. Cheap, and it is the difference between a rule and a habit.
            var verified = await IntersectCompanyAsync(expansion.EmployeeIds, companyId, cancellationToken);

            int dropped = expansion.EmployeeIds.Count - verified.Count;
            if (dropped > 0)
                _log.LogWarning(
                    "Principal {Kind}:{Id} in company {Company} expanded to {Dropped} employee(s) outside the " +
                    "company or inactive; they were excluded. The org tree carries no CompanyID, so it can span tenants.",
                    kind, id, companyId, dropped);

            return CommPrincipalExpansion.Of(verified, expansion.LabelAr, expansion.LabelEn, expansion.Reason);
        }

        public async Task<bool> CoversAsync(
            string kind, int? id, string? key, int companyId, int employeeId, CancellationToken cancellationToken = default)
        {
            if (employeeId <= 0) return false;

            // Employee is answered directly: expanding it would be a database round trip to compare two ints.
            if (string.Equals(kind, CommPrincipalKind.Employee, StringComparison.Ordinal))
                return id == employeeId;

            var expansion = await ExpandAsync(kind, id, key, companyId, cancellationToken);
            return expansion.Supported && expansion.EmployeeIds.Contains(employeeId);
        }

        public async Task<(string? LabelAr, string? LabelEn)> LabelAsync(
            string kind, int? id, string? key, int companyId, CancellationToken cancellationToken = default)
        {
            var expansion = await ExpandAsync(kind, id, key, companyId, cancellationToken);
            if (expansion.LabelAr != null || expansion.LabelEn != null)
                return (expansion.LabelAr, expansion.LabelEn);

            // A label is display text; falling back to the token is better than blank, and better than a
            // bare id which tells the reader nothing.
            var token = "@" + (kind ?? "?").ToLowerInvariant() + ":" + (id?.ToString() ?? key ?? "?");
            return (token, token);
        }

        // Per-kind gates. Role returns its own message: the flag exists, and turning it on still does not make
        // role mentions work, because no source is registered (ADR-032 §5).
        private string? GateFor(string kind) => kind switch
        {
            CommMentionTargetKind.Team when !_options.EnableTeamMentions =>
                "CommunicationPlatform:EnableTeamMentions is off.",
            CommMentionTargetKind.Department when !_options.EnableDepartmentMentions =>
                "CommunicationPlatform:EnableDepartmentMentions is off.",
            CommMentionTargetKind.Role when !_options.EnableRoleMentions =>
                "CommunicationPlatform:EnableRoleMentions is off.",
            _ => null,
        };

        private async Task<List<int>> IntersectCompanyAsync(
            IReadOnlyList<int> candidates, int companyId, CancellationToken cancellationToken)
        {
            if (candidates.Count == 0) return new List<int>();
            var ids = candidates.Distinct().ToList();

            // Employee.EmpCompanyID is the authority for which company a person belongs to — the same column
            // and the same reasoning as OrgHierarchy's intersection. IsActive is applied here too: notifying a
            // terminated employee is noise, and granting them thread access is a hole.
            return await _db.Employee.AsNoTracking()
                .Where(e => ids.Contains(e.ID) && e.EmpCompanyID == companyId && e.IsActive)
                .Select(e => e.ID)
                .ToListAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Source: @employee:{id}
    // ---------------------------------------------------------------------------------------------
    public sealed class EmployeeCommPrincipalSource : ICommPrincipalSource
    {
        private readonly CrossDbContext _db;
        public EmployeeCommPrincipalSource(CrossDbContext db) => _db = db;

        public string Kind => CommMentionTargetKind.Employee;

        public async Task<CommPrincipalExpansion> ExpandAsync(
            int? id, string? key, int companyId, CancellationToken cancellationToken = default)
        {
            if (id is not > 0) return CommPrincipalExpansion.Unsupported("an employee mention needs a positive employee id");

            var row = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == id.Value && e.EmpCompanyID == companyId && e.IsActive)
                .Select(e => new { e.ID, e.FullName, e.FullNameEn })
                .FirstOrDefaultAsync(cancellationToken);

            // Absent, other-company and inactive answer IDENTICALLY. Distinguishing them would turn a mention
            // into a cross-tenant existence oracle: an author could probe ids and learn which ones exist
            // elsewhere. Same rule CommunicationAccessService applies to conversations.
            if (row == null)
                return CommPrincipalExpansion.Of(Array.Empty<int>(), reason: "employee not found in this company");

            return CommPrincipalExpansion.Of(new[] { row.ID }, row.FullName, row.FullNameEn ?? row.FullName);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Source: @team:{managerEmployeeId} — a manager plus their direct and indirect reports.
    //
    // "Team" has no table in this product, and inventing one would be a new master-data concept for a
    // collaboration feature to own. The org tree already expresses exactly this relationship, and
    // IOrgHierarchy already walks it with a cycle guard and the company intersection, so a team mention is
    // that verified walk — read-only, no change to the service, no new table.
    // ---------------------------------------------------------------------------------------------
    public sealed class TeamCommPrincipalSource : ICommPrincipalSource
    {
        private readonly IOrgHierarchy _org;
        private readonly CrossDbContext _db;

        public TeamCommPrincipalSource(IOrgHierarchy org, CrossDbContext db) { _org = org; _db = db; }

        public string Kind => CommMentionTargetKind.Team;

        public async Task<CommPrincipalExpansion> ExpandAsync(
            int? id, string? key, int companyId, CancellationToken cancellationToken = default)
        {
            if (id is not > 0)
                return CommPrincipalExpansion.Unsupported(
                    "a team mention addresses a MANAGER's org subtree, so it needs that manager's employee id");

            var manager = await _db.Employee.AsNoTracking()
                .Where(e => e.ID == id.Value && e.EmpCompanyID == companyId && e.IsActive)
                .Select(e => new { e.FullName, e.FullNameEn })
                .FirstOrDefaultAsync(cancellationToken);

            if (manager == null)
                return CommPrincipalExpansion.Of(Array.Empty<int>(), reason: "manager not found in this company");

            var members = await _org.DirectAndIndirectReportsAsync(companyId, id.Value, cancellationToken);

            // IOrgHierarchy always returns at least the manager themselves, so a manager with no reports is a
            // set of one rather than an empty set that would read as "denied".
            return CommPrincipalExpansion.Of(
                members.ToList(),
                $"فريق {manager.FullName}",
                $"{manager.FullNameEn ?? manager.FullName}'s team");
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Source: @department:{hierarchicalNodeId} — an org node and every node beneath it.
    //
    // Employee.DepartmentID points at a Hierarchical node, so membership is a direct column read once the
    // descendant node set is known. That is why this source walks NODES and not employees: a department
    // mention must reach the sub-departments too, and walking employees would miss anyone filed under a child
    // node.
    // ---------------------------------------------------------------------------------------------
    public sealed class DepartmentCommPrincipalSource : ICommPrincipalSource
    {
        private readonly CrossDbContext _db;
        public DepartmentCommPrincipalSource(CrossDbContext db) => _db = db;

        public string Kind => CommMentionTargetKind.Department;

        public async Task<CommPrincipalExpansion> ExpandAsync(
            int? id, string? key, int companyId, CancellationToken cancellationToken = default)
        {
            if (id is not > 0) return CommPrincipalExpansion.Unsupported("a department mention needs a positive org node id");

            var nodes = await _db.Hierarchicals.AsNoTracking()
                .Select(h => new { h.H_ID, h.H_Parent, h.H_Name, h.H_NameEn, h.IsActive })
                .ToListAsync(cancellationToken);

            var root = nodes.FirstOrDefault(n => n.H_ID == id.Value);
            if (root == null)
                return CommPrincipalExpansion.Of(Array.Empty<int>(), reason: "org node not found");

            var childrenByParent = nodes
                .Where(n => n.H_Parent.HasValue)
                .GroupBy(n => n.H_Parent!.Value)
                .ToDictionary(g => g.Key, g => g.Select(x => x.H_ID).ToList());

            // Iterative walk with a visited set — the tree is user-maintained and can contain a cycle. Exactly
            // the guard OrgHierarchy documents; a recursive walk here would stack-overflow on the same data.
            var nodeIds = new HashSet<int> { root.H_ID };
            var stack = new Stack<int>();
            stack.Push(root.H_ID);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!childrenByParent.TryGetValue(current, out var kids)) continue;
                foreach (var child in kids)
                    if (nodeIds.Add(child)) stack.Push(child);
            }

            var employeeIds = await _db.Employee.AsNoTracking()
                .Where(e => e.DepartmentID.HasValue
                            && nodeIds.Contains(e.DepartmentID.Value)
                            && e.EmpCompanyID == companyId
                            && e.IsActive)
                .Select(e => e.ID)
                .ToListAsync(cancellationToken);

            return CommPrincipalExpansion.Of(employeeIds, root.H_Name, root.H_NameEn ?? root.H_Name);
        }
    }
}
