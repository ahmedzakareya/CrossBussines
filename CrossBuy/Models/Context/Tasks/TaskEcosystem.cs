using Microsoft.EntityFrameworkCore;

namespace CrossBuy.Models.Context.Tasks
{
	// ==========================================================================================
	// TASK ECOSYSTEM — data foundation (checklist, dependencies, templates)
	//
	// All NEW tables. No existing Tasks table is altered, so a database on which the C-slice script
	// has not yet run keeps every existing Tasks screen working exactly as before — only the new code
	// paths fail, and they fail loudly. Same discipline as the construction C1 satellites.
	//
	// Every table carries CompanyId. There is no company default anywhere: an unresolved company reads
	// nothing and writes nothing.
	// ==========================================================================================

	/// بند قائمة التحقق — one checklist line on a task.
	///
	/// Deliberately does NOT drive TaskItem.ProgressPct. Progress is a value a human sets and TM-1 has
	/// always treated it that way; silently recomputing it from checklist state would change the meaning
	/// of an existing column. Checklist completion is exposed as its own derived figure instead.
	public class TaskChecklistItem
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }
		public int TaskId { get; set; }

		public string Title { get; set; } = "";
		// English twin of the column above. Nullable and never required: read it through
		// DisplayName.Or(<En>, <Ar>) so a row that never got one still shows a name.
		public string? TitleEn { get; set; }
		public bool IsDone { get; set; }
		public DateTime? DoneAt { get; set; }
		public int? DoneByEmployeeId { get; set; }

		public int SortOrder { get; set; }
		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// نوع الاعتمادية. Only Finish-to-Start is enforced today; the others are recorded so a schedule
	/// view can render them without a schema change, and are documented as not yet gating.
	public static class TaskDependencyKinds
	{
		public const string FinishToStart = "FinishToStart";
		public const string StartToStart = "StartToStart";
		public const string FinishToFinish = "FinishToFinish";
		public const string StartToFinish = "StartToFinish";

		public static readonly IReadOnlyList<string> All =
			new[] { FinishToStart, StartToStart, FinishToFinish, StartToFinish };

		public static bool IsKnown(string? k) => k != null && All.Contains(k, StringComparer.Ordinal);
	}

	/// اعتمادية بين مهمتين — "Successor depends on Predecessor".
	///
	/// The edge direction is the thing people get wrong, so it is stated once here and never restated:
	/// PREDECESSOR must happen first; SUCCESSOR is the one that gets blocked.
	public class TaskDependency
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }

		public int PredecessorTaskId { get; set; }
		public int SuccessorTaskId { get; set; }

		public string Kind { get; set; } = TaskDependencyKinds.FinishToStart;

		/// Optional lag in days. Negative = lead. Recorded now so a schedule view does not need a
		/// migration later; it does not gate anything yet.
		public int LagDays { get; set; }

		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
	}

	/// قالب مهام — a reusable set of tasks with relative due dates and their own dependency edges.
	public class TaskTemplate
	{
		public int ID { get; set; }
		public int CompanyId { get; set; }

		public string Name { get; set; } = "";
		public string? NameEn { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }   // English twin; falls back to Description

		public bool IsActive { get; set; } = true;

		public DateTime CreatedAt { get; set; }
		public int? CreatedByEmployeeId { get; set; }
		public DateTime? UpdatedAt { get; set; }
		public int? UpdatedByEmployeeId { get; set; }

		public List<TaskTemplateItem> Items { get; set; } = new();
	}

	/// One task inside a template.
	public class TaskTemplateItem
	{
		public int ID { get; set; }
		public int TaskTemplateId { get; set; }
		public int CompanyId { get; set; }

		public string Title { get; set; } = "";
		public string? TitleEn { get; set; }
		public string? Description { get; set; }
		public string? DescriptionEn { get; set; }   // English twin; falls back to Description
		public string Priority { get; set; } = "Normal";
		public decimal? EstimatedHours { get; set; }

		/// Due date = anchor date + this many days. The template stores a RELATIVE offset because an
		/// absolute date in a reusable template is wrong the second time it is used.
		public int DueOffsetDays { get; set; }

		/// Optional default assignee. Null = the applier decides.
		public int? DefaultAssigneeEmployeeId { get; set; }

		public int SortOrder { get; set; }

		/// Dependency inside the template, by SortOrder of another item in the SAME template.
		/// Null = no predecessor. Stored as a sort order rather than an id so a template can be copied
		/// without rewriting ids.
		public int? PredecessorSortOrder { get; set; }

		/// Checklist lines to create with the task, newline-separated. A child table would be a third
		/// level of nesting for data that is never queried on its own.
		public string? ChecklistLines { get; set; }

		public TaskTemplate? Template { get; set; }
	}

	// ==========================================================================================
	// Model configuration. Kept here so the footprint in the shared CrossDbContext stays at one call.
	// ==========================================================================================
	public static class TaskEcosystemModel
	{
		public static void Configure(ModelBuilder b)
		{
			b.Entity<TaskChecklistItem>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.TaskId, x.SortOrder });
			});

			b.Entity<TaskDependency>(e =>
			{
				// One edge per (predecessor, successor) pair. A second identical edge is not a second
				// dependency, it is a duplicate — and it would double-count in any blocked calculation.
				e.HasIndex(x => new { x.CompanyId, x.PredecessorTaskId, x.SuccessorTaskId }).IsUnique();
				e.HasIndex(x => new { x.CompanyId, x.SuccessorTaskId });
			});

			b.Entity<TaskTemplate>(e =>
			{
				e.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique();
				e.HasMany(x => x.Items)
				 .WithOne(i => i.Template!)
				 .HasForeignKey(i => i.TaskTemplateId)
				 .OnDelete(DeleteBehavior.Cascade);
			});

			b.Entity<TaskTemplateItem>(e =>
			{
				e.HasIndex(x => new { x.TaskTemplateId, x.SortOrder });
			});
		}
	}
}
