using CrossBuy.BL.Communication;
using CrossBuy.Models.Communication;
using CrossBuy.Models.Context.Communication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace CrossBuy.Tests.SqlServer;

[Collection(SqlServerCollection.Name)]
public sealed class CommDeliveryClaimStoreSqlTests
{
    private readonly SqlServerFixture _sql;

    public CommDeliveryClaimStoreSqlTests(SqlServerFixture sql) => _sql = sql;

    [SkippableFact]
    public async Task Sql_claims_are_atomic_and_scoped_to_the_requested_company()
    {
        Skip.If(!_sql.Available, _sql.SkipReason);
        await _sql.EnsureEfSchemaAsync();

        const int companyId = 1;
        const int otherCompanyId = 2;
        const int count = 16;
        var token = Guid.NewGuid().ToString("N");
        var notificationIds = new List<long>();
        var notifications = Enumerable.Range(0, count)
            .Select(i => NewNotification(companyId, $"{token}-mine-{i}"))
            .ToList();

        await using (var mine = _sql.NewContext())
        {
            mine.Set<CommNotification>().AddRange(notifications);
            await mine.SaveChangesAsync();
            notificationIds.AddRange(notifications.Select(x => x.Id));
            mine.Set<CommNotificationDelivery>().AddRange(notifications.Select((notification, i) =>
                NewDelivery(companyId, notification, $"{token}-mine-{i}")));
            await mine.SaveChangesAsync();
        }

        await using (var theirs = _sql.NewContextAsCompany(otherCompanyId))
        {
            var notification = NewNotification(otherCompanyId, $"{token}-other");
            theirs.Set<CommNotification>().Add(notification);
            await theirs.SaveChangesAsync();
            theirs.Set<CommNotificationDelivery>().Add(NewDelivery(otherCompanyId, notification, $"{token}-other"));
            await theirs.SaveChangesAsync();
        }

        var options = Options.Create(new CommunicationPlatformOptions
        {
            MaxDeliveryAttempts = 5,
            StaleClaimMinutes = 10,
        });
        IReadOnlyList<CommNotificationDelivery> first;
        IReadOnlyList<CommNotificationDelivery> second;
        await using (var db1 = _sql.NewContext())
        await using (var db2 = _sql.NewContext())
        {
            var store1 = new SqlServerCommDeliveryClaimStore(db1, options);
            var store2 = new SqlServerCommDeliveryClaimStore(db2, options);
            var claims = await Task.WhenAll(
                store1.ClaimAsync(companyId, count / 2),
                store2.ClaimAsync(companyId, count / 2));
            first = claims[0];
            second = claims[1];
        }

        var claimedIds = first.Select(x => x.Id).Concat(second.Select(x => x.Id)).ToArray();
        Assert.Equal(count, claimedIds.Length);
        Assert.Equal(count, claimedIds.Distinct().Count());
        Assert.All(first.Concat(second), row =>
        {
            Assert.Equal(companyId, row.CompanyID);
            Assert.Equal(CommDeliveryStatus.Claimed, row.Status);
        });

        await using var verify = _sql.NewContext();
        var other = await verify.Set<CommNotificationDelivery>()
            .IgnoreQueryFilters()
            .SingleAsync(x => x.DedupKey == $"{token}-other");
        Assert.Equal(otherCompanyId, other.CompanyID);
        Assert.Equal(CommDeliveryStatus.Pending, other.Status);

        var persisted = await verify.Set<CommNotificationDelivery>()
            .Where(x => notificationIds.Contains(x.NotificationId))
            .ToListAsync();
        Assert.Equal(count, persisted.Count);
        Assert.All(persisted, row => Assert.Equal(CommDeliveryStatus.Claimed, row.Status));
    }

    private static CommNotification NewNotification(int companyId, string dedupKey) => new()
    {
        CompanyID = companyId,
        TemplateKey = "test",
        Category = CommNotificationCategories.Comments,
        Priority = "Normal",
        LegacyType = "Comment",
        EntityType = "SalesInvoice",
        EntityId = 1,
        RecipientEmployeeId = 1,
        TitleAr = "اختبار",
        TitleEn = "Test",
        BodyAr = "اختبار",
        BodyEn = "Test",
        DedupKey = dedupKey,
        CreatedAt = DateTime.UtcNow,
    };

    private static CommNotificationDelivery NewDelivery(int companyId, CommNotification notification, string dedupKey) => new()
    {
        CompanyID = companyId,
        NotificationId = notification.Id,
        Channel = CommChannel.InApp,
        Status = CommDeliveryStatus.Pending,
        Attempts = 0,
        DedupKey = dedupKey,
        CreatedAt = DateTime.UtcNow,
    };
}
