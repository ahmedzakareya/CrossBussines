using CrossBuy.BL;
using CrossBuy.BL.Documents;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Context.Accounting;
using CrossBuy.Models.Context.Documents;
using CrossBuy.Models.Context.Platform;
using CrossBuy.Models.Context.Portal;
using CrossBuy.Models.Platform;
using CrossBuy.Tests.Portal;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CrossBuy.Tests;

public class CustomerDocumentWaveTests
{
    private static BusinessContext Actor(int company = 1, int employee = 7)
        => new() { CompanyId = company, EmployeeId = employee, Source = BusinessContextSource.Test };
    private static AccountingAccessService Accounting(PlatformTestHost h, BusinessContext actor)
        => new(h.Seed, new HttpContextAccessor(), new StubContextAccessor(actor),
            new BootstrapAccessPolicyReader(h.Seed, NullLogger<BootstrapAccessPolicyReader>.Instance),
            NullLogger<AccountingAccessService>.Instance);
    private static void Grant(PlatformTestHost h, string role, int company = 1, int employee = 7)
    {
        h.Seed.AccountingUserRoles.Add(new AccountingUserRole { CompanyID = company, EmployeeId = employee, Role = role });
        h.Seed.SaveChanges();
    }

    [Theory]
    [InlineData("CustomerDocumentUploader", true, false)]
    [InlineData("CustomerDocumentPublisher", false, true)]
    [InlineData("ChiefAccountant", false, false)]
    [InlineData("Accountant", false, false)]
    [InlineData("Auditor", false, false)]
    public async Task Upload_and_publication_are_independent_explicit_grants(string role, bool upload, bool publish)
    {
        using var h = new PlatformTestHost();
        Grant(h, role);
        var access = Accounting(h, Actor());
        Assert.Equal(upload, await access.CanAsync(Actor(), AccountingActions.CustomerDocumentUpload));
        Assert.Equal(publish, await access.CanAsync(Actor(), AccountingActions.CustomerDocumentPublish));
        Assert.False(await access.CanAsync(Actor(2), AccountingActions.CustomerDocumentPublish));
        Assert.False(await access.CanAsync(Actor(employee: 8), AccountingActions.CustomerDocumentUpload));
    }

    [Fact]
    public async Task No_grants_or_portal_identity_never_acquire_write_authority()
    {
        using var h = new PlatformTestHost();
        var service = Accounting(h, Actor());
        foreach (var action in new[] { AccountingActions.CustomerDocumentUpload, AccountingActions.CustomerDocumentPublish })
        {
            Assert.False(await service.CanAsync(Actor(), action));
            Assert.False(await service.CanAsync(Actor(0), action));
            Assert.False(await service.CanAsync(new() { CompanyId = 1, EmployeeId = 7, Source = BusinessContextSource.ExternalPortal }, action));
        }
    }

    private static (ClientDocumentPublicationService Service, long Id) Writer(DocumentPortalHost h,
        string grant = AccountingActions.CustomerDocumentPublisher, BusinessContext? actor = null, IBusinessEventService? events = null)
    {
        actor ??= Actor();
        h.Db.Customers.Add(new Customer { ID = 100, CompanyID = 1, Name = "Acme" });
        h.Db.Set<PlatformDocumentType>().Add(new PlatformDocumentType
        { CompanyID = 1, Code = "CustomerContract", AppliesToEntityTypes = "Customer", IsActive = true });
        h.Db.SaveChanges();
        var id = h.SeedDocument(1);
        var doc = h.Db.Set<PlatformDocument>().Single(x => x.Id == id);
        doc.DocumentTypeId = h.Db.Set<PlatformDocumentType>().Single().Id;
        h.Db.SaveChanges();
        Grant(h.Platform, grant);
        var accounting = Accounting(h.Platform, actor);
        var access = new DocumentAccessResolver(new[] { new CustomerDocumentOwnerResolver(h.Db) },
            new[] { accounting }, h.Platform.Registry(h.Db), NullLogger<DocumentAccessResolver>.Instance);
        return (new(h.Db, new StubContextAccessor(actor), access, accounting,
            events ?? h.Platform.Events(h.Db, actor), TimeProvider.System), id);
    }

