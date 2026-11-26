using System.Data;
using CrossBuy.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CrossBuy.BL.Construction;

// Internal admission composition only: never commits, disposes or rolls back the caller's transaction.
// A later document transition must run in that same transaction before its owner commits.
internal static class ConstructionAdmissionUnit
{
    internal static Task<ConstructionReservationResult> RunAsync(CrossDbContext db,
        Func<Task<ConstructionReservationResult>> operation, CancellationToken ct) =>
        RunAsync(db, operation, static x => x.Ok, static code => new ConstructionReservationResult(false, code), ct);

    internal static async Task<T> RunAsync<T>(CrossDbContext db, Func<Task<T>> operation,
        Func<T, bool> accepted, Func<string, T> refusal, CancellationToken ct)
    {
        var tx = db.Database.CurrentTransaction;
        if (tx == null || tx.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable || !tx.SupportsSavepoints)
            return refusal("SerializableAdmissionTransactionRequired");
        if (db.ChangeTracker.HasChanges()) return refusal("CleanUnitOfWorkRequired");
        var before = db.ChangeTracker.Entries().Select(e => new
        {
            Entry = e, State = e.State, Current = e.CurrentValues.Clone(), Original = e.OriginalValues.Clone()
        }).ToArray();
        var retained = before.Select(x => x.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var point = "R7A" + Guid.NewGuid().ToString("N")[..24]; // SQL Server savepoint names are at most 32 characters.
        await tx.CreateSavepointAsync(point, ct);
        async Task Undo()
        {
            try { await tx.RollbackToSavepointAsync(point, CancellationToken.None); }
            finally
            {
                foreach (var entry in db.ChangeTracker.Entries().ToArray())
                    if (!retained.Contains(entry.Entity)) entry.State = EntityState.Detached;
                foreach (var item in before)
                {
                    item.Entry.State = EntityState.Unchanged;
                    item.Entry.CurrentValues.SetValues(item.Current);
                    item.Entry.OriginalValues.SetValues(item.Original);
                    item.Entry.State = item.State;
                }
            }
        }
        try
        {
            var result = await operation();
            if (!accepted(result)) await Undo();
            await tx.ReleaseSavepointAsync(point, CancellationToken.None);
            return result;
        }
        catch (Exception failure)
        {
            try { await Undo(); }
            catch (Exception rollback) { throw new AggregateException("Admission failed and the owner must roll back its transaction.", failure, rollback); }
            throw;
        }
    }
}
