using AutoMapper;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Admin;
using CrossBuy.ViewModel;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using static Azure.Core.HttpHeader;

namespace CrossBuy.BL
{
	public class AdministrativeStructureService : IAdministrativeStructureService
	{
		private readonly CrossDbContext _context;
		private readonly IMapper _mapper;
		private readonly IServiceProvider _serviceProvider;
		private readonly IHttpContextAccessor _httpContextAccessor;


		public AdministrativeStructureService(CrossDbContext context, IMapper mapper, IServiceProvider serviceProvider, IHttpContextAccessor httpContextAccessor)
		{
			this._context = context;
			_serviceProvider = serviceProvider;
			//_context = context;
			_mapper = mapper;
			_httpContextAccessor = httpContextAccessor;
		}
		public Task<List<HierarchicalDto>> GataAll()
		{
			var hierarchy = _context.Hierarchicals
				.Select(h => new HierarchicalDto
				{
					H_ID = h.H_ID,
					H_Name = h.H_Name,
					H_Parent = h.H_Parent,
					H_NameEn = h.H_NameEn,
					H_Notes = h.H_Notes,
					H_Type = h.H_Type,
					H_ObjectID = h.H_ObjectID,
					Sort = h.Sort,
					IsActive = h.IsActive,
					H_ParentNAme = h.Parent != null ? h.Parent.H_Name : null
				}).ToList();

			ResolveImages(hierarchy);
			return Task.FromResult(hierarchy);
		}

		// Fill each node's Image: employee nodes (type 5) -> ProfileImage, company nodes (type 1) -> CompanyImage
		private void ResolveImages(List<HierarchicalDto> nodes)
		{
			var empImgs = _context.Employee
				.Where(e => e.ProfileImage != null && e.ProfileImage != "")
				.Select(e => new { e.ID, e.ProfileImage })
				.ToDictionary(e => e.ID, e => e.ProfileImage);
			var compImgs = _context.Companies
				.Where(c => c.CompanyImage != null && c.CompanyImage != "")
				.Select(c => new { c.CompanyID, c.CompanyImage })
				.ToDictionary(c => c.CompanyID, c => c.CompanyImage);

			foreach (var n in nodes)
			{
				if (n.H_ObjectID == null) continue;
				if (n.H_Type == 5 && empImgs.TryGetValue(n.H_ObjectID.Value, out var pi)) n.Image = NormImg(pi);
				else if (n.H_Type == 1 && compImgs.TryGetValue(n.H_ObjectID.Value, out var ci)) n.Image = NormImg(ci);
			}
		}

		// Normalize stored paths (e.g. "Files\\1\\x.png") into web paths ("/Files/1/x.png")
		private static string NormImg(string p)
		{
			if (string.IsNullOrEmpty(p)) return p;
			p = p.Replace("\\", "/");
			if (!p.StartsWith("/") && !p.StartsWith("http")) p = "/" + p;
			return p;
		}

		public async Task<List<HierarchicalDto>> GetByCompanyAsync(int companyId)
		{
			var all = await _context.Hierarchicals.ToListAsync();

			var root = all.FirstOrDefault(h => h.H_ObjectID == companyId && h.H_Type == 1);
			if (root == null) return new List<HierarchicalDto>();

			// The company node itself comes first. It used to be left out, so every tree built from this list had
			// no root and drew nothing (the HR structure screen showed an empty tree once it was scoped here).
			var result = new List<Hierarchical> { root };
			CollectDescendants(all, root.H_ID, result);

			return result.Select(h => new HierarchicalDto
			{
				H_ID       = h.H_ID,
				H_Name     = h.H_Name,
				H_NameEn   = h.H_NameEn,
				// The root is a root here even when the company sits under a parent company: its parent is
				// outside this list, and a dangling parent id would orphan the whole tree.
				H_Parent   = h.H_ID == root.H_ID ? null : h.H_Parent,
				H_Notes    = h.H_Notes,
				H_Type     = h.H_Type,
				H_ObjectID = h.H_ObjectID,
				Sort       = h.Sort,
				IsActive   = h.IsActive,
				H_ParentNAme = all.FirstOrDefault(p => p.H_ID == h.H_Parent)?.H_Name
			}).ToList();
		}

