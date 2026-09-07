using CrossBuy.BL;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Comm;
using CrossBuy.Models.Context.Inventory;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 (Slice-003) — Quotation registration + DocComment entity-code validation.
    //
    // Quotation was the one surviving instance of the problem ADR-002 exists to prevent: QuotationDetails.cshtml
    // stored document comments under a FREE-TEXT EntityType. The literal it used happens to equal the canonical
    // PascalCase code exactly, so registration needed no alias mapping — but that was luck, and these tests
    // verify it rather than assuming it.
    public class Slice3QuotationCommentTests
    {
        // The exact literals already written to DocComments by the three wired screens.
        private const string StoredQuotationType = "Quotation";
        private const string StoredPurchaseInvoiceType = "PurchaseInvoice";

        private static IDocCommentService Comments(PlatformTestHost host)
            => new DocCommentService(host.Db, new NoOpNotificationService(), host.Registry());

        // ---- Test 26: registry definition valid ----
        [Fact]
        public void Quotation_is_registered_with_comments_enabled_and_a_real_route()
        {
            using var host = new PlatformTestHost();
            var d = host.Registry().GetDefinition(EntityRegistry.Quotation);

            Assert.Equal("Quotation", d.Code);
            Assert.Equal(StoredQuotationType, d.Code);   // the stored literal IS the canonical code
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayNameAr));
            Assert.NotEqual(d.DisplayNameAr, d.DisplayNameEn);
            Assert.Equal("Inventory", d.Module);
            Assert.Equal(EntityRegistry.ScopeInventory, d.PermissionScope);
            Assert.True(d.SupportsComments);            // _DocTimeline is wired on QuotationDetails.cshtml
            Assert.True(d.SupportsSearch);
            Assert.False(d.SupportsTimeline);           // no producer, no screen — the flag must not over-promise
            Assert.False(d.ListedInRecordPicker);       // never a TM-2 picker type
        }

        [Fact]
        public void Registering_Quotation_did_not_change_the_record_picker()
        {
            using var host = new PlatformTestHost();
            ITaskLinkResolver resolver = new TaskLinkResolver(host.Registry());
            Assert.Equal(
                new[] { "SalesInvoice", "Customer", "ManufWorkOrder", "PosOrder", "Employee", "Project", "Item" },
                resolver.Types().Select(t => t.Key).ToArray());
        }

        // ---- Test 27: URL + search + resolve ----
        [Fact]
        public async Task Quotation_url_search_and_resolve_work_against_real_rows()
        {
            using var host = new PlatformTestHost();
            host.Db.Quotations.Add(new Quotation
            {
                CompanyID = 1, QuoteNo = "QT-2026-00031", CustomerId = 1,
                QuoteDate = new DateTime(2026, 5, 1), Status = "Draft", GrandTotal = 750m,
            });
            await host.Db.SaveChangesAsync();
            var id = host.Db.Quotations.Single().ID;

            var registry = host.Registry();
            Assert.Equal($"/Inventory/QuotationDetails?id={id}", registry.BuildUrl(EntityRegistry.Quotation, id));

            var found = await registry.SearchAsync(EntityRegistry.Quotation, "QT-2026", PlatformTestHost.DefaultContext());
            Assert.Equal("QT-2026-00031", Assert.Single(found).Label);

            var resolved = await registry.ResolveAsync(EntityRegistry.Quotation, id, PlatformTestHost.DefaultContext());
            Assert.True(resolved.Found);
            Assert.Equal("QT-2026-00031", resolved.Label);

            // Cross-company resolve is refused and yields no navigable link.
            var other = await registry.ResolveAsync(EntityRegistry.Quotation, id, PlatformTestHost.DefaultContext(companyId: 2));
            Assert.False(other.Found);
            Assert.Null(other.Url);
            Assert.Empty(await registry.SearchAsync(EntityRegistry.Quotation, null, PlatformTestHost.DefaultContext(companyId: 2)));
        }

        // ---- Test 27 (continued): existing comments remain visible ----
        [Fact]
        public async Task Comments_written_before_the_registry_still_load()
        {
            using var host = new PlatformTestHost();

            // Rows exactly as the pre-Stage-0 screens wrote them: a free-text EntityType, no validation.
            host.Db.DocComments.Add(new DocComment
            { CompanyID = 1, EntityType = StoredQuotationType, EntityId = 31, Body = "تعليق قديم على عرض السعر", CreatedAt = DateTime.UtcNow.AddYears(-1) });
            host.Db.DocComments.Add(new DocComment
            { CompanyID = 1, EntityType = StoredPurchaseInvoiceType, EntityId = 21, Body = "old purchase note", CreatedAt = DateTime.UtcNow.AddYears(-1) });
            // A row whose type is NOT registered at all — e.g. a screen that was later removed. Reads must still
            // work: validation is write-only by design, so history never disappears.
            host.Db.DocComments.Add(new DocComment
            { CompanyID = 1, EntityType = "SomeRetiredScreen", EntityId = 9, Body = "legacy orphan", CreatedAt = DateTime.UtcNow.AddYears(-2) });
            await host.Db.SaveChangesAsync();

            var svc = Comments(host);
            Assert.Single(await svc.ListAsync(1, 7, StoredQuotationType, 31));
            Assert.Single(await svc.ListAsync(1, 7, StoredPurchaseInvoiceType, 21));
            Assert.Single(await svc.ListAsync(1, 7, "SomeRetiredScreen", 9));
        }

        [Fact]
        public async Task A_new_comment_on_a_registered_commentable_entity_is_accepted()
        {
            using var host = new PlatformTestHost();
            var svc = Comments(host);

            var dto = await svc.AddAsync(1, 7, EntityRegistry.Quotation, 31, "تعليق جديد", null);
            Assert.True(dto.Id > 0);

            var stored = await host.NewContext().DocComments.SingleAsync();
            Assert.Equal("Quotation", stored.EntityType);   // canonical code persisted
            Assert.Equal(1, stored.CompanyID);
        }

        // ---- Test 28: invalid free-text type rejected ----
        [Theory]
        [InlineData("Quotations")]        // plural
        [InlineData("quotation")]         // wrong case — codes are ordinal
        [InlineData("QUOTATION")]
        [InlineData("SalesQuote")]        // invented
        [InlineData("purchase_invoice")]  // the NotificationTypes catalog key, not a code
        [InlineData("WorkOrder")]         // the journal SourceType value, not a code
        public async Task A_new_comment_with_an_unregistered_entity_type_is_rejected(string badType)
        {
            using var host = new PlatformTestHost();
            var svc = Comments(host);

            await Assert.ThrowsAsync<DocCommentEntityTypeException>(
                () => svc.AddAsync(1, 7, badType, 31, "should not persist", null));

            Assert.Equal(0, await host.NewContext().DocComments.CountAsync());
        }

        [Fact]
        public async Task A_registered_entity_that_does_not_support_comments_is_rejected()
        {
            using var host = new PlatformTestHost();
            var svc = Comments(host);

            // Item is registered but SupportsComments is false — no screen collects comments for it, so accepting
            // one would create data nothing can display.
            Assert.False(host.Registry().GetDefinition(EntityRegistry.Item).SupportsComments);
            await Assert.ThrowsAsync<DocCommentEntityTypeException>(
                () => svc.AddAsync(1, 7, EntityRegistry.Item, 5, "nowhere to show this", null));
            Assert.Equal(0, await host.NewContext().DocComments.CountAsync());
        }

        [Fact]
        public async Task A_non_positive_entity_id_is_rejected()
        {
            using var host = new PlatformTestHost();
            var svc = Comments(host);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => svc.AddAsync(1, 7, EntityRegistry.Quotation, 0, "no target", null));
        }

        // ---- Test 29: cross-company comment access denied ----
        [Fact]
        public async Task Comments_are_not_visible_across_companies()
        {
            using var host = new PlatformTestHost();
            host.Db.DocComments.Add(new DocComment
            { CompanyID = 1, EntityType = EntityRegistry.Quotation, EntityId = 31, Body = "company one", CreatedAt = DateTime.UtcNow });
            host.Db.DocComments.Add(new DocComment
            { CompanyID = 2, EntityType = EntityRegistry.Quotation, EntityId = 31, Body = "company two", CreatedAt = DateTime.UtcNow });
            await host.Db.SaveChangesAsync();

            var svc = Comments(host);
            var one = await svc.ListAsync(1, 7, EntityRegistry.Quotation, 31);
            var two = await svc.ListAsync(2, 7, EntityRegistry.Quotation, 31);

            Assert.Equal("company one", Assert.Single(one).Body);
            Assert.Equal("company two", Assert.Single(two).Body);
            // The same entity id in two companies must never merge.
            Assert.Empty(await svc.ListAsync(3, 7, EntityRegistry.Quotation, 31));
        }

        [Fact]
        public async Task A_soft_deleted_comment_is_not_returned()
        {
            using var host = new PlatformTestHost();
            host.Db.DocComments.Add(new DocComment
            {
                CompanyID = 1, EntityType = EntityRegistry.Quotation, EntityId = 31, Body = "deleted",
                CreatedAt = DateTime.UtcNow, DeletedAt = DateTime.UtcNow,
            });
            await host.Db.SaveChangesAsync();
            Assert.Empty(await Comments(host).ListAsync(1, 7, EntityRegistry.Quotation, 31));
        }
    }

    // The notification side-effect of AddAsync is not what these tests exercise, and the real service needs a
    // SignalR hub context. This double records nothing and never throws.
    internal sealed class NoOpNotificationService : INotificationService
    {
        public Task NotifyAsync(int recipientEmployeeId, string? titleAr, string? titleEn,
            string? bodyAr, string? bodyEn, string type, int? refId = null,
            string? url = null, int? companyId = null, int? actorEmployeeId = null,
            string? priority = null, string? category = null, string? dedupKey = null,
            DateTime? expiresAt = null, string? icon = null,
            string? entityType = null, int? entityId = null) => Task.CompletedTask;

        public Task<int> NotifyRoleAsync(int companyId, string scope, string[] roles,
            string? titleAr, string? titleEn, string? bodyAr, string? bodyEn,
            string type, int? refId = null, int? exceptEmployeeId = null) => Task.FromResult(0);
    }
}
