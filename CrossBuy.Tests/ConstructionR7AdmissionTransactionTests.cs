using System.Data;
using System.Reflection;
using CrossBuy.BL.Construction;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7AdmissionTransactionTests
{
    // No public production hook or assembly-wide friend access is introduced for these internal composition methods.
    internal static Task<ConstructionReservationResult> Invoke(object service, string method, params object[] args) =>
        (Task<ConstructionReservationResult>)service.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, args)!;

    internal static Task<ConstructionReservationResult> Reserve(ConstructionCommitmentReservationService service,
        ConstructionR7ReservationTests.Setup s, bool first, Guid id) => Invoke(service, "ReserveInTransactionAsync",
        ConstructionR7OrderAllocationTests.Actor(), s.Project, first ? s.FirstAllocation : s.SecondAllocation,
        first ? s.FirstVersion : s.SecondVersion, id, "Composed reservation", CancellationToken.None);

    internal static Task<ConstructionReservationResult> Consume(ConstructionBudgetExceptionService service,
        ConstructionR7ReservationTests.Setup s, int exception, Guid id) => Invoke(service, "ConsumeInTransactionAsync",
        ConstructionR7BudgetExceptionTests.Approver, s.Project, exception, id, "Composed exception", CancellationToken.None);

    [Fact]
    public async Task Borrowed_reservation_never_commits_its_owner_and_replay_never_grants_admission()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var service = ConstructionR7ReservationTests.Service(f.Db); var id = Guid.NewGuid();
        Assert.Equal("SerializableAdmissionTransactionRequired", (await Reserve(service, s, true, id)).Code);
        await using (var owner = await f.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.True((await Reserve(service, s, true, id)).GrantsCurrentAdmission);
            var replay = await Reserve(service, s, true, id); Assert.Equal("AlreadyReserved", replay.Code); Assert.False(replay.GrantsCurrentAdmission);
            Assert.Same(owner, f.Db.Database.CurrentTransaction); await owner.RollbackAsync();
        }
        await using var verify = f.NewContext(); Assert.Empty(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Budget_refusal_keeps_the_owners_saved_edit_and_existing_reservation()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        var service = ConstructionR7ReservationTests.Service(f.Db);
        Assert.True((await service.ReserveAsync(ConstructionR7OrderAllocationTests.Actor(), s.Project, s.FirstAllocation, s.FirstVersion, Guid.NewGuid(), "Existing commitment")).Ok);
        await using (var owner = await f.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            var project = await f.Db.Projects.SingleAsync(); project.Name = "Owner work survives refusal"; await f.Db.SaveChangesAsync();
            var denied = await Reserve(service, s, false, Guid.NewGuid()); Assert.Equal("BudgetHardBlock", denied.Code);
            Assert.Same(owner, f.Db.Database.CurrentTransaction); Assert.Equal(EntityState.Unchanged, f.Db.Entry(project).State);
            await owner.CommitAsync();
        }
        await using var verify = f.NewContext(); Assert.Equal("Owner work survives refusal", (await verify.Projects.AsNoTracking().SingleAsync()).Name);
        Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Borrowed_exception_consumption_is_rolled_back_with_the_owners_later_failure()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7BudgetExceptionTests.Arrange(f.Db);
        var service = ConstructionR7BudgetExceptionTests.Service(f.Db);
        Assert.True((await service.ApproveAsync(ConstructionR7BudgetExceptionTests.Approver, s.Setup.Project, s.Request.Id!.Value, s.Request.Version!, Guid.NewGuid(), "Independent decision")).Ok);
        await using (var owner = await f.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable))
        {
            Assert.True((await Consume(service, s.Setup, s.Request.Id.Value, Guid.NewGuid())).GrantsCurrentAdmission);
            Assert.Same(owner, f.Db.Database.CurrentTransaction); await owner.RollbackAsync();
        }
        await using var verify = f.NewContext(); Assert.Single(await verify.Set<ConstructionCommitmentReservation>().AsNoTracking().ToListAsync());
        Assert.Empty(await verify.Set<ConstructionBudgetExceptionConsumption>().AsNoTracking().ToListAsync());
        Assert.Single(await verify.Set<ConstructionBudgetExceptionDecision>().AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Dirty_or_unauthorized_borrowers_cannot_flush_or_discard_pending_owner_changes()
    {
        using var f = new ConstructionTestFixture(); var s = await ConstructionR7ReservationTests.Arrange(f.Db);
        await using var owner = await f.Db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var project = await f.Db.Projects.SingleAsync(); project.Name = "Unsaved owner work";
        Assert.Equal("CleanUnitOfWorkRequired", (await Reserve(ConstructionR7ReservationTests.Service(f.Db), s, true, Guid.NewGuid())).Code);
        Assert.Equal("AccessDenied", (await Reserve(ConstructionR7ReservationTests.Service(f.Db, false), s, true, Guid.NewGuid())).Code);
        Assert.True(f.Db.ChangeTracker.HasChanges()); Assert.Equal("Unsaved owner work", project.Name);
        await owner.RollbackAsync();
    }
}
