using System.Runtime.CompilerServices;
using CrossBuy.Hubs;
using CrossBuy.Models.Context.Hospital;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CrossBuy.BL.Hospital
{
	// ==========================================================================================
	// HOSPITAL H83 — the waiting-room display is PUSHED, not polled. Whenever a save adds an appointment or changes its
	// status (arrived, called into the room, done, no-show, cancelled) the company's open displays get one SignalR
	// signal, "hospitalQueueChanged", and fetch their content from the ordinary authorized endpoint.
	//
	// WHY AN INTERCEPTOR: an appointment's status is written from at least five places (the desk, the consultation's
	// start and finish, the dental chair, the self check-in kiosk). Hooking each one is a list somebody forgets to extend;
	// watching the save itself cannot miss a writer.
	//
	// The signal carries NO DATA — it only says "look again". Visibility stays with QueueDisplayData, exactly like
	// calendarChanged on NotificationsHub's company group. Sent only once the change is COMMITTED, never before: a save
	// inside a transaction (the consultation's finish runs in one) waits for that transaction's commit and is dropped on
	// rollback — otherwise the screen would fetch the old state on the early signal and get no second one. A failed
	// send is swallowed: a display that misses a push still has its slow safety refresh.
	//
	// A SINGLETON inside CrossDbContext: it injects only IHubContext (itself a singleton) — never a scoped service.
	// ==========================================================================================
	public sealed class HospitalQueueSignalInterceptor : SaveChangesInterceptor, IDbTransactionInterceptor
	{
		public const string Event = "hospitalQueueChanged";

		private readonly IHubContext<NotificationsHub> _hub;
		private readonly ConditionalWeakTable<DbContext, HashSet<int>> _pending = new();

		public HospitalQueueSignalInterceptor(IHubContext<NotificationsHub> hub) { _hub = hub; }

		public override InterceptionResult<int> SavingChanges(DbContextEventData e, InterceptionResult<int> result) { Collect(e.Context); return result; }

		public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData e, InterceptionResult<int> result, CancellationToken ct = default)
		{ Collect(e.Context); return ValueTask.FromResult(result); }

		public override int SavedChanges(SaveChangesCompletedEventData e, int result) { Flush(e.Context); return result; }

		public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData e, int result, CancellationToken ct = default)
		{ Flush(e.Context); return ValueTask.FromResult(result); }

		public override void SaveChangesFailed(DbContextErrorEventData e) { if (e.Context != null) _pending.Remove(e.Context); }

		public override Task SaveChangesFailedAsync(DbContextErrorEventData e, CancellationToken ct = default)
		{ if (e.Context != null) _pending.Remove(e.Context); return Task.CompletedTask; }

		private void Collect(DbContext? db)
		{
			if (db == null) return;
			HashSet<int>? set = null;
			foreach (var entry in db.ChangeTracker.Entries<HospitalAppointment>())
			{
				bool touches = entry.State is EntityState.Added or EntityState.Deleted
					|| (entry.State == EntityState.Modified && (entry.Property(a => a.Status).IsModified || entry.Property(a => a.QueueNumber).IsModified));
				if (!touches || entry.Entity.CompanyId <= 0) continue;
				(set ??= _pending.GetOrCreateValue(db)).Add(entry.Entity.CompanyId);
			}
		}

		// an EF transaction: hold the companies until it ends
		public void TransactionCommitted(System.Data.Common.DbTransaction t, TransactionEndEventData e) => Send(Take(e.Context));
		public Task TransactionCommittedAsync(System.Data.Common.DbTransaction t, TransactionEndEventData e, CancellationToken ct = default) { Send(Take(e.Context)); return Task.CompletedTask; }
		public void TransactionRolledBack(System.Data.Common.DbTransaction t, TransactionEndEventData e) => Take(e.Context);
		public Task TransactionRolledBackAsync(System.Data.Common.DbTransaction t, TransactionEndEventData e, CancellationToken ct = default) { Take(e.Context); return Task.CompletedTask; }

		private HashSet<int>? Take(DbContext? db)
		{
			if (db == null || !_pending.TryGetValue(db, out var set)) return null;
			_pending.Remove(db);
			return set;
		}

		private void Flush(DbContext? db)
		{
			if (db == null || !_pending.TryGetValue(db, out _)) return;
			if (db.Database.CurrentTransaction != null) return;              // TransactionCommitted sends it
			var ambient = System.Transactions.Transaction.Current;
			if (ambient != null)
			{
				var set = Take(db);
				ambient.TransactionCompleted += (_, a) => { if (a.Transaction?.TransactionInformation.Status == System.Transactions.TransactionStatus.Committed) Send(set); };
				return;
			}
			Send(Take(db));
		}

		private void Send(HashSet<int>? set)
		{
			if (set == null) return;
			foreach (var companyId in set)
			{
				// fire-and-forget: a display is a convenience, never a reason to slow or fail the desk's save
				_ = _hub.Clients.Group(NotificationsHub.CompanyGroupFor(companyId)).SendAsync(Event).ContinueWith(_ => { }, TaskScheduler.Default);
			}
		}
	}
}
