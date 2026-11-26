namespace CrossBuy.BL.Platform.Ai
{
	/// <summary>Natural-language entry point for the permission-scoped task surface.</summary>
	public sealed class AskTasksQueryHandler : AskSurfaceQueryHandler
	{
		public const string IntentId = "tasks.query";

		public AskTasksQueryHandler(AskTasksSurface surface, IAskQueryComposer composer)
			: base(surface, composer) { }

		public override string Intent => IntentId;
		public override AskText Description => AskText.Of(
			"قائمة المهام وأعدادها حسب الحالة والأولوية والتصنيف وتاريخ الاستحقاق",
			"List or count tasks by status, priority, category, or due date",
			"Lister ou compter les tâches par statut, priorité, catégorie ou échéance");

		public override AskText Example => AskText.Of(
			"ما المهام المتأخرة؟", "Which tasks are overdue?", "Quelles tâches sont en retard ?");

		public override IReadOnlyCollection<string> Keywords { get; } = new[]
		{
			"المهام", "المهام المفتوحة", "المهام المتأخرة", "المهام المستحقة", "مهامي", "مهماتي",
			"عدد المهام", "المهام حسب الحالة", "المهام حسب الأولوية", "مهمة عاجلة",
			"tasks", "my tasks", "open tasks", "overdue tasks", "tasks due", "task list",
			"task count", "tasks by status", "urgent tasks", "what tasks are due",
		};

		protected override IReadOnlyList<string> Sources { get; } = new[] { "TaskItems" };

		protected override AskQuery DefaultQuery() => new()
		{
			Surface = AskTasksSurface.SurfaceCode,
			GroupBy = new[] { "status" },
			Measures = new[] { new AskMeasure { Column = "taskcount", Agg = AskAgg.Sum } },
			OrderBy = "taskcount",
			Descending = true,
			Limit = 10,
		};
	}
}