    [Fact]
    public async Task Publish_retry_revoke_retry_and_republish_keep_history_and_audit()
    {
        using var h = new DocumentPortalHost();
        var (service, id) = Writer(h);
        Assert.True((await service.PublishAsync(id)).Ok);
        Assert.True((await service.PublishAsync(id)).Ok);
        Assert.Single(await h.Db.Set<ClientDocumentPublication>().AsNoTracking().ToListAsync());
        h.ActAs();
        Assert.Single(await h.Service().ListAsync());
        Assert.NotNull(await h.Service().OpenAsync(h.Db.Set<ClientDocumentPublication>().Single().Id));
        Assert.True((await service.RevokeAsync(id)).Ok);
        Assert.True((await service.RevokeAsync(id)).Ok);
        Assert.Empty(await h.Service().ListAsync());
        Assert.True((await service.PublishAsync(id)).Ok);
        var rows = await h.Db.Set<ClientDocumentPublication>().AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows.Where(x => x.RevokedAt == null));
        Assert.Equal(3, await h.Db.Set<BusinessEvent>().CountAsync());
        Assert.Single(await h.Db.Set<PlatformDocument>().AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("Submitted", "Internal", "Customer", 1)]
    [InlineData("Rejected", "Internal", "Customer", 1)]
    [InlineData("Active", "Confidential", "Customer", 1)]
    [InlineData("Active", "Internal", "Employee", 1)]
    [InlineData("Active", "Internal", "Customer", 2)]
    public async Task Writer_refuses_ineligible_documents_without_grants_or_events(string status, string tier, string family, int company)
    {
        using var h = new DocumentPortalHost();
        var (service, id) = Writer(h);
        var doc = h.Db.Set<PlatformDocument>().Single(x => x.Id == id);
        doc.Status = status; doc.Confidentiality = tier; doc.EntityType = family; doc.CompanyID = company;
        h.Db.SaveChanges();
        Assert.False((await service.PublishAsync(id)).Ok);
        Assert.Empty(await h.Db.Set<ClientDocumentPublication>().ToListAsync());
        Assert.Empty(await h.Db.Set<BusinessEvent>().ToListAsync());
    }

    [Fact]
    public async Task Uploader_cannot_publish_and_foreign_customer_owner_is_refused()
    {
        using var h = new DocumentPortalHost();
        var (service, id) = Writer(h, AccountingActions.CustomerDocumentUploader);
        Assert.False((await service.PublishAsync(id)).Ok);
        Assert.False((await service.RevokeAsync(id)).Ok);
        Grant(h.Platform, AccountingActions.CustomerDocumentPublisher);
        var owner = await h.Db.Customers.SingleAsync();
        owner.CompanyID = 2; h.Db.SaveChanges();
        Assert.False((await service.PublishAsync(id)).Ok);
    }

    [Theory]
    [InlineData("wrong-type")]
    [InlineData("inactive-type")]
    [InlineData("foreign-type")]
    [InlineData("missing-type")]
    [InlineData("missing-version")]
    [InlineData("foreign-version")]
    public async Task Writer_requires_an_eligible_type_and_an_owned_current_version(string defect)
    {
        using var h = new DocumentPortalHost();
        var (service,id)=Writer(h);
        var doc=h.Db.Set<PlatformDocument>().Single();
        var type=h.Db.Set<PlatformDocumentType>().Single();
        if(defect=="wrong-type") type.AppliesToEntityTypes="Employee";
        if(defect=="inactive-type") type.IsActive=false;
        if(defect=="foreign-type") type.CompanyID=2;
        if(defect=="missing-type") doc.DocumentTypeId=null;
        if(defect=="missing-version") doc.CurrentVersionId=null;
        if(defect=="foreign-version") h.Db.Set<PlatformDocumentVersion>().Single().CompanyID=2;
        h.Db.SaveChanges();
        Assert.False((await service.PublishAsync(id)).Ok);
        Assert.Empty(await h.Db.Set<ClientDocumentPublication>().AsNoTracking().ToListAsync());
    }

    private sealed class FailingEvents : IBusinessEventService
    {
        public Task<long> RecordAsync(BusinessEventRecord record,CancellationToken ct=default) => throw new InvalidOperationException("audit unavailable");
        public BusinessEventEnvelope BuildEnvelope(BusinessEvent stored) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("CustomerDocumentUploader",true)]
    [InlineData("CustomerDocumentPublisher",false)]
    public async Task Actual_platform_upload_uses_the_customer_owner_and_independent_upload_grant(string role,bool allowed)
    {
        using var h=new DocumentPortalHost();
        Writer(h,role);
        var actor=Actor(); var accounting=Accounting(h.Platform,actor);
        var resolver=new DocumentAccessResolver(new[] {new CustomerDocumentOwnerResolver(h.Db)},new[] {accounting},
            h.Platform.Registry(h.Db),NullLogger<DocumentAccessResolver>.Instance);
        using var storage=new TempDocumentStorage();
        var platform=new PlatformDocumentService(h.Db,new StubContextAccessor(actor),resolver,storage.Storage,h.Platform.Registry(h.Db));
        var result=await platform.UploadAsync(new DocumentUploadRequest
        {EntityType=EntityRegistry.Customer,EntityId=100,DocumentTypeId=h.Db.Set<PlatformDocumentType>().Single().Id,
         FileName="contract.pdf",ContentType="application/pdf"},new MemoryStream(System.Text.Encoding.UTF8.GetBytes("%PDF-1.4 sample")));
        Assert.Equal(allowed,result.Ok);
        using var reread=h.Platform.AllCompanies();
        Assert.Equal(allowed ? 2 : 1,await reread.Set<PlatformDocument>().CountAsync());
    }

    [Fact]
    public async Task Audit_failure_rolls_back_publication_and_retains_the_document()
    {
        using var h=new DocumentPortalHost();
        var (service,id)=Writer(h,events:new FailingEvents());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>service.PublishAsync(id));
        using var reread=h.Platform.AllCompanies();
        Assert.Empty(await reread.Set<ClientDocumentPublication>().AsNoTracking().ToListAsync());
        Assert.Single(await reread.Set<PlatformDocument>().AsNoTracking().ToListAsync());
    }
}
