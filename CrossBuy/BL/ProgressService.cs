using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Accounting;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL
{
	// Projects & Contracting — P3 Execution / progress. OPERATIONAL ONLY: no GL, no journal entries, no new writer.
	// Cumulative snapshots per BOQ item; overall % is value-weighted (Σ executed value ÷ Σ BOQ value). Period value =
	// this cumulative − the previous measurement's cumulative (basis for المستخلصات in P4). Flexible; ProjectId.

	// one editor row (posted from the inline table) — cumulative executed qty (+ optional manual % for lump-sum items)
	public class ProgressRowInput
	{
		public int? BoqItemId { get; set; }
		public decimal CumulativeQty { get; set; }
		public decimal? ManualPercent { get; set; }
	}

	// a computed line for display in the editor (BOQ item context + previous/current + auto %/value/period)
	public class ProgressLineView
	{
		public int? BoqItemId { get; set; }
		public string? Code { get; set; }
		public string Description { get; set; } = "";
		public string? Unit { get; set; }
		public decimal BoqQty { get; set; }
		public decimal UnitPrice { get; set; }
		public decimal BoqValue { get; set; }
		public decimal PrevCumulativeQty { get; set; }
		public decimal PrevExecutedValue { get; set; }
		public decimal CumulativeQty { get; set; }
		public decimal? ManualPercent { get; set; }
		public decimal Percent { get; set; }          // capped at 100
		public decimal ExecutedValue { get; set; }     // capped at BoqValue
		public decimal PeriodValue { get; set; }       // ExecutedValue − PrevExecutedValue
		public bool OverBoq { get; set; }              // cumulative exceeds BOQ qty (variation-order flag)
	}

	public class ProgressEditModel
	{
		public Project? Project { get; set; }
		public int MeasurementId { get; set; }         // 0 = new
		public int MeasurementNo { get; set; }
		public DateTime MeasurementDate { get; set; }
		public string? Note { get; set; }
		public string Status { get; set; } = "Draft";
		public bool HasBoq { get; set; }
		public List<ProgressLineView> Lines { get; set; } = new();
		public decimal OverallPercent { get; set; }
		public decimal ExecutedValue { get; set; }
		public decimal PeriodValue { get; set; }
	}

	public class ProgressSummary
	{
		public int MeasurementCount { get; set; }
		public decimal OverallPercent { get; set; }    // current = latest measurement
		public decimal ExecutedValue { get; set; }
		public DateTime? LatestDate { get; set; }
	}

	public interface IProgressService
	{
		Task<List<ProjectProgress>> GetMeasurementsAsync(int companyId, int projectId);
		Task<ProgressEditModel> BuildEditModelAsync(int companyId, int projectId, int? measurementId);
		Task<(bool ok, string? error, int id)> SaveMeasurementAsync(int companyId, int projectId, int measurementId, DateTime date, string? note, List<ProgressRowInput> rows, int? userId);
		Task<(bool ok, string? error)> ConfirmAsync(int companyId, int id);
		Task<(bool ok, string? error)> DeleteAsync(int companyId, int id);
		Task<ProgressSummary> GetProjectProgressAsync(int companyId, int projectId);
	}

	public class ProgressService : IProgressService
	{
		private readonly CrossDbContext _db;
		public ProgressService(CrossDbContext db) { _db = db; }

		private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

		// per-line executed value: manual% → %×BoqValue ; else min(cumQty, boqQty)×unitPrice. Capped at BoqValue.
		private static decimal ExecValue(decimal boqQty, decimal unitPrice, decimal cumQty, decimal? manualPct)
		{
			decimal boqValue = R(boqQty * unitPrice);
			if (manualPct.HasValue)
				return R(Math.Min(Math.Max(manualPct.Value, 0m), 100m) / 100m * boqValue);
			return R(Math.Min(cumQty, boqQty) * unitPrice);
		}
		private static decimal Pct(decimal boqQty, decimal cumQty, decimal? manualPct)
		{
			if (manualPct.HasValue) return Math.Min(Math.Max(manualPct.Value, 0m), 100m);
			return boqQty > 0 ? Math.Min(R(cumQty / boqQty * 100m), 100m) : 0m;
		}

		public Task<List<ProjectProgress>> GetMeasurementsAsync(int companyId, int projectId) =>
			_db.ProjectProgresses.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.ProjectId == projectId)
				.OrderByDescending(p => p.MeasurementNo).ToListAsync();

		// leaf BOQ items = items that are NOT a parent of any other item (section headers roll up from children)
		private async Task<List<BoqItem>> LeafItemsAsync(int companyId, int projectId)
		{
			var all = await _db.BoqItems.AsNoTracking()
				.Where(b => b.CompanyID == companyId && b.ProjectId == projectId)
				.OrderBy(b => b.SortOrder).ThenBy(b => b.ID).ToListAsync();
			var parentIds = all.Where(x => x.ParentId != null).Select(x => x.ParentId!.Value).ToHashSet();
			return all.Where(x => !parentIds.Contains(x.ID)).ToList();
		}

		public async Task<ProgressEditModel> BuildEditModelAsync(int companyId, int projectId, int? measurementId)
		{
			var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			var leaves = await LeafItemsAsync(companyId, projectId);
			bool hasBoq = leaves.Count > 0;

			// current measurement (editing) + previous measurement (the highest No strictly below it, or latest if new)
			ProjectProgress? current = null;
			if (measurementId.HasValue && measurementId.Value > 0)
				current = await _db.ProjectProgresses.AsNoTracking().Include(p => p.Lines)
					.FirstOrDefaultAsync(p => p.ID == measurementId.Value && p.CompanyID == companyId && p.ProjectId == projectId);

			int curNo = current?.MeasurementNo ?? int.MaxValue;
			var prev = await _db.ProjectProgresses.AsNoTracking().Include(p => p.Lines)
				.Where(p => p.CompanyID == companyId && p.ProjectId == projectId && p.MeasurementNo < curNo)
				.OrderByDescending(p => p.MeasurementNo).FirstOrDefaultAsync();

			Dictionary<int, ProjectProgressLine> curByBoq = current?.Lines.Where(l => l.BoqItemId != null).ToDictionary(l => l.BoqItemId!.Value) ?? new();
			Dictionary<int, ProjectProgressLine> prevByBoq = prev?.Lines.Where(l => l.BoqItemId != null).ToDictionary(l => l.BoqItemId!.Value) ?? new();
			var curNull = current?.Lines.FirstOrDefault(l => l.BoqItemId == null);
			var prevNull = prev?.Lines.FirstOrDefault(l => l.BoqItemId == null);

			var model = new ProgressEditModel
			{
				Project = project,
				MeasurementId = current?.ID ?? 0,
				MeasurementNo = current?.MeasurementNo ?? ((await _db.ProjectProgresses.Where(p => p.CompanyID == companyId && p.ProjectId == projectId).Select(p => (int?)p.MeasurementNo).MaxAsync() ?? 0) + 1),
				MeasurementDate = current?.MeasurementDate ?? DateTime.Today,
				Note = current?.Note,
				Status = current?.Status ?? "Draft",
				HasBoq = hasBoq
			};

			var isAr = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			decimal sumExec = 0, sumBoq = 0, sumPrevExec = 0;
			if (hasBoq)
			{
				foreach (var b in leaves)
				{
					decimal boqValue = R(b.Quantity * b.UnitPrice);
					curByBoq.TryGetValue(b.ID, out var cl);
					prevByBoq.TryGetValue(b.ID, out var pl);
					decimal cum = cl?.CumulativeQty ?? 0m; decimal? man = cl?.ManualPercent;
					decimal prevCum = pl?.CumulativeQty ?? 0m; decimal? prevMan = pl?.ManualPercent;
					decimal exec = ExecValue(b.Quantity, b.UnitPrice, cum, man);
					decimal prevExec = ExecValue(b.Quantity, b.UnitPrice, prevCum, prevMan);
					var bdesc = isAr ? b.Description : (string.IsNullOrWhiteSpace(b.DescriptionEn) ? b.Description : b.DescriptionEn);
					model.Lines.Add(new ProgressLineView
					{
						BoqItemId = b.ID, Code = b.Code, Description = bdesc, Unit = b.Unit,
						BoqQty = b.Quantity, UnitPrice = b.UnitPrice, BoqValue = boqValue,
						PrevCumulativeQty = prevCum, PrevExecutedValue = prevExec,
						CumulativeQty = cum, ManualPercent = man,
						Percent = Pct(b.Quantity, cum, man), ExecutedValue = exec, PeriodValue = R(exec - prevExec),
						OverBoq = b.Quantity > 0 && cum > b.Quantity
					});
					sumExec += exec; sumBoq += boqValue; sumPrevExec += prevExec;
				}
			}
			else
			{
				// no BOQ → a single whole-project manual-% line; weight by ContractValue if present
				decimal boqValue = R(project?.ContractValue ?? 0m);
				decimal? man = curNull?.ManualPercent; decimal? prevMan = prevNull?.ManualPercent;
				decimal exec = ExecValue(1m, boqValue, 0m, man ?? 0m);   // boqQty=1, unitPrice=boqValue → manual% of value
				decimal prevExec = ExecValue(1m, boqValue, 0m, prevMan ?? 0m);
				model.Lines.Add(new ProgressLineView
				{
					BoqItemId = null, Description = "المشروع (بلا BOQ)", BoqQty = 0m, UnitPrice = 0m, BoqValue = boqValue,
					PrevCumulativeQty = 0m, PrevExecutedValue = prevExec, CumulativeQty = 0m, ManualPercent = man,
					Percent = man.HasValue ? Math.Min(Math.Max(man.Value, 0m), 100m) : 0m, ExecutedValue = exec, PeriodValue = R(exec - prevExec)
				});
				sumExec += exec; sumBoq += boqValue; sumPrevExec += prevExec;
			}

			model.ExecutedValue = R(sumExec);
			model.PeriodValue = R(sumExec - sumPrevExec);
			model.OverallPercent = sumBoq > 0 ? R(sumExec / sumBoq * 100m) : 0m;
			return model;
		}

		public async Task<(bool ok, string? error, int id)> SaveMeasurementAsync(int companyId, int projectId, int measurementId, DateTime date, string? note, List<ProgressRowInput> rows, int? userId)
		{
			var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.ID == projectId && p.CompanyID == companyId);
			if (project == null) return (false, "المشروع غير موجود", 0);

			ProjectProgress hdr;
			if (measurementId > 0)
			{
				hdr = await _db.ProjectProgresses.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == measurementId && p.CompanyID == companyId && p.ProjectId == projectId)
					?? throw new InvalidOperationException("القياس غير موجود");
				if (hdr.Status != "Draft") return (false, "لا يمكن تعديل قياس مؤكَّد", 0);
				_db.ProjectProgressLines.RemoveRange(hdr.Lines);
				hdr.Lines.Clear();
			}
			else
			{
				// a new measurement's date must not precede the latest CONFIRMED measurement
				var lastConfirmed = await _db.ProjectProgresses.Where(p => p.CompanyID == companyId && p.ProjectId == projectId && p.Status == "Confirmed")
					.OrderByDescending(p => p.MeasurementNo).Select(p => (DateTime?)p.MeasurementDate).FirstOrDefaultAsync();
				if (lastConfirmed.HasValue && date.Date < lastConfirmed.Value.Date) return (false, "تاريخ القياس لا يسبق آخر قياس مؤكَّد", 0);
				int nextNo = (await _db.ProjectProgresses.Where(p => p.CompanyID == companyId && p.ProjectId == projectId).Select(p => (int?)p.MeasurementNo).MaxAsync() ?? 0) + 1;
				hdr = new ProjectProgress { CompanyID = companyId, ProjectId = projectId, MeasurementNo = nextNo, Status = "Draft", CreatedAt = DateTime.UtcNow, CreatedBy = userId };
				_db.ProjectProgresses.Add(hdr);
			}

			hdr.MeasurementDate = date;
			hdr.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
			foreach (var r in rows ?? new())
			{
				bool anyInput = r.CumulativeQty != 0m || r.ManualPercent.HasValue;
				if (!anyInput) continue;   // skip untouched lines
				hdr.Lines.Add(new ProjectProgressLine
				{
					BoqItemId = r.BoqItemId,
					CumulativeQty = r.CumulativeQty < 0 ? 0 : r.CumulativeQty,
					ManualPercent = r.ManualPercent.HasValue ? Math.Min(Math.Max(r.ManualPercent.Value, 0m), 100m) : (decimal?)null
				});
			}

			// snapshot the value-weighted overall % + executed value (recomputed authoritatively)
			var leaves = await LeafItemsAsync(companyId, projectId);
			decimal sumExec = 0, sumBoq = 0;
			if (leaves.Count > 0)
			{
				var byBoq = hdr.Lines.Where(l => l.BoqItemId != null).ToDictionary(l => l.BoqItemId!.Value);
				foreach (var b in leaves)
				{
					decimal boqValue = R(b.Quantity * b.UnitPrice);
					byBoq.TryGetValue(b.ID, out var l);
					sumExec += ExecValue(b.Quantity, b.UnitPrice, l?.CumulativeQty ?? 0m, l?.ManualPercent);
					sumBoq += boqValue;
				}
			}
			else
			{
				decimal boqValue = R(project.ContractValue ?? 0m);
				var l = hdr.Lines.FirstOrDefault(x => x.BoqItemId == null);
				sumExec += ExecValue(1m, boqValue, 0m, l?.ManualPercent ?? 0m);
				sumBoq += boqValue;
			}
			hdr.ExecutedValue = R(sumExec);
			hdr.OverallPercent = sumBoq > 0 ? R(sumExec / sumBoq * 100m) : 0m;

			await _db.SaveChangesAsync();
			return (true, null, hdr.ID);
		}

		public async Task<(bool ok, string? error)> ConfirmAsync(int companyId, int id)
		{
			var hdr = await _db.ProjectProgresses.FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);
			if (hdr == null) return (false, "القياس غير موجود");
			hdr.Status = "Confirmed";
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<(bool ok, string? error)> DeleteAsync(int companyId, int id)
		{
			var hdr = await _db.ProjectProgresses.Include(p => p.Lines).FirstOrDefaultAsync(p => p.ID == id && p.CompanyID == companyId);
			if (hdr == null) return (false, "القياس غير موجود");
			if (hdr.Status != "Draft") return (false, "لا يمكن حذف قياس مؤكَّد");
			_db.ProjectProgressLines.RemoveRange(hdr.Lines);
			_db.ProjectProgresses.Remove(hdr);
			await _db.SaveChangesAsync();
			return (true, null);
		}

		public async Task<ProgressSummary> GetProjectProgressAsync(int companyId, int projectId)
		{
			var list = await _db.ProjectProgresses.AsNoTracking()
				.Where(p => p.CompanyID == companyId && p.ProjectId == projectId)
				.OrderByDescending(p => p.MeasurementNo).ToListAsync();
			var latest = list.FirstOrDefault();
			return new ProgressSummary
			{
				MeasurementCount = list.Count,
				OverallPercent = latest?.OverallPercent ?? 0m,
				ExecutedValue = latest?.ExecutedValue ?? 0m,
				LatestDate = latest?.MeasurementDate
			};
		}
	}
}