		private static void CollectDescendants(List<Hierarchical> all, int parentId, List<Hierarchical> result)
		{
			foreach (var child in all.Where(h => h.H_Parent == parentId))
			{
				result.Add(child);
				CollectDescendants(all, child.H_ID, result);
			}
		}

		public async Task<HierarchicalDto> GetParentData(int ID)
		{
			var hierarchy = await _context.Hierarchicals
				.Where(c => c.H_ID == ID)
				.Select(h => new HierarchicalDto
				{
					H_ID = h.H_ID,
					H_Name = h.H_Name,
					H_Parent = h.H_Parent,
					H_NameEn = h.H_NameEn,
					H_Notes = h.H_Notes,
					H_Type = h.H_Type,
					H_ObjectID = h.H_ObjectID,
					Sort = h.Sort,
					IsActive = h.IsActive,
					H_ParentNAme = h.Parent != null ? h.Parent.H_Name : null
				})
				.FirstOrDefaultAsync();

			return hierarchy;
		}

		// SEC-01 part C — THE ACTOR COMES FROM THE RESOLVED CONTEXT, NOT FROM THE SESSION.
		//
		// WHAT WAS WRONG, AND IT WAS TWO THINGS.
		//
		// 1. The actor was deserialised from `HttpContext.Session["Employee"]`. Stage 1 removed exactly
		//    this pattern everywhere else: a session blob is client-influenced state that survives role
		//    changes, deactivation and company reassignment, and `BusinessContextFactory` exists to replace
		//    it — it reads the signed-in employee's own row and a session value that disagrees loses.
		//
		// 2. `employee.ID` was dereferenced with no null check, so a request arriving without that session
		//    key did not refuse — it threw NullReferenceException AFTER the method had been entered. A
		//    write path whose failure mode is a 500 is not a refusal; it is an accident that happened to
		//    stop short.
		//
		// NOW IT FAILS CLOSED, AND IT REFUSES THROUGH THE CALLER'S EXISTING PATH. An unresolved context,
		// or one carrying no employee, throws BEFORE the entity is constructed, so nothing is written.
		//
		// WHY AN EXCEPTION RATHER THAN A NULL RETURN. The sole caller is
		// AdminController.AddHierarchicalItem, which already wraps this call in try/catch and answers
		// `{ success = false, message }`. Returning null would have moved the NullReferenceException from
		// here to `dto.H_ID` one line later — the same crash, one frame further out, and a change whose
		// benefit never reaches the user. Throwing lands in a handler that already exists, so the refusal
		// is clean with NO edit to that controller, which this wave does not own (it is staged by another
		// TAB). The message is deliberately generic: the caller echoes `ex.Message` to the client, so it
		// must not name a company, a role or whether a record exists.
		//
		// WHAT THIS DOES NOT FIX, stated so nobody reads more into it than is here: this method still has
		// no PERMISSION check and `Hierarchical` still has no company column, so the tree remains global.
		// Both belong to AdminController.AddHierarchicalItem and to a schema decision, and both are blocked
		// in this wave — the controller is staged by another TAB and the column needs owner approval.
		public async Task<HierarchicalDto> Save(HierarchicalDto dto)
		{
			var contexts = _serviceProvider.GetService(typeof(CrossBuy.BL.Platform.IBusinessContextAccessor))
				as CrossBuy.BL.Platform.IBusinessContextAccessor;
			var context = contexts == null ? null : await contexts.TryGetCurrentAsync();
			if (context?.EmployeeId is not > 0)
				throw new UnauthorizedAccessException("Not permitted.");

			var newEntity = new Hierarchical
			{
				H_Name = dto.H_Name,
				H_NameEn = dto.H_NameEn,
				H_Parent = dto.H_Parent,
				H_Type = 3,
				IsActive = true,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = context.EmployeeId.Value
			};

			// 3. أضفه إلى الـ DbContext واحفظ
			_context.Hierarchicals.Add(newEntity);
			await _context.SaveChangesAsync();

			// 4. حوله إلى DTO للرد
			return new HierarchicalDto
			{
				H_ID = newEntity.H_ID,
				H_Name = newEntity.H_Name,
				H_NameEn = newEntity.H_NameEn,
				H_Parent = newEntity.H_Parent,
				H_Type = newEntity.H_Type,
				Sort = newEntity.Sort,
				IsActive = newEntity.IsActive
			};
		}
	}
}

