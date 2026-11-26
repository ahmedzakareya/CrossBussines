using CrossBuy.BL.Workspace;
using CrossBuy.Models.Context.Admin;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Communication;
using Xunit;

namespace CrossBuy.Tests;

public sealed class WorkspaceNotificationExpiryTests
{
    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2050, 1, 2, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("R3 UTC+3", TimeSpan.FromHours(3), "R3", "R3");
    }

    [Fact]
    public async Task Summary_page_and_badge_use_utc_expiry_and_keep_company_and_recipient_scope()
    {
        using var host = new CommunicationTestHost();
        var clock = new FixedClock();
        var now = clock.GetUtcNow().UtcDateTime;
        Notification Notice(int company, int employee, string title, DateTime? expires) => new()
        {
            CompanyID=company, RecipientEmployeeID=employee, TitleEn=title, TitleAr=title,
            CreatedAt=now.AddHours(-1), ExpiresAt=expires, IsRead=false
        };
        host.Db.Notifications.AddRange(
            Notice(1, 11, "valid in UTC", now.AddHours(1)),
            Notice(1, 11, "never expires", null),
            Notice(1, 11, "expired", now.AddTicks(-1)),
            Notice(1, 11, "boundary expired", now),
            Notice(1, 12, "other employee", null));
        await host.Db.SaveChangesAsync();
        var context = new BusinessContext {CompanyId=1, EmployeeId=11, Source=BusinessContextSource.Test};
        using var fresh=host.NewContext();
        var source=new PlatformNotificationWorkspaceSource(fresh,clock);
        var summary=await source.GetAsync(context,false,25);
        var page=await source.GetPageAsync(context,false,0,25);
        Assert.Equal(new[]{"never expires","valid in UTC"},summary.Select(x=>x.Title));
        Assert.Equal(summary.Select(x=>x.Id),page.Select(x=>x.Id));
        Assert.Equal(2,await source.CountAsync(context,false));
        Assert.Equal(2,await source.CountUnreadAsync(context));
        var unbound=new BusinessContext {CompanyId=1,Source=BusinessContextSource.Test};
        Assert.Empty(await source.GetAsync(unbound,false,25));
        Assert.Equal(0,await source.CountUnreadAsync(unbound));
    }
}
