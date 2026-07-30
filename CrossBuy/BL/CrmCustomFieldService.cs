using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Crm;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	public class CrmFieldWithValue
	{
		public CrmCustomField Field { get; set; } = null!;
		public string? Value { get; set; }
	}

	public interface ICrmCustomFieldService
	{
		Task<List<CrmCustomField>> GetFieldsAsync(int companyId, string? entityType, bool activeOnly);
		Task<(bool ok, string? error, int id)> SaveFieldAsync(CrmCustomField dto);
		Task DeleteFieldAsync(int companyId, int id);
		/// active fields for an entity type, each paired with the stored value for entityId (0 = new → blank).
		Task<List<CrmFieldWithValue>> GetForEntityAsync(int companyId, string entityType, int entityId);
		Task SaveValuesAsync(int companyId, string entityType, int entityId, Dictionary<int, string?> values);
	}

	public class CrmCustomFieldService : ICrmCustomFieldService
	{
		private readonly CrossDbContext _db;
		public CrmCustomFieldService(CrossDbContext db) { _db = db; }

		public Task<List<CrmCustomField>> GetFieldsAsync(int companyId, string? entityType, bool activeOnly)
		{
			var q = _db.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == companyId);
			if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(f => f.EntityType == entityType);
			if (activeOnly) q = q.Where(f => f.IsActive);
			return q.OrderBy(f => f.EntityType).ThenBy(f => f.SortOrder).ThenBy(f => f.ID).ToListAsync();
		}

		public async Task<(bool ok, string? error, int id)> SaveFieldAsync(CrmCustomField dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Label)) return (false, "التسمية مطلوبة", 0);
			var et = new[] { "Lead", "Opportunity", "Account", "Activity" }.Contains(dto.EntityType) ? dto.EntityType : "Lead";
			var ft = new[] { "Text", "Number", "Date", "Select", "Checkbox" }.Contains(dto.FieldType) ? dto.FieldType : "Text";
			// derive a stable key if not supplied
			var key = string.IsNullOrWhiteSpace(dto.FieldKey) ? "cf_" + Guid.NewGuid().ToString("N").Substring(0, 8) : dto.FieldKey.Trim();
			CrmCustomField e;
			if (dto.ID > 0) e = await _db.CrmCustomFields.FirstOrDefaultAsync(f => f.ID == dto.ID && f.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("الحقل غير موجود");
			else { e = new CrmCustomField { CompanyID = dto.CompanyID, FieldKey = key }; _db.CrmCustomFields.Add(e); }
			e.EntityType = et; e.Label = dto.Label.Trim(); e.LabelEn = dto.LabelEn; e.FieldType = ft;
			e.Options = dto.Options; e.OptionsEn = dto.OptionsEn; e.Required = dto.Required; e.SortOrder = dto.SortOrder; e.IsActive = dto.IsActive;
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task DeleteFieldAsync(int companyId, int id)
		{
			var e = await _db.CrmCustomFields.FirstOrDefaultAsync(f => f.ID == id && f.CompanyID == companyId);
			if (e == null) return;
			_db.CrmCustomFieldValues.RemoveRange(_db.CrmCustomFieldValues.Where(v => v.FieldId == id));
			_db.CrmCustomFields.Remove(e);
			await _db.SaveChangesAsync();
		}

		public async Task<List<CrmFieldWithValue>> GetForEntityAsync(int companyId, string entityType, int entityId)
		{
			var fields = await _db.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == companyId && f.EntityType == entityType && f.IsActive)
				.OrderBy(f => f.SortOrder).ThenBy(f => f.ID).ToListAsync();
			var vals = entityId > 0
				? await _db.CrmCustomFieldValues.AsNoTracking().Where(v => v.CompanyID == companyId && v.EntityType == entityType && v.EntityId == entityId).ToListAsync()
				: new();
			return fields.Select(f => new CrmFieldWithValue { Field = f, Value = vals.FirstOrDefault(v => v.FieldId == f.ID)?.Value }).ToList();
		}

		public async Task SaveValuesAsync(int companyId, string entityType, int entityId, Dictionary<int, string?> values)
		{
			if (entityId <= 0 || values == null || values.Count == 0) return;
			var fieldIds = await _db.CrmCustomFields.AsNoTracking().Where(f => f.CompanyID == companyId && f.EntityType == entityType).Select(f => f.ID).ToListAsync();
			var existing = await _db.CrmCustomFieldValues.Where(v => v.CompanyID == companyId && v.EntityType == entityType && v.EntityId == entityId).ToListAsync();
			foreach (var kv in values)
			{
				if (!fieldIds.Contains(kv.Key)) continue;   // only known fields for this entity type
				var row = existing.FirstOrDefault(v => v.FieldId == kv.Key);
				if (row == null) { row = new CrmCustomFieldValue { CompanyID = companyId, FieldId = kv.Key, EntityType = entityType, EntityId = entityId }; _db.CrmCustomFieldValues.Add(row); }
				row.Value = kv.Value;
			}
			await _db.SaveChangesAsync();
		}
	}
}
