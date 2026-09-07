using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;

namespace CrossBuy.BL
{
	// TM-2: resolves a task's polymorphic link (EntityType + EntityId) to a display label + a deep-link URL, and powers the
	// record picker (types + search). Read-only — NO GL/stock. Types with a real per-id detail screen get a deep link;
	// types with only a list screen link to the list; a type with no screen returns Url = null (label shown, no "open").
	//
	// PLATFORM KERNEL (ADR-002): this is now a COMPATIBILITY WRAPPER. The type table and every search /
	// resolve query moved to IEntityRegistry, which is the single source of truth for entity codes. The DTOs
	// and method signatures below are unchanged so TasksController and DevSeedController compile and behave
	// exactly as before; new code must depend on IEntityRegistry directly, not on this wrapper.
	public class TaskLinkTypeDto { public string Key { get; set; } = ""; public string LabelAr { get; set; } = ""; public string LabelEn { get; set; } = ""; public string Icon { get; set; } = ""; }
	public class TaskLinkOptionDto { public int Id { get; set; } public string Label { get; set; } = ""; }
	public class TaskLinkDto { public string TypeLabelAr { get; set; } = ""; public string TypeLabelEn { get; set; } = ""; public string Label { get; set; } = ""; public string? Url { get; set; } public string Icon { get; set; } = ""; }

	public interface ITaskLinkResolver
	{
		List<TaskLinkTypeDto> Types();
		Task<List<TaskLinkOptionDto>> SearchAsync(int companyId, string entityType, string? term);
		Task<TaskLinkDto?> ResolveAsync(int companyId, string? entityType, int? entityId);
		Task<string?> PartyNameAsync(int companyId, string? partyType, int? partyId);   // TM-9: Supplier(Vendor)/Customer name for a scheduled task
	}

	public class TaskLinkResolver : ITaskLinkResolver
	{
		private readonly IEntityRegistry _registry;
		public TaskLinkResolver(IEntityRegistry registry) { _registry = registry; }

		// The legacy signatures carry the company explicitly, so the context is built around that company
		// rather than resolved from the request — the registry only uses it for company scoping.
		private static BusinessContext Ctx(int companyId) => BusinessContext.ForSystem(companyId);

		// Picker membership is a registry flag: "Supplier" is searchable but was never listed by the picker,
		// so it stays out of this list exactly as before.
		public List<TaskLinkTypeDto> Types() => _registry.GetDefinitions()
			.Where(d => d.ListedInRecordPicker)
			.Select(d => new TaskLinkTypeDto { Key = d.Code, LabelAr = d.DisplayNameAr, LabelEn = d.DisplayNameEn, Icon = d.Icon })
			.ToList();

		public async Task<List<TaskLinkOptionDto>> SearchAsync(int companyId, string entityType, string? term)
		{
			// An unknown type returned an empty list before the kernel; keep that instead of throwing.
			if (!_registry.IsValid(entityType)) return new();
			var rows = await _registry.SearchAsync(entityType, term, Ctx(companyId));
			return rows.Select(r => new TaskLinkOptionDto { Id = r.EntityId, Label = r.Label }).ToList();
		}

		public async Task<TaskLinkDto?> ResolveAsync(int companyId, string? entityType, int? entityId)
		{
			if (string.IsNullOrWhiteSpace(entityType) || entityId == null || entityId <= 0) return null;
			// Pre-kernel this looked the type up in Types(); a type absent from the picker list (Supplier)
			// resolved to null. Gate on the same flag to keep that behaviour.
			if (!_registry.TryGetDefinition(entityType, out var def) || !def!.ListedInRecordPicker) return null;

			var res = await _registry.ResolveAsync(def.Code, entityId.Value, Ctx(companyId));
			return new TaskLinkDto
			{
				TypeLabelAr = def.DisplayNameAr,
				TypeLabelEn = def.DisplayNameEn,
				Icon = def.Icon,
				Label = res.Label,        // "#id" when the record is gone — safe, same as before
				Url = res.Url,            // null when the record is gone or the type has no screen
			};
		}

		// TM-9: resolve a scheduled task's expected party (Vendor/Customer) name for display. Read-only.
		public async Task<string?> PartyNameAsync(int companyId, string? partyType, int? partyId)
		{
			if (partyId == null || partyId <= 0 || string.IsNullOrWhiteSpace(partyType)) return null;
			if (partyType != EntityRegistry.Supplier && partyType != EntityRegistry.Customer) return null;
			var res = await _registry.ResolveAsync(partyType, partyId.Value, Ctx(companyId));
			return res.Found ? res.Label : null;
		}
	}
}