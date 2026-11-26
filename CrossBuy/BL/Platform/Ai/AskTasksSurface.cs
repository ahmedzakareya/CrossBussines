using System.Globalization;
using CrossBuy.BL;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CrossBuy.BL.Platform.Ai
{
	/// <summary>
	/// Read-only task data for the assistant. The query enters through the same list authorizer as
	/// /Tasks, so personal/team/company scope, linked-record visibility, and source read entrances
	/// remain identical to the visible task list.
	/// </summary>
	public sealed class AskTasksSurface : AskSurface
	{
		public const string SurfaceCode = "tasks.query";

		private readonly CrossDbContext _db;
		private readonly ITaskReadAuthorizer _readAuthorizer;

		public AskTasksSurface(CrossDbContext db, ITaskReadAuthorizer readAuthorizer)
		{
			_db = db ?? throw new ArgumentNullException(nameof(db));
			_readAuthorizer = readAuthorizer ?? throw new ArgumentNullException(nameof(readAuthorizer));
		}

		public override string Code => SurfaceCode;
		public override string Module => EntityRegistry.ScopeTasks;
		public override string Action => TasksActions.Read;
		public override string? NameKey => "__name";

		public override AskText Description => AskText.Of(
			"المهام التي يسمح لك بعرضها: قائمة أو عدد حسب الحالة أو الأولوية أو التصنيف أو تاريخ الاستحقاق",
			"Tasks you are authorized to see: list or count by status, priority, category, or due date",
			"Tâches accessibles : liste ou décompte par statut, priorité, catégorie ou échéance");

		public override AskText Rules => AskText.Of(
			"قراءة فقط · النتائج تمر عبر صلاحيات قائمة المهام نفسها، بما فيها نطاق المستخدم والفريق وصلاحية السجل المرتبط · " +
			"التأخير يُشتق من تاريخ الاستحقاق والحالة الحالية، ولا يُعرض للمهمة المكتملة · لا تُنشأ المهام ولا تُعدّل من المساعد.",
			"Read-only · results use the same task-list authorization, including personal/team scope and linked-record visibility · " +
			"overdue is derived from the due date and current status; completed tasks are not overdue · the assistant cannot create or change tasks.",
			"Lecture seule · mêmes autorisations que la liste des tâches, y compris le périmètre personnel/de l'équipe et la visibilité du dossier lié · " +
			"le retard est déduit de l'échéance et du statut actuel; une tâche terminée n'est pas en retard.");

		public override IReadOnlyList<AskColumn> Columns { get; } = new[]
		{
			new AskColumn { Name = "title", Kind = AskColumnKind.Dimension,
				Label = AskText.Of("المهمة", "Task", "Tâche"),
				Synonyms = new[] { "مهمة", "المهام", "عنوان المهمة", "اسم المهمة", "task", "tasks", "task title", "work item" } },
			new AskColumn { Name = "status", Kind = AskColumnKind.Dimension,
				Label = AskText.Of("الحالة", "Status", "Statut"),
				Synonyms = new[] { "حالة", "الحالة", "جديدة", "قيد التنفيذ", "مكتملة", "متأخرة", "مفتوحة", "منتهية",
					"status", "new", "in progress", "done", "completed", "overdue", "open", "finished" } },
			new AskColumn { Name = "priority", Kind = AskColumnKind.Dimension,
				Label = AskText.Of("الأولوية", "Priority", "Priorité"),
				Synonyms = new[] { "أولوية", "الأولوية", "عاجلة", "عالي", "عادية", "منخفضة", "priority", "urgent", "high", "normal", "low" } },
			new AskColumn { Name = "category", Kind = AskColumnKind.Dimension,
				Label = AskText.Of("التصنيف", "Category", "Catégorie"),
				Synonyms = new[] { "التصنيف", "الفئة", "نوع المهمة", "category", "type" } },
			new AskColumn { Name = "date", Kind = AskColumnKind.Dimension,
				Label = AskText.Of("تاريخ الاستحقاق", "Due date", "Échéance"),
				Synonyms = new[] { "الاستحقاق", "تاريخ الاستحقاق", "موعد التسليم", "تاريخ التسليم", "due", "due date", "deadline" } },
			new AskColumn { Name = "taskcount", Kind = AskColumnKind.Measure,
				Label = AskText.Of("عدد المهام", "Task count", "Nombre de tâches"),
				Synonyms = new[] { "عدد المهام", "كام مهمة", "كم مهمة", "عدد", "العدد", "task count", "number of tasks", "how many tasks" } },
		};

		public override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> RowsAsync(
			AskScope scope, CancellationToken ct = default)
		{
			var authorization = await _readAuthorizer.ForListAsync(ct);
			// An absent list grant or a mismatched server-resolved company is a refusal, not an empty
			// unscoped query. ApplyAsync is the canonical task breadth + linked-source filter.
			if (authorization == null || authorization.Context.CompanyId != scope.CompanyId)
				return Array.Empty<IReadOnlyDictionary<string, object?>>();

			var authorized = await _readAuthorizer.ApplyAsync(_db.TaskItems.AsNoTracking(), authorization, ct);
			var tasks = await authorized
				.Where(t => t.CompanyId == scope.CompanyId)
				.OrderByDescending(t => t.DueDate)
				.ThenBy(t => t.ID)
				.Select(t => new { t.ID, t.Title, t.TitleEn, t.Status, t.Priority, t.Category, t.DueDate })
				.ToListAsync(ct);

			var arabic = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar";
			var now = DateTime.UtcNow;
			return tasks.Select(task =>
			{
				var title = AskItems.Readable(arabic ? task.Title : task.TitleEn,
					arabic ? task.TitleEn : task.Title) ?? "#" + task.ID.ToString(CultureInfo.InvariantCulture);
				var status = TaskStatuses.IsOverdue(task.DueDate, task.Status, now)
					? (arabic ? "متأخرة" : "Overdue")
					: StatusLabel(task.Status, arabic);
				return (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
				{
					["title"] = title,
					["status"] = status,
					["priority"] = PriorityLabel(task.Priority, arabic),
					["category"] = task.Category,
					["date"] = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					["taskcount"] = 1m,
					["__name"] = title,
					[SearchNameKey] = ((task.Title ?? "") + " " + (task.TitleEn ?? "")).Trim(),
					[RecordHrefKey] = "/Tasks/Detail/" + task.ID.ToString(CultureInfo.InvariantCulture),
				};
			}).ToList();
		}

		private static string StatusLabel(string? value, bool arabic) => value switch
		{
			TaskStatuses.New => arabic ? "جديدة" : "New",
			TaskStatuses.InProgress => arabic ? "قيد التنفيذ" : "In progress",
			TaskStatuses.Done => arabic ? "مكتملة" : "Done",
			_ => value ?? (arabic ? "(غير محددة)" : "(not recorded)"),
		};

		private static string PriorityLabel(string? value, bool arabic) => value?.Trim().ToLowerInvariant() switch
		{
			"urgent" => arabic ? "عاجلة" : "Urgent",
			"high" => arabic ? "عالية" : "High",
			"normal" => arabic ? "عادية" : "Normal",
			"low" => arabic ? "منخفضة" : "Low",
			_ => value ?? (arabic ? "(غير محددة)" : "(not recorded)"),
		};
	}
}
