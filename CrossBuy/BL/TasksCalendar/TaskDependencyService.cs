using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.TasksCalendar
{
	// ==========================================================================================
	// TASK DEPENDENCIES
	//
	// The whole difficulty of this feature is one thing: A depends on B depends on C depends on A.
	// A dependency graph with a cycle is not "slightly wrong" — every task in the cycle blocks every
	// other, so nothing in it can ever start, and no UI can explain why. So the cycle check is the
	// feature, and the rest is bookkeeping.
	//
	// The check runs BEFORE the edge is written, walking the edges that already exist. It is a plain
	// breadth-first reachability test: if the SUCCESSOR can already reach the PREDECESSOR, adding
	// predecessor -> successor closes a loop.
	//
	// Edge direction, stated once: PREDECESSOR happens first; SUCCESSOR is the one blocked.
	// ==========================================================================================

	public sealed class TaskDependencyView
	{
		public required int Id { get; init; }
		public required int PredecessorTaskId { get; init; }
		public required string PredecessorTitle { get; init; }
		public required string PredecessorStatus { get; init; }
		public required int SuccessorTaskId { get; init; }
		public required string SuccessorTitle { get; init; }
		public required string Kind { get; init; }
		public required int LagDays { get; init; }

		/// True when this edge is currently holding the successor back.
		public required bool IsBlocking { get; init; }
	}

	public sealed class TaskBlockingState
	{
		public required int TaskId { get; init; }
		public required bool IsBlocked { get; init; }
		public required IReadOnlyList<int> BlockedByTaskIds { get; init; }
		public required IReadOnlyList<string> BlockedByTitles { get; init; }
	}

	public interface ITaskDependencyService
	{
		Task<(bool ok, string? error, int id)> AddAsync(
			int companyId, int predecessorTaskId, int successorTaskId, string kind, int lagDays,
			int? actorEmployeeId, CancellationToken ct = default);

		Task<(bool ok, string? error)> RemoveAsync(int companyId, int id, CancellationToken ct = default);

		Task<List<TaskDependencyView>> ForTaskAsync(int companyId, int taskId, CancellationToken ct = default);

		Task<TaskBlockingState> BlockingStateAsync(int companyId, int taskId, CancellationToken ct = default);

		/// Blocking state for many tasks in ONE round trip — the board needs it for every card, and a
		/// per-card query would be a query per card.
		Task<Dictionary<int, TaskBlockingState>> BlockingStateManyAsync(
			int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default);

		Task<(bool wouldCycle, IReadOnlyList<int> path)> WouldCreateCycleAsync(
			int companyId, int predecessorTaskId, int successorTaskId, CancellationToken ct = default);

		/// The two tasks an edge joins — an authorisation check has to know both, because removing an
		/// edge changes what BOTH of them wait for.
		Task<(int predecessorTaskId, int successorTaskId)?> EndpointsAsync(
			int companyId, int id, CancellationToken ct = default);
	}

	public sealed class TaskDependencyService : ITaskDependencyService
	{
		private readonly CrossDbContext _db;
		public TaskDependencyService(CrossDbContext db) { _db = db; }

		public async Task<(bool ok, string? error, int id)> AddAsync(
			int companyId, int predecessorTaskId, int successorTaskId, string kind, int lagDays,
			int? actorEmployeeId, CancellationToken ct = default)
		{
			if (companyId <= 0) return (false, "الشركة غير محددة (unresolved company)", 0);

			// A task cannot wait for itself. Caught first because it is the one case the graph walk
			// below would report as a cycle with a confusing single-element path.
			if (predecessorTaskId == successorTaskId)
				return (false, "المهمة لا تعتمد على نفسها (a task cannot depend on itself)", 0);

			if (!TaskDependencyKinds.IsKnown(kind))
				return (false, "نوع اعتمادية غير معروف (unknown dependency kind)", 0);

			// BOTH tasks must exist in THIS company. Read as a pair so a cross-company id cannot be
			// smuggled in as one half of the edge.
			var tasks = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && (t.ID == predecessorTaskId || t.ID == successorTaskId))
				.Select(t => t.ID).ToListAsync(ct);
			if (tasks.Count != 2)
				return (false, "إحدى المهمتين غير موجودة في هذه الشركة (a task was not found in this company)", 0);

			if (await _db.TaskDependencies.AnyAsync(
					d => d.CompanyId == companyId && d.PredecessorTaskId == predecessorTaskId
					     && d.SuccessorTaskId == successorTaskId, ct))
				return (false, "هذه الاعتمادية موجودة بالفعل (this dependency already exists)", 0);

			var (wouldCycle, path) = await WouldCreateCycleAsync(companyId, predecessorTaskId, successorTaskId, ct);
			if (wouldCycle)
				return (false,
					"هذه الاعتمادية تُنشئ حلقة مغلقة: " + string.Join(" → ", path) +
					$" (this dependency would create a cycle: {string.Join(" -> ", path)})", 0);

			var edge = new TaskDependency
			{
				CompanyId = companyId,
				PredecessorTaskId = predecessorTaskId,
				SuccessorTaskId = successorTaskId,
				Kind = kind,
				LagDays = lagDays,
				CreatedAt = TaskCalendarTime.UtcNow(),
				CreatedByEmployeeId = actorEmployeeId
			};
			_db.TaskDependencies.Add(edge);
			await _db.SaveChangesAsync(ct);
			return (true, null, edge.ID);
		}

		public async Task<(bool ok, string? error)> RemoveAsync(int companyId, int id, CancellationToken ct = default)
		{
			var edge = await _db.TaskDependencies
				.FirstOrDefaultAsync(d => d.ID == id && d.CompanyId == companyId, ct);
			if (edge == null) return (false, "الاعتمادية غير موجودة (dependency not found)");

			_db.TaskDependencies.Remove(edge);
			await _db.SaveChangesAsync(ct);
			return (true, null);
		}

		/// Would adding predecessor -> successor close a loop?
		///
		/// It would exactly when the SUCCESSOR can already reach the PREDECESSOR by following existing
		/// edges forward. Breadth-first, with a visited set, so a graph that already contains a cycle
		/// (from data written before this service existed) terminates instead of hanging.
		public async Task<(bool wouldCycle, IReadOnlyList<int> path)> WouldCreateCycleAsync(
			int companyId, int predecessorTaskId, int successorTaskId, CancellationToken ct = default)
		{
			if (predecessorTaskId == successorTaskId)
				return (true, new[] { predecessorTaskId, successorTaskId });

			var edges = await _db.TaskDependencies.AsNoTracking()
				.Where(d => d.CompanyId == companyId)
				.Select(d => new { d.PredecessorTaskId, d.SuccessorTaskId })
				.ToListAsync(ct);

			var forward = edges.GroupBy(e => e.PredecessorTaskId)
							   .ToDictionary(g => g.Key, g => g.Select(x => x.SuccessorTaskId).ToList());

			// Walk forward from the successor. Reaching the predecessor means the new edge closes a loop.
			var cameFrom = new Dictionary<int, int>();
			var visited = new HashSet<int> { successorTaskId };
			var queue = new Queue<int>();
			queue.Enqueue(successorTaskId);

			while (queue.Count > 0)
			{
				var current = queue.Dequeue();
				if (!forward.TryGetValue(current, out var nexts)) continue;

				foreach (var next in nexts)
				{
					if (!visited.Add(next)) continue;
					cameFrom[next] = current;

					if (next == predecessorTaskId)
					{
						// Rebuild the loop for the message: the user needs to see WHICH chain closes,
						// not just that one does.
						var path = new List<int> { predecessorTaskId };
						var node = predecessorTaskId;
						while (cameFrom.TryGetValue(node, out var prev)) { path.Add(prev); node = prev; }
						path.Reverse();
						path.Add(successorTaskId);   // the edge being attempted closes back here
						return (true, path);
					}
					queue.Enqueue(next);
				}
			}

			return (false, Array.Empty<int>());
		}

		public async Task<(int predecessorTaskId, int successorTaskId)?> EndpointsAsync(
			int companyId, int id, CancellationToken ct = default)
		{
			if (companyId <= 0) return null;
			var e = await _db.TaskDependencies.AsNoTracking()
				.Where(x => x.CompanyId == companyId && x.ID == id)
				.Select(x => new { x.PredecessorTaskId, x.SuccessorTaskId }).FirstOrDefaultAsync(ct);
			return e == null ? null : (e.PredecessorTaskId, e.SuccessorTaskId);
		}

		public async Task<List<TaskDependencyView>> ForTaskAsync(int companyId, int taskId, CancellationToken ct = default)
		{
			var edges = await _db.TaskDependencies.AsNoTracking()
				.Where(d => d.CompanyId == companyId && (d.PredecessorTaskId == taskId || d.SuccessorTaskId == taskId))
				.ToListAsync(ct);
			if (edges.Count == 0) return new();

			var ids = edges.Select(e => e.PredecessorTaskId).Concat(edges.Select(e => e.SuccessorTaskId))
						   .Distinct().ToList();
			var tasks = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && ids.Contains(t.ID))
				.Select(t => new { t.ID, t.Title, t.Status })
				.ToDictionaryAsync(t => t.ID, ct);

			return edges.Select(e =>
			{
				tasks.TryGetValue(e.PredecessorTaskId, out var p);
				tasks.TryGetValue(e.SuccessorTaskId, out var s);
				return new TaskDependencyView
				{
					Id = e.ID,
					PredecessorTaskId = e.PredecessorTaskId,
					PredecessorTitle = p?.Title ?? $"#{e.PredecessorTaskId}",
					PredecessorStatus = p?.Status ?? "",
					SuccessorTaskId = e.SuccessorTaskId,
					SuccessorTitle = s?.Title ?? $"#{e.SuccessorTaskId}",
					Kind = e.Kind,
					LagDays = e.LagDays,
					// Only Finish-to-Start gates today, and only while the predecessor is not Done.
					IsBlocking = e.Kind == TaskDependencyKinds.FinishToStart
								 && p != null && !string.Equals(p.Status, "Done", StringComparison.Ordinal)
				};
			}).ToList();
		}

		public async Task<TaskBlockingState> BlockingStateAsync(int companyId, int taskId, CancellationToken ct = default)
		{
			var many = await BlockingStateManyAsync(companyId, new[] { taskId }, ct);
			return many.TryGetValue(taskId, out var s)
				? s
				: new TaskBlockingState
				{
					TaskId = taskId, IsBlocked = false,
					BlockedByTaskIds = Array.Empty<int>(), BlockedByTitles = Array.Empty<string>()
				};
		}

		public async Task<Dictionary<int, TaskBlockingState>> BlockingStateManyAsync(
			int companyId, IReadOnlyCollection<int> taskIds, CancellationToken ct = default)
		{
			var result = new Dictionary<int, TaskBlockingState>();
			if (companyId <= 0 || taskIds.Count == 0) return result;

			var edges = await _db.TaskDependencies.AsNoTracking()
				.Where(d => d.CompanyId == companyId
							&& d.Kind == TaskDependencyKinds.FinishToStart
							&& taskIds.Contains(d.SuccessorTaskId))
				.Select(d => new { d.SuccessorTaskId, d.PredecessorTaskId })
				.ToListAsync(ct);

			var predecessorIds = edges.Select(e => e.PredecessorTaskId).Distinct().ToList();
			var open = await _db.TaskItems.AsNoTracking()
				.Where(t => t.CompanyId == companyId && predecessorIds.Contains(t.ID) && t.Status != "Done")
				.Select(t => new { t.ID, t.Title })
				.ToDictionaryAsync(t => t.ID, t => t.Title, ct);

			foreach (var id in taskIds.Distinct())
			{
				var blockers = edges.Where(e => e.SuccessorTaskId == id && open.ContainsKey(e.PredecessorTaskId))
									.Select(e => e.PredecessorTaskId).Distinct().ToList();
				result[id] = new TaskBlockingState
				{
					TaskId = id,
					IsBlocked = blockers.Count > 0,
					BlockedByTaskIds = blockers,
					BlockedByTitles = blockers.Select(b => open[b]).ToList()
				};
			}
			return result;
		}
	}
}
