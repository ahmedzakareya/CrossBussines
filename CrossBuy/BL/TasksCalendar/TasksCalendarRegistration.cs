using Microsoft.Extensions.DependencyInjection;

namespace CrossBuy.BL.TasksCalendar
{
	// TAB-5 registration surface for the Tasks/Calendar worker composition.
	//
	// WHY AN EXTENSION METHOD RATHER THAN LINES IN Program.cs. Program.cs is the shared file SHF-01, whose
	// change rule names exactly this pattern: "ONE registration extension method per platform". Every service
	// this composition needs is registered HERE, in a TAB-5-owned file, so Program.cs receives a single bounded
	// call and no future Tasks/Calendar registration has to touch the shared file again.
	//
	// WHAT WAS MISSING, and why it mattered. ITaskOverdueSweepService had an implementation, a dedup contract
	// and tests, but NO registration and therefore no caller. The consequence was not cosmetic: a task could
	// pass its due date, raise no Task.BecameOverdue event, send the assignee no overdue notification, and then
	// — after the grace period — escalate to the manager. The manager learned the task was late before the
	// person doing it ever heard about it. Registering the sweep is what closes that.
	//
	// DELIBERATELY NOT MOVED HERE: ITaskCalendarEventPublisher, ITaskNotificationService and
	// ITaskEscalationService, and the AddHostedService lines. They are already registered in Program.cs and
	// they work. Relocating them would mean rewriting another tab's block inside a shared file for no
	// behavioural gain, which the SHF-01 rule forbids ("never reorder another tab's block"). This method adds
	// what is missing and nothing else.
	public static class TasksCalendarRegistration
	{
		/// Registers the Tasks/Calendar background-worker composition. Idempotent by construction: it uses
		/// TryAdd semantics through ServiceCollectionDescriptorExtensions, so calling it twice — or calling it
		/// when a host has already registered one of these itself — cannot produce a duplicate registration
		/// that would make a scope resolve two sweep instances.
		public static IServiceCollection AddTasksCalendarWorkers(this IServiceCollection services)
		{
			// The overdue sweep. Scoped, like every other service in this module: it is resolved inside the
			// per-company worker scope so the company query filters and the BusinessContext are both bound.
			Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
				.TryAddScoped<ITaskOverdueSweepService, TaskOverdueSweepService>(services);

			return services;
		}
	}
}
