using CrossBuy.BL;
using CrossBuy.BL.Construction;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Construction;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CrossBuy.Tests;

public sealed class ConstructionR7ConsolidationTests
{
    [Fact]
    public async Task Current_state_is_written_on_the_stable_boq_identity_not_on_the_history_satellite()
    {
        using var f = new ConstructionTestFixture(); var project = await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1, project.ID,
            new() { new() { Code="1", Description="Work", Quantity=10, UnitPrice=2 } },7,"Initial scope")).ok);
        var id=(await f.ActiveLineIdsByCodeAsync(project.ID))["1"];
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,new(),7,"Retire unused scope")).ok);
        await using var verify=f.NewContext();
        var line=await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==id);
        Assert.Equal("Retired",line.Status);Assert.NotNull(line.RetiredAt);
        Assert.Equal("Retire unused scope",line.RetiredReason);
        Assert.Empty(await verify.BoqLineStates.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cost_only_edit_rotates_the_token_and_stale_cost_does_not_replace_it()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Code="1",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=1}},7,"Initial scope")).ok);
        var id=(await f.ActiveLineIdsByCodeAsync(project.ID))["1"];var token=await f.LineTokenAsync(id);
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Id=id,Code="1",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=3,ExpectedToken=token}},7,"Estimate update")).ok);
        Assert.NotEqual(token,await f.LineTokenAsync(id));
        Assert.False((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Id=id,Code="1",Description="Work",Quantity=10,UnitPrice=2,MaterialCost=9,ExpectedToken=token}},8,"Stale estimate")).ok);
        await using var verify=f.NewContext();
        Assert.Equal(3m,(await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==id)).MaterialCost);
    }

    [Fact]
    public async Task A_conflict_after_new_id_assignment_rolls_back_lines_and_audit_together()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        var scope=new CompanyScopeHolder();scope.Set(1,null);
        var options=new DbContextOptionsBuilder<CrossDbContext>()
            .UseSqlite(f.Db.Database.GetDbConnection()).AddInterceptors(new FailSecondSave()).Options;
        await using(var db=new CrossDbContext(options,scope))
        {
            var audit=new ConstructionAuditService(db);var service=new BoqService(db,audit);
            var result=await service.SaveLinesAsync(1,project.ID,
                new(){new(){Code="1",Description="Must roll back",Quantity=1,UnitPrice=2}},7,"Atomic save proof");
            Assert.False(result.ok);Assert.Contains("another user",result.error!);
        }
        await using var verify=f.NewContext();
        Assert.Empty(await verify.BoqItems.AsNoTracking().Where(x=>x.ProjectId==project.ID).ToListAsync());
        Assert.Empty(await verify.ConstructionAuditEntries.AsNoTracking().Where(x=>x.ProjectId==project.ID).ToListAsync());
    }

    [Fact]
    public async Task Existing_certificate_without_a_capture_is_not_given_an_invented_snapshot()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,
            new(){new(){Code="1",Description="Work",Quantity=10,UnitPrice=2}},7,"Initial scope")).ok);
        var id=(await f.ActiveLineIdsByCodeAsync(project.ID))["1"];
        var certificate=await f.SeedPostedClientCertificateAsync(project.ID,id,periodValue:10m);
        var lineId=await f.CertificateLineIdAsync(certificate.ID);
        Assert.Null(await f.Revisions.ReproduceCertificateLineAsync(1,lineId));
        await using var verify=f.NewContext();
        Assert.Null((await verify.ProgressBillingLines.AsNoTracking().SingleAsync(x=>x.ID==lineId)).SnapshotCapturedAt);
        Assert.Empty(await verify.CertificateLineSnapshots.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_certified_line_cannot_change_its_unit_without_an_impact_review_and_revision(bool singleItem)
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,new(){new(){Code="U",Description="Certified work",Unit="m3",Quantity=10,UnitPrice=2}},7,"Initial")).ok);
        var id=(await f.ActiveLineIdsByCodeAsync(project.ID))["U"];
        await f.SeedPostedClientCertificateAsync(project.ID,id,periodValue:2m);
        if(singleItem)
            Assert.False((await f.Boq.SaveItemAsync(new CrossBuy.Models.Context.Accounting.BoqItem{ID=id,CompanyID=1,ProjectId=project.ID,Code="U",Description="Certified work",Unit="pcs",Quantity=10,UnitPrice=2})).ok);
        else
            Assert.False((await f.Boq.SaveLinesAsync(1,project.ID,new(){new(){Id=id,Code="U",Description="Certified work",Unit="pcs",Quantity=10,UnitPrice=2}},7,"Invalid unit reinterpretation")).ok);
        await using var verify=f.NewContext();
        Assert.Equal("m3",(await verify.BoqItems.AsNoTracking().SingleAsync(x=>x.ID==id)).Unit);
    }

    [Fact]
    public async Task An_uncommitted_line_can_correct_its_unit_without_replacing_its_identity()
    {
        using var f=new ConstructionTestFixture();var project=await f.SeedProjectAsync();
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,new(){new(){Code="U",Description="Draft work",Unit="m",Quantity=10,UnitPrice=2}},7,"Initial")).ok);
        var id=(await f.ActiveLineIdsByCodeAsync(project.ID))["U"];
        Assert.True((await f.Boq.SaveLinesAsync(1,project.ID,new(){new(){Id=id,Code="U",Description="Draft work",Unit="m3",Quantity=10,UnitPrice=2}},7,"Correct draft unit")).ok);
        await using var verify=f.NewContext();var stored=await verify.BoqItems.AsNoTracking().SingleAsync();
        Assert.Equal(id,stored.ID);Assert.Equal("m3",stored.Unit);
    }

    private sealed class FailSecondSave : SaveChangesInterceptor
    {
        private int _calls;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result,CancellationToken cancellationToken=default)
        {
            if(++_calls==2)throw new DbUpdateConcurrencyException("Synthetic conflict between identity and audit save");
            return ValueTask.FromResult(result);
        }
    }
}
