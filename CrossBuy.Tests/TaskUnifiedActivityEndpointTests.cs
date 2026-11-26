using System.Text.Json;
using CrossBuy.BL;
using CrossBuy.BL.Communication;
using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests;

public sealed class TaskUnifiedActivityEndpointTests
{
    private sealed class Company(bool resolved = true) : IRequestCompanyResolver
    {
        public Task<CompanyResolution> ResolveAsync(int? requestSuppliedCompanyId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(resolved ? CompanyResolution.Resolved(7, 11, null) : CompanyResolution.Unresolved("test"));
    }
    private sealed class Access(bool allowed) : ITasksAccessService
    {
        public Task<bool> CanAsync(BusinessContext context, string action, PermissionTarget? target = null, CancellationToken cancellationToken = default)
            => Task.FromResult(allowed);
        public Task<AccessScope> ResolveScopeAsync(BusinessContext context, string action, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
    private sealed class Timeline : ICommTimelineAggregator
    {
        public int Calls;
        public CommTimelineQuery? Query;
        public BusinessContext? Context;
        public CommTimelineResult Result = CommTimelineResult.Empty();
        public Task<CommTimelineResult> GetAsync(BusinessContext context, CommTimelineQuery query, CancellationToken cancellationToken = default)
        {
            Calls++; Query = query; Context = context;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }
    private static BusinessContext Me(int company = 7) => new() { CompanyId = company, EmployeeId = 11, Source = BusinessContextSource.Test };
    private static TasksController Controller(IServiceProvider services, bool allowed = true, bool resolved = true)
        => new(tasks:null!, links:null!, ts:null!, cost:null!, billing:null!, gen:null!, reports:null!, matcher:null!,
            tasksAccess:new Access(allowed), businessContexts:new StubContextAccessor(Me()), company:new Company(resolved),
            accounting:null!, checklist:null!, deps:null!, templates:null!, escalation:null!, services:services, files:null!, env:null!);

    [Theory]
    [InlineData(false, true, 42)]
    [InlineData(true, false, 42)]
    [InlineData(true, true, 0)]
    public async Task Refused_task_never_reaches_the_collaboration_reader(bool allowed, bool resolved, int id)
    {
        var timeline = new Timeline();
        using var provider = new ServiceCollection().AddSingleton<ICommTimelineAggregator>(timeline).BuildServiceProvider();
        Assert.IsType<ForbidResult>(await Controller(provider, allowed, resolved).TaskActivity(id));
        Assert.Equal(0, timeline.Calls);
    }

    [Fact]
    public async Task Missing_communication_is_unavailable_and_not_an_empty_history()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var result = Assert.IsType<ObjectResult>(await Controller(provider).TaskActivity(42));
        Assert.Equal(503, result.StatusCode);
        Assert.Contains(TasksController.CommunicationUnavailableCode, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task Merged_comments_and_events_keep_their_identity_body_and_paging_cursor()
    {
        var stamp = new DateTime(2050,1,1,0,0,0,DateTimeKind.Utc);
        var entity = new CommEntityRef("Task",42);
        var timeline = new Timeline { Result = new CommTimelineResult
        {
            Items = new[] {
                new CommTimelineItem { ItemKey="BusinessEvent:one",Source=CommTimelineSourceKinds.BusinessEvent,Entity=entity,ItemType="Task.Created",TitleEn="Created",OccurredAt=stamp },
                new CommTimelineItem { ItemKey="Comment:one",Source=CommTimelineSourceKinds.Comment,Entity=entity,ItemType="comment",TitleEn="Comment",Body="<script>plain body</script>",OccurredAt=stamp }
            }, NextBefore=stamp, NextBeforeKey="Comment:one",
            SourceReports=new[] { new CommTimelineSourceReport { Source="failed",Contributed=false,ItemCount=0,Note="error: private connection information" } }
        }};
        using var provider=new ServiceCollection().AddSingleton<ICommTimelineAggregator>(timeline).BuildServiceProvider();
        var result=Assert.IsType<JsonResult>(await Controller(provider).TaskActivity(42,stamp,"cursor",999));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var items=json.RootElement.GetProperty("items");
        Assert.Equal(2,items.GetArrayLength());
        Assert.Equal("Comment:one",items[1].GetProperty("key").GetString());
        Assert.Equal("<script>plain body</script>",items[1].GetProperty("description").GetString());
        Assert.True(json.RootElement.GetProperty("degraded").GetBoolean());
        Assert.DoesNotContain("private connection",json.RootElement.GetRawText());
        Assert.Equal("Comment:one",json.RootElement.GetProperty("nextBeforeKey").GetString());
        Assert.Equal(100,timeline.Query!.Take);
        Assert.Equal(stamp,timeline.Query.Before);
        Assert.Equal("cursor",timeline.Query.BeforeKey);
        Assert.Equal(entity,timeline.Query.Entity);
        Assert.Equal(7,timeline.Context!.CompanyId);
        Assert.Equal(11,timeline.Context.EmployeeId);
    }

    [Fact]
    public async Task Communication_entity_refusal_is_not_presented_as_no_history()
    {
        var timeline=new Timeline { Result=new CommTimelineResult {
            Items=Array.Empty<CommTimelineItem>(),SourceReports=new[] { new CommTimelineSourceReport {
                Source="*",Contributed=false,ItemCount=0,Note="denied: not permitted" } } } };
        using var provider=new ServiceCollection().AddSingleton<ICommTimelineAggregator>(timeline).BuildServiceProvider();
        Assert.IsType<ForbidResult>(await Controller(provider).TaskActivity(42));
    }

    [Fact]
    public async Task Request_cancellation_propagates_instead_of_becoming_empty_activity()
    {
        using var provider=new ServiceCollection().AddSingleton<ICommTimelineAggregator>(new Timeline()).BuildServiceProvider();
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Controller(provider).TaskActivity(42,ct:cancellation.Token));
    }
}
