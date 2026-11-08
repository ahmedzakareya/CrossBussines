using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Controllers
{
	// ==========================================================================================
	// HR — THE ORGANISATION CHART, in the owner-approved prototype design (design-output/hr-prototype, #/org).
	//
	// One company's tree, top-down, every person shown with their photo (initials when there is none). The tree
	// is the caller's company only: GetByCompanyAsync walks from the company's own node, so a parent company's
	// chart includes its subsidiaries and nothing else. A position with nobody in it is drawn as a dashed
	// "vacant" node. Adding a unit reuses the existing AddHierarchicalItem endpoint and its organization-manage
	// right; nothing here writes.
	// ==========================================================================================
	public partial class AdminController
	{
		public sealed record OrgChartNode(int Id, int? ParentId, int Type, string Name, string? NameEn, string? Photo,
			int? EmployeeId, string? Title, string? TitleEn, bool Active);

		public sealed record OrgChartModel(IReadOnlyList<OrgChartNode> Nodes, int People, int Units, int Positions, int Vacant, bool CanManage);

		private async Task<OrgChartModel> BuildOrgChartAsync(int companyId, CancellationToken ct)
		{
			var nodes = await administrativeStructureService.GetByCompanyAsync(companyId);
			var empIds = nodes.Where(n => n.H_Type == 5 && n.H_ObjectID is > 0).Select(n => n.H_ObjectID!.Value).Distinct().ToList();

			// The people in the chart, company-bounded a second time: the tree has no company column, so the
			// employee row's own company decides whether a name and photo may be shown.
			var people = await Db.Employee.AsNoTracking()
				.Where(e => empIds.Contains(e.ID) && e.EmpCompanyID == companyId)
				.Select(e => new { e.ID, e.FullName, e.FullNameEn, e.JobTitleID, e.IsActive })
				.ToListAsync(ct);
			var titleIds = people.Select(p => p.JobTitleID).Distinct().ToList();
			var titles = await Db.JobTitles.AsNoTracking().Where(t => titleIds.Contains(t.ID))
				.ToDictionaryAsync(t => t.ID, t => (ar: t.TitleAr, en: t.Title), ct);
			var ctx = await BusinessContexts.TryGetCurrentAsync();
			var photos = ctx == null ? new Dictionary<int, string>() : await CrossBuy.BL.Platform.EmployeePhotos.ResolveAsync(Db, ctx, empIds, ct);
			var byId = people.ToDictionary(p => p.ID);

			var list = new List<OrgChartNode>();
			foreach (var n in nodes)
			{
				int type = n.H_Type ?? 3;
				if (type == 5 && n.H_ObjectID is int eid && byId.TryGetValue(eid, out var p))
				{
					titles.TryGetValue(p.JobTitleID, out var t);
					list.Add(new OrgChartNode(n.H_ID, n.H_Parent, 5, p.FullName, p.FullNameEn, photos.GetValueOrDefault(eid), eid, t.ar, t.en, p.IsActive));
				}
				else if (type == 5)
				{
					continue;   // an employee node whose employee is not in this company is never drawn
				}
				else
				{
					list.Add(new OrgChartNode(n.H_ID, n.H_Parent, type, n.H_Name ?? n.H_NameEn ?? "", n.H_NameEn, null, null, null, null, n.IsActive ?? true));
				}
			}
			int positions = list.Count(x => x.Type == 4);
			int vacant = list.Count(x => x.Type == 4 && !list.Any(c => c.ParentId == x.Id && c.Type == 5));
			bool canManage = ctx != null && await HrAccess.CanAsync(ctx, CrossBuy.BL.HrActions.OrganizationManage, null);
			return new OrgChartModel(list, list.Count(x => x.Type == 5), list.Count(x => x.Type is 2 or 3), positions, vacant, canManage);
		}
	}
}
