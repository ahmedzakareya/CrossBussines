using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL
{
	// HM-1-أ (ب-3, own-or-join): a transaction handle that OWNS a new transaction only when the DbContext has none;
	// otherwise it JOINS the caller's ambient transaction and its Commit/Rollback/Dispose become no-ops (the owner
	// decides). This lets every existing tx site keep its exact code (BeginOrJoin → CommitAsync → RollbackAsync) while
	// becoming safe to nest under an outer sale/purchase transaction. A site with NO ambient transaction behaves
	// EXACTLY as before (opens, commits, disposes its own). Signature-compatible drop-in for BeginTransactionAsync().
	public sealed class ScopedTx : IAsyncDisposable
	{
		private readonly IDbContextTransaction? _tx;   // null ⇒ joined an existing ambient transaction
		private readonly DbContext _ctx;
		private bool _settled;                          // committed or rolled back explicitly
		private ScopedTx(DbContext ctx, IDbContextTransaction? tx) { _ctx = ctx; _tx = tx; }

		public static async Task<ScopedTx> BeginOrJoinAsync(DbContext ctx)
			=> new ScopedTx(ctx, ctx.Database.CurrentTransaction == null ? await ctx.Database.BeginTransactionAsync() : null);

		public bool Owns => _tx != null;
		public async Task CommitAsync() { if (_tx != null) { await _tx.CommitAsync(); _settled = true; } }

		// OWNER ONLY. A DB rollback does NOT reset EF's ChangeTracker: entities that already SaveChanged stay tracked
		// as Unchanged with generated IDs for rows that no longer exist, and any still-Added entity would be re-inserted
		// by the next SaveChanges in this (request-Scoped) DbContext. So after rolling the DB back we CLEAR the tracker —
		// no phantom row is ever re-written, and no stale identity-map entry is returned to a later query in the same
		// request. The joiner never clears; the outer owner does it once for the whole nested unit of work.
		public async Task RollbackAsync()
		{
			if (_tx != null)
			{
				await _tx.RollbackAsync();
				_ctx.ChangeTracker.Clear();
				_settled = true;
			}
		}

		// Defensive net for the EXCEPTION path (method threw before Commit/Rollback): undo the DB and clear the tracker
		// so a caught-higher-up request cannot re-write phantoms. No-op when joined or already settled.
		public async ValueTask DisposeAsync()
		{
			if (_tx != null)
			{
				if (!_settled)
				{
					try { await _tx.RollbackAsync(); } catch { /* connection already gone — DB rolls back on its own */ }
					_ctx.ChangeTracker.Clear();
				}
				await _tx.DisposeAsync();
			}
		}
	}
}
