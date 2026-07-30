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

			var result = new List<Hierarchical>();
			CollectDescendants(all, root.H_ID, result);

			return result.Select(h => new HierarchicalDto
			{
				H_ID       = h.H_ID,
				H_Name     = h.H_Name,
				H_NameEn   = h.H_NameEn,
				H_Parent   = h.H_Parent,
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

		public async Task<HierarchicalDto> Save(HierarchicalDto dto)
		{
			var employeeJson = _httpContextAccessor.HttpContext?.Session.GetString("Employee");
			var employee = employeeJson != null ? JsonConvert.DeserializeObject<EmployeeViewModel>(employeeJson) : null;


			var newEntity = new Hierarchical
			{
				H_Name = dto.H_Name,
				H_NameEn = dto.H_NameEn,
				H_Parent = dto.H_Parent,
				H_Type = 3,
				IsActive = true,
				CreatedAt = DateTime.UtcNow,
				CreatedBy = employee.ID
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

