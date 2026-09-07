using CrossBuy.BL.Construction;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Project BOQ totals (estimate — no GL). ΣLineValue vs the project's ContractValue for a quick sanity check.
	public class BoqSummary
	{
		public int ItemCount { get; set; }
		public decimal TotalValue { get; set; }            // Σ (Qty × UnitPrice)
		public decimal TotalCost { get; set; }             // Σ (material+labor+subcontract+equipment)
		public decimal Margin => Math.Round(TotalValue - TotalCost, 2);
		public decimal MarginPct => TotalValue != 0 ? Math.Round(Margin / TotalValue * 100m, 2) : 0m;
		public decimal? ContractValue { get; set; }        // from Project (P0)
		public decimal VarianceVsContract => Math.Round(TotalValue - (ContractValue ?? 0m), 2);  // ΣValue − contract
	}

	// one inline BOQ row posted from the editor (invoice-lines style). Kind = "main" (section header) or "sub".
	public class BoqRowInput
	{
		public string? Kind { get; set; }
		public string? Code { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string? Unit { get; set; }
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal? MaterialCost { get; set; }
		public decimal? LaborCost { get; set; }
		public decimal? SubcontractCost { get; set; }
		public decimal? EquipmentCost { get; set; }
	}

	// ------------------------------------------------------------------------------------------
	// C1 / CR-01 — an identified BOQ line.
	//
	// `Id` is the whole point. The pre-C1 editor posted rows with no identity, which is why the old
	// save could only express itself as "delete everything, insert what was posted" — and that is what
	// broke the link between a certificate line and the BOQ line it had certified.
	// ------------------------------------------------------------------------------------------
	public class BoqLineInput
	{
		/// 0 = a new line. Non-zero = an EXISTING line, whose identity must survive this save.
		public int Id { get; set; }

		public string? Kind { get; set; }                 // "main" | "sub" (hierarchy, as before)
		public string? Code { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }
		public string? Unit { get; set; }
		public decimal Quantity { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal? MaterialCost { get; set; }
		public decimal? LaborCost { get; set; }
		public decimal? SubcontractCost { get; set; }
		public decimal? EquipmentCost { get; set; }

		/// Optimistic concurrency. When supplied it must match the line's stored token, or the save is
		/// refused rather than overwriting whatever the other user just did.
		public byte[]? ExpectedToken { get; set; }
	}

	/// The outcome of a stable-identity save. Counted rather than inferred, so a caller (and a test)
	/// can assert exactly what the save did.
	public sealed class BoqSaveResult
	{
		public bool ok { get; init; }
		public string? error { get; init; }
		public int unchanged { get; init; }
		public int updated { get; init; }
		public int added { get; init; }
		public int retired { get; init; }

		public static BoqSaveResult Refuse(string message) => new() { ok = false, error = message };
	}

	public interface IBoqService
	{
		Task<List<BoqItem>> GetForProjectAsync(int companyId, int projectId);
		Task<(bool ok, string? error, int id)> SaveItemAsync(BoqItem dto);
		Task<(bool ok, string? error)> DeleteItemAsync(int companyId, int id);
		Task<BoqSummary> GetSummaryAsync(int companyId, int projectId);

		// The editor save. Kept for the existing screen; it now DELEGATES to SaveLinesAsync and no
		// longer deletes anything — see the class comment.
		Task<(bool ok, string? error, int count)> ReplaceAllAsync(int companyId, int projectId, List<BoqRowInput> rows);

		// C1 / CR-01 — the stable-identity save.
		Task<BoqSaveResult> SaveLinesAsync(int companyId, int projectId, List<BoqLineInput> rows,
			int? actorEmployeeId, string reason, CancellationToken ct = default);
	}

	// ==========================================================================================
	// BoqService — C1 / CR-01 REMEDIATION.
	//
	// WHAT WAS WRONG (the defect this class no longer permits):
	//
	//   ReplaceAllAsync used to run
	//        _db.BoqItems.RemoveRange(existing);           // every line of the project
	//        …then re-insert the posted rows              // with BRAND NEW identities
	//   while ProgressBillingService keys previously-billed value by BoqItemId. So ONE BOQ re-save made
	//   work that had already been certified and posted look unbilled, and the next certificate billed
	//   it a second time. Nothing warned, because from the database's point of view nothing was wrong:
	//   the old identities simply no longer existed.
	//
	// WHAT REPLACES IT — a difference, not a replacement:
	//
	//   * a line the caller identifies is UPDATED IN PLACE, keeping its id;
	//   * a line the caller omits is RETIRED (BoqLineState.Status = Retired), never deleted, so every
	//     certificate, measurement and issue that points at it still resolves;
	//   * a line that is REFERENCED by an approved/posted document may not be removed at all — the save
	//     is refused and names the line;
	//   * a contractual quantity or rate may not be changed here once the line is under an approved
	//     commercial revision or has been certified. That change belongs to a revision or an approved
	//     variation (CR-03, CommercialRevisionService);
	//   * every change is audited line-by-line under ONE correlation id, with an actor and a reason.
	//
	// NOTHING IS EVER DELETED BY THIS CLASS. `DeleteItemAsync` is retained for the single-item screen
	// and now refuses a referenced line as well.
	// ==========================================================================================
	public class BoqService : IBoqService
	{
		private const string Source = nameof(BoqService);

		private readonly CrossDbContext _db;
		private readonly IConstructionAuditService _audit;

		public BoqService(CrossDbContext db, IConstructionAuditService audit)
		{
			_db = db;
			_audit = audit;
		}

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		public Task<List<BoqItem>> GetForProjectAsync(int companyId, int projectId) =>
			_db.BoqItems.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync();

		/// The ACTIVE BOQ — retired lines excluded. This is what the editor and every commercial read
		/// should use; GetForProjectAsync stays as-is so existing callers are unaffected.
		public async Task<List<BoqItem>> GetActiveForProjectAsync(int companyId, int projectId)
		{
			var retired = await _db.BoqLineStates.AsNoTracking()
				.Where(s => s.CompanyID == companyId && s.ProjectId == projectId && s.Status == BoqLineStatus.Retired)
				.Select(s => s.BoqItemId).ToListAsync();

			return await _db.BoqItems.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId && !retired.Contains(b.ID))
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync();
		}

		public async Task<(bool ok, string? error, int id)> SaveItemAsync(BoqItem dto)
		{
			if (string.IsNullOrWhiteSpace(dto.Description)) return (false, "Description is required", 0);
			if (dto.Quantity < 0 || dto.UnitPrice < 0) return (false, "Quantity and rate cannot be negative", 0);
			// project must belong to the company
			var prj = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == dto.ProjectId && p.CompanyID == dto.CompanyID);
			if (prj == null) return (false, "Project not found", 0);
			// parent (if any) must be a BOQ item of the SAME project
			if (dto.ParentId.HasValue)
			{
				if (dto.ParentId.Value == dto.ID) return (false, "A line cannot be its own parent", 0);
				var parentOk = await _db.BoqItems.AnyAsync(b => b.ID == dto.ParentId.Value && b.ProjectId == dto.ProjectId && b.CompanyID == dto.CompanyID);
				if (!parentOk) return (false, "Invalid parent line", 0);
			}

			BoqItem e;
			bool isNew = dto.ID <= 0;
			if (!isNew)
				e = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == dto.ID && b.CompanyID == dto.CompanyID) ?? throw new InvalidOperationException("Line not found");
			else
			{
				e = new BoqItem { CompanyID = dto.CompanyID, ProjectId = dto.ProjectId, CreatedAt = DateTime.UtcNow };
				if (dto.SortOrder <= 0)
					e.SortOrder = ((await _db.BoqItems.Where(b => b.CompanyID == dto.CompanyID && b.ProjectId == dto.ProjectId).Select(b => (int?)b.SortOrder).MaxAsync()) ?? 0) + 1;
				else e.SortOrder = dto.SortOrder;
				_db.BoqItems.Add(e);
			}

			// C1 / CR-03: a contractual value may not be changed here once the line is committed.
			if (!isNew && (e.Quantity != dto.Quantity || e.UnitPrice != dto.UnitPrice))
			{
				var guard = await CommercialChangeRefusalAsync(dto.CompanyID, dto.ProjectId, e.ID, e.Code ?? e.Description);
				if (guard != null) return (false, guard, 0);
			}

			e.ParentId = dto.ParentId; if (!isNew && dto.SortOrder > 0) e.SortOrder = dto.SortOrder;
			e.Code = string.IsNullOrWhiteSpace(dto.Code) ? null : dto.Code.Trim();
			e.Description = dto.Description.Trim();
			e.DescriptionEn = string.IsNullOrWhiteSpace(dto.DescriptionEn) ? null : dto.DescriptionEn.Trim();
			e.Unit = string.IsNullOrWhiteSpace(dto.Unit) ? null : dto.Unit.Trim();
			e.Quantity = dto.Quantity; e.UnitPrice = dto.UnitPrice;
			e.MaterialCost = dto.MaterialCost; e.LaborCost = dto.LaborCost;
			e.SubcontractCost = dto.SubcontractCost; e.EquipmentCost = dto.EquipmentCost;
			await _db.SaveChangesAsync();

			if (isNew) await EnsureStateAsync(e, null);
			await _db.SaveChangesAsync();
			return (true, null, e.ID);
		}

		public async Task<(bool ok, string? error)> DeleteItemAsync(int companyId, int id)
		{
			var e = await _db.BoqItems.FirstOrDefaultAsync(b => b.ID == id && b.CompanyID == companyId);
			if (e == null) return (false, "Line not found");
			if (await _db.BoqItems.AnyAsync(b => b.ParentId == id)) return (false, "Delete the child lines first");

			// C1 / CR-01: a referenced line is never removed. Before C1 this method checked only for
			// child items, so a certified line could be deleted outright.
			var refs = await ReferencesAsync(companyId, new[] { id });
			if (refs.TryGetValue(id, out var why))
				return (false, $"Line «{e.Code ?? e.Description}» cannot be deleted — {why}");

			_db.BoqItems.Remove(e);
			var state = await _db.BoqLineStates.FirstOrDefaultAsync(s => s.BoqItemId == id);
			if (state != null) _db.BoqLineStates.Remove(state);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		// ------------------------------------------------------------------------------------------
		// The legacy editor entry point. It no longer replaces anything: it maps the posted rows onto
		// the existing lines (by id where the editor supplies one, else by code, else by description)
		// and delegates to the stable-identity save.
		// ------------------------------------------------------------------------------------------
		public async Task<(bool ok, string? error, int count)> ReplaceAllAsync(int companyId, int projectId, List<BoqRowInput> rows)
		{
			var existing = await GetActiveForProjectAsync(companyId, projectId);
			var byCode = existing.Where(x => !string.IsNullOrWhiteSpace(x.Code))
								 .GroupBy(x => x.Code!.Trim(), StringComparer.OrdinalIgnoreCase)
								 .Where(g => g.Count() == 1)
								 .ToDictionary(g => g.Key, g => g.Single().ID, StringComparer.OrdinalIgnoreCase);
			var byDescription = existing.GroupBy(x => x.Description.Trim(), StringComparer.OrdinalIgnoreCase)
										.Where(g => g.Count() == 1)
										.ToDictionary(g => g.Key, g => g.Single().ID, StringComparer.OrdinalIgnoreCase);

			var claimed = new HashSet<int>();
			var mapped = new List<BoqLineInput>();
			foreach (var r in rows ?? new())
			{
				if (string.IsNullOrWhiteSpace(r.Description)) continue;

				int id = 0;
				if (!string.IsNullOrWhiteSpace(r.Code) && byCode.TryGetValue(r.Code.Trim(), out var byCodeId) && claimed.Add(byCodeId))
					id = byCodeId;
				else if (byDescription.TryGetValue(r.Description.Trim(), out var byDescId) && claimed.Add(byDescId))
					id = byDescId;

				mapped.Add(new BoqLineInput
				{
					Id = id, Kind = r.Kind, Code = r.Code, Description = r.Description, DescriptionEn = r.DescriptionEn,
					Unit = r.Unit, Quantity = r.Quantity, UnitPrice = r.UnitPrice,
					MaterialCost = r.MaterialCost, LaborCost = r.LaborCost,
					SubcontractCost = r.SubcontractCost, EquipmentCost = r.EquipmentCost
				});
			}

			var result = await SaveLinesAsync(companyId, projectId, mapped, null,
				"BOQ editor save");
			return (result.ok, result.error, result.ok ? result.unchanged + result.updated + result.added : 0);
		}

		// ------------------------------------------------------------------------------------------
		// C1 / CR-01 — the stable-identity save.
		// ------------------------------------------------------------------------------------------
		public async Task<BoqSaveResult> SaveLinesAsync(int companyId, int projectId, List<BoqLineInput> rows,
			int? actorEmployeeId, string reason, CancellationToken ct = default)
		{
			if (companyId <= 0) return BoqSaveResult.Refuse("Unresolved company");
			if (string.IsNullOrWhiteSpace(reason))
				return BoqSaveResult.Refuse("A reason is required for a commercial change");

			var project = await _db.Projects.AsNoTracking()
				.FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId, ct);
			if (project == null) return BoqSaveResult.Refuse("Project not found");

			rows ??= new();
			var input = rows.Where(r => !string.IsNullOrWhiteSpace(r.Description)).ToList();

			// ---- duplicate codes inside the posted set --------------------------------------------
			var dupe = input.Where(r => !string.IsNullOrWhiteSpace(r.Code))
							.GroupBy(r => r.Code!.Trim(), StringComparer.OrdinalIgnoreCase)
							.FirstOrDefault(g => g.Count() > 1);
			if (dupe != null)
				return BoqSaveResult.Refuse($"Line code «{dupe.Key}» is duplicated — codes are unique within the contract/project " +
											$"(duplicate line code '{dupe.Key}')");

			// ---- the existing, non-retired lines ---------------------------------------------------
			var states = await _db.BoqLineStates
				.Where(s => s.CompanyID == companyId && s.ProjectId == projectId)
				.ToListAsync(ct);
			var stateByItem = states.ToDictionary(s => s.BoqItemId);
			var retiredIds = states.Where(s => s.Status == BoqLineStatus.Retired).Select(s => s.BoqItemId).ToHashSet();

			var existing = await _db.BoqItems
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.ToListAsync(ct);
			var active = existing.Where(b => !retiredIds.Contains(b.ID)).ToList();
			var existingById = existing.ToDictionary(b => b.ID);

			// ---- every identified row must belong to THIS company and project ----------------------
			// A row naming a line of another company or project is refused with the same message as a
			// row naming a line that does not exist, so ids cannot be probed.
			foreach (var r in input.Where(r => r.Id > 0))
				if (!existingById.ContainsKey(r.Id) || retiredIds.Contains(r.Id))
					return BoqSaveResult.Refuse($"Line #{r.Id} was not found in this project");

			// ---- duplicate codes against lines NOT in the posted set -------------------------------
			var postedIds = input.Where(r => r.Id > 0).Select(r => r.Id).ToHashSet();
			foreach (var r in input.Where(r => !string.IsNullOrWhiteSpace(r.Code)))
			{
				var clash = active.FirstOrDefault(b =>
					!postedIds.Contains(b.ID) &&
					string.Equals(b.Code?.Trim(), r.Code!.Trim(), StringComparison.OrdinalIgnoreCase));
				if (clash != null && clash.ID != r.Id)
					return BoqSaveResult.Refuse($"Line code «{r.Code!.Trim()}» is already in use (duplicate line code)");
			}

			// ---- what is being removed, and may it be? ---------------------------------------------
			var removed = active.Where(b => !postedIds.Contains(b.ID)).ToList();
			var references = await ReferencesAsync(companyId, removed.Select(b => b.ID).ToList());
			var blocked = removed.FirstOrDefault(b => references.ContainsKey(b.ID));
			if (blocked != null)
				return BoqSaveResult.Refuse(
					$"Line «{blocked.Code ?? blocked.Description}» cannot be deleted — {references[blocked.ID]}. " +
					$"Cannot remove line '{blocked.Code ?? blocked.Description}' — use an approved variation");

			// ---- concurrency + commercial-change guards, before anything is written ----------------
			foreach (var r in input.Where(r => r.Id > 0))
			{
				var line = existingById[r.Id];
				stateByItem.TryGetValue(r.Id, out var st);

				if (r.ExpectedToken != null &&
					!ConstructionConcurrency.Matches(r.ExpectedToken, st?.ConcurrencyToken ?? Array.Empty<byte>()))
					return BoqSaveResult.Refuse(ConstructionConcurrency.ConflictMessage);

				if (line.Quantity != r.Quantity || line.UnitPrice != r.UnitPrice)
				{
					var refusal = await CommercialChangeRefusalAsync(companyId, projectId, line.ID, line.Code ?? line.Description);
					if (refusal != null) return BoqSaveResult.Refuse(refusal);
				}
			}

			// ==========================================================================================
			// Everything below this line writes. All validation is above it, so a refusal leaves the
			// database untouched — which is what makes "nothing was written" assertable.
			// ==========================================================================================
			var correlation = Guid.NewGuid();
			var drafts = new List<ConstructionAuditDraft>();
			var now = DateTime.UtcNow;
			int unchanged = 0, updated = 0, added = 0;

			int sort = 1;
			int? lastMainId = null;
			var newlyAdded = new List<(BoqItem item, BoqLineInput row)>();

			foreach (var r in input)
			{
				bool isSub = string.Equals(r.Kind, "sub", StringComparison.OrdinalIgnoreCase);

				if (r.Id > 0)
				{
					var line = existingById[r.Id];
					bool changed = false;

					void Track(string field, string? oldText, string? newText, decimal? oldNum = null, decimal? newNum = null)
					{
						drafts.Add(oldNum.HasValue
							? ConstructionAuditDraft.Numeric(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
								line.ID, line.ID, field, oldNum.Value, newNum!.Value, ConstructionChangeKind.Updated,
								reason, actorEmployeeId, Source, correlation)
							: ConstructionAuditDraft.Text(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
								line.ID, line.ID, field, oldText, newText, ConstructionChangeKind.Updated,
								reason, actorEmployeeId, Source, correlation));
						changed = true;
					}

					var newCode = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim();
					var newDesc = r.Description!.Trim();
					var newDescEn = string.IsNullOrWhiteSpace(r.DescriptionEn) ? null : r.DescriptionEn.Trim();
					var newUnit = string.IsNullOrWhiteSpace(r.Unit) ? null : r.Unit.Trim();

					if (line.Code != newCode) { Track("Code", line.Code, newCode); line.Code = newCode; }
					if (line.Description != newDesc) { Track("Description", line.Description, newDesc); line.Description = newDesc; }
					if (line.DescriptionEn != newDescEn) { Track("DescriptionEn", line.DescriptionEn, newDescEn); line.DescriptionEn = newDescEn; }
					if (line.Unit != newUnit) { Track("Unit", line.Unit, newUnit); line.Unit = newUnit; }
					if (line.Quantity != r.Quantity) { Track("Quantity", null, null, line.Quantity, r.Quantity); line.Quantity = r.Quantity; }
					if (line.UnitPrice != r.UnitPrice) { Track("UnitPrice", null, null, line.UnitPrice, r.UnitPrice); line.UnitPrice = r.UnitPrice; }

					line.MaterialCost = r.MaterialCost; line.LaborCost = r.LaborCost;
					line.SubcontractCost = r.SubcontractCost; line.EquipmentCost = r.EquipmentCost;

					// Sort order and hierarchy are presentation, NOT identity: they move freely and are
					// deliberately not audited as commercial changes.
					line.SortOrder = sort++;
					line.ParentId = isSub ? lastMainId : null;
					if (!isSub) lastMainId = line.ID;

					if (changed)
					{
						updated++;
						var st = stateByItem[line.ID];
						st.ConcurrencyToken = ConstructionConcurrency.NewToken();
						st.UpdatedAt = now;
						st.UpdatedBy = actorEmployeeId;
					}
					else unchanged++;
				}
				else
				{
					var item = new BoqItem
					{
						CompanyID = companyId,
						ProjectId = projectId,
						SortOrder = sort++,
						Code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code.Trim(),
						Description = r.Description!.Trim(),
						DescriptionEn = string.IsNullOrWhiteSpace(r.DescriptionEn) ? null : r.DescriptionEn.Trim(),
						Unit = string.IsNullOrWhiteSpace(r.Unit) ? null : r.Unit.Trim(),
						Quantity = r.Quantity < 0 ? 0 : r.Quantity,
						UnitPrice = r.UnitPrice < 0 ? 0 : r.UnitPrice,
						MaterialCost = r.MaterialCost, LaborCost = r.LaborCost,
						SubcontractCost = r.SubcontractCost, EquipmentCost = r.EquipmentCost,
						CreatedAt = now
					};
					_db.BoqItems.Add(item);
					newlyAdded.Add((item, r));
					added++;
				}
			}

			// ---- retirement: the row STAYS, it just leaves the working BOQ -------------------------
			int retiredCount = 0;
			foreach (var b in removed)
			{
				var st = stateByItem.TryGetValue(b.ID, out var found) ? found : null;
				if (st == null)
				{
					st = NewState(companyId, projectId, b.ID, actorEmployeeId, now);
					_db.BoqLineStates.Add(st);
					stateByItem[b.ID] = st;
				}
				st.Status = BoqLineStatus.Retired;
				st.RetiredAt = now;
				st.RetiredBy = actorEmployeeId;
				st.RetiredReason = reason.Trim();
				st.ConcurrencyToken = ConstructionConcurrency.NewToken();
				st.UpdatedAt = now;
				st.UpdatedBy = actorEmployeeId;

				drafts.Add(ConstructionAuditDraft.Text(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
					b.ID, b.ID, "Status", BoqLineStatus.Active, BoqLineStatus.Retired,
					ConstructionChangeKind.Retired, reason, actorEmployeeId, Source, correlation));
				retiredCount++;
			}

			// A first save assigns identities to the new lines; their state rows and audit follow, so an
			// audit row can carry the real id rather than a placeholder.
			await _db.SaveChangesAsync(ct);

			foreach (var (item, row) in newlyAdded)
			{
				var st = NewState(companyId, projectId, item.ID, actorEmployeeId, now);
				_db.BoqLineStates.Add(st);

				drafts.Add(ConstructionAuditDraft.Text(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
					item.ID, item.ID, "Description", null, item.Description,
					ConstructionChangeKind.Created, reason, actorEmployeeId, Source, correlation));
				drafts.Add(ConstructionAuditDraft.Numeric(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
					item.ID, item.ID, "Quantity", 0m, item.Quantity,
					ConstructionChangeKind.Created, reason, actorEmployeeId, Source, correlation));
				drafts.Add(ConstructionAuditDraft.Numeric(companyId, projectId, ConstructionAuditEntityTypes.BoqItem,
					item.ID, item.ID, "UnitPrice", 0m, item.UnitPrice,
					ConstructionChangeKind.Created, reason, actorEmployeeId, Source, correlation));
			}

			// Ensure every pre-existing line that had no state row gets one, so identity and concurrency
			// are complete from the first C1 save onward.
			foreach (var b in existing.Where(b => !stateByItem.ContainsKey(b.ID)))
				_db.BoqLineStates.Add(NewState(companyId, projectId, b.ID, actorEmployeeId, now));

			_audit.Record(drafts);
			await _db.SaveChangesAsync(ct);

			return new BoqSaveResult
			{
				ok = true, unchanged = unchanged, updated = updated, added = added, retired = retiredCount
			};
		}

		// ------------------------------------------------------------------------------------------
		// helpers
		// ------------------------------------------------------------------------------------------
		private static BoqLineState NewState(int companyId, int projectId, int boqItemId, int? actor, DateTime now) =>
			new()
			{
				CompanyID = companyId, ProjectId = projectId, BoqItemId = boqItemId,
				Status = BoqLineStatus.Active, CreatedAt = now, CreatedBy = actor,
				ConcurrencyToken = ConstructionConcurrency.NewToken()
			};

		private async Task EnsureStateAsync(BoqItem item, int? actor)
		{
			if (await _db.BoqLineStates.AnyAsync(s => s.BoqItemId == item.ID)) return;
			_db.BoqLineStates.Add(NewState(item.CompanyID, item.ProjectId, item.ID, actor, DateTime.UtcNow));
		}

		/// Why each of the given lines may not be removed. A line absent from the result is free to retire.
		/// Every branch names the document that holds it, because "cannot delete" without a reason is
		/// indistinguishable from a bug.
		private async Task<Dictionary<int, string>> ReferencesAsync(int companyId, IReadOnlyCollection<int> ids)
		{
			var result = new Dictionary<int, string>();
			if (ids.Count == 0) return result;

			var certified = await (from l in _db.ProgressBillingLines.AsNoTracking()
								   join h in _db.ProgressBillings.AsNoTracking() on l.BillingId equals h.ID
								   where h.CompanyID == companyId && l.BoqItemId != null && ids.Contains(l.BoqItemId.Value)
										 && (h.Status == "Approved" || h.Status == "Posted")
								   select l.BoqItemId!.Value).Distinct().ToListAsync();
			foreach (var id in certified) result[id] = "Certified on a client certificate";

			var measured = await (from l in _db.ProjectProgressLines.AsNoTracking()
								  join h in _db.ProjectProgresses.AsNoTracking() on l.ProgressId equals h.ID
								  where h.CompanyID == companyId && l.BoqItemId != null && ids.Contains(l.BoqItemId.Value)
										&& h.Status == "Confirmed"
								  select l.BoqItemId!.Value).Distinct().ToListAsync();
			foreach (var id in measured) if (!result.ContainsKey(id)) result[id] = "Used by a confirmed measurement";

			var issued = await (from l in _db.ProjectMaterialIssueLines.AsNoTracking()
								join h in _db.ProjectMaterialIssues.AsNoTracking() on l.IssueId equals h.ID
								where h.CompanyID == companyId && l.BoqItemId != null && ids.Contains(l.BoqItemId.Value)
									  && h.Status == "Posted"
								select l.BoqItemId!.Value).Distinct().ToListAsync();
			foreach (var id in issued) if (!result.ContainsKey(id)) result[id] = "Used by a posted material issue";

			var scoped = await _db.SubcontractScopes.AsNoTracking()
				.Where(s => s.CompanyID == companyId && s.BoqItemId != null && ids.Contains(s.BoqItemId.Value)
							&& s.Status == SubcontractScopeStatus.Active)
				.Select(s => s.BoqItemId!.Value).Distinct().ToListAsync();
			foreach (var id in scoped) if (!result.ContainsKey(id)) result[id] = "Allocated to a subcontract scope";

			var varied = await _db.VariationOrderLines.AsNoTracking()
				.Where(l => l.BoqItemId != null && ids.Contains(l.BoqItemId.Value))
				.Join(_db.VariationOrders.AsNoTracking().Where(v => v.CompanyID == companyId && v.Status == "Approved"),
					  l => l.VariationOrderId, v => v.ID, (l, v) => l.BoqItemId!.Value)
				.Distinct().ToListAsync();
			foreach (var id in varied) if (!result.ContainsKey(id)) result[id] = "Covered by an approved variation";

			return result;
		}

		/// C1 / CR-03 — why a contractual quantity/rate may not be changed on this path, or null when it may.
		private async Task<string?> CommercialChangeRefusalAsync(int companyId, int projectId, int boqItemId, string label)
		{
			var state = await _db.BoqLineStates.AsNoTracking().FirstOrDefaultAsync(s => s.BoqItemId == boqItemId);
			bool underRevision = state?.CurrentRevisionId != null;

			var refs = await ReferencesAsync(companyId, new[] { boqItemId });
			bool referenced = refs.ContainsKey(boqItemId);

			if (!underRevision && !referenced) return null;

			return $"The quantity or rate of line «{label}» cannot be edited directly — a commercial revision or an approved variation is required " +
				   $"(a contractual quantity or rate change on '{label}' requires an approved commercial revision or variation)";
		}

		public async Task<BoqSummary> GetSummaryAsync(int companyId, int projectId)
		{
			var items = await GetActiveForProjectAsync(companyId, projectId);
			var cv = await _db.Projects.AsNoTracking().Where(p => p.ID == projectId && p.CompanyID == companyId).Select(p => p.ContractValue).FirstOrDefaultAsync();
			return new BoqSummary
			{
				ItemCount = items.Count,
				TotalValue = R(items.Sum(i => i.LineValue)),
				TotalCost = R(items.Sum(i => i.EstimatedCost)),
				ContractValue = cv
			};
		}
	}
}
