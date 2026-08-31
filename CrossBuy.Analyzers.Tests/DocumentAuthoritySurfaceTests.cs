namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// The Central Document Platform was added to the declared authority surface. These tests pin the
/// BOUNDARY of that addition, not the platform's own behaviour.
///
/// The addition is unusual and worth guarding carefully, because it credits a SERVICE and not only a
/// resolver. That is the shape CORRECTION-004 warns about: a credit is safe only while every member of
/// the credited type really does take a decision. Two things hold it in place — these tests, which prove
/// the analyzer credits the declared names and nothing that merely resembles them, and
/// DocumentAuthorityMemberTests in CrossBuy.Tests, which drives every member of the real interface
/// through a recording resolver and fails if one stops asking.
///
/// The failure this guards against is not "documents are broken". It is a surface that says "authorized"
/// where no decision is taken, which converts an unknown into a false assurance.
/// </summary>
public class DocumentAuthoritySurfaceTests
{
    private static EndpointFacts Single(string controllerSource, string action)
    {
        var compilation = AnalyzerHarness.CompileController(controllerSource);
        AnalyzerHarness.AssertCompiles(compilation);
        var inventory = AuthorizationInventory.Build(compilation, CancellationToken.None);
        var match = inventory.Endpoints.SingleOrDefault(e => e.Action == action);
        Assert.NotNull(match);
        return match!;
    }

    // -----------------------------------------------------------------------------------------------------
    // RECOGNISED — the approved authorities are credited.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_submission_endpoint_that_reaches_the_document_service_is_credited()
    {
        // The real shape of Documents/submit. The controller names no company, no employee, no
        // confidentiality and no status: it hands the request down, and SubmitAsync authorizes.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IPlatformDocumentService _documents;
    public TestController(IPlatformDocumentService documents) { _documents = documents; }

    [HttpPost]
    public async Task<IActionResult> Submit(string entityType, int entityId)
    {
        var result = await _documents.SubmitAsync(
            new DocumentSubmissionRequest { EntityType = entityType, EntityId = entityId },
            System.IO.Stream.Null);
        return result.Ok ? Ok() : NotFound();
    }
}
""", "Submit");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Contains("IPlatformDocumentService", endpoint.InBodyEvidence);
    }

    [Fact]
    public void A_verification_endpoint_is_credited_on_the_same_authority()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IPlatformDocumentService _documents;
    public TestController(IPlatformDocumentService documents) { _documents = documents; }

    [HttpPost]
    public async Task<IActionResult> Verify(long id, string? note)
    {
        var result = await _documents.VerifyAsync(id, note);
        return result.Ok ? Ok() : NotFound();
    }
}
""", "Verify");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void The_access_spine_itself_is_credited_when_a_controller_asks_it_directly()
    {
        // No endpoint does this today; the declaration covers it because a future consumer of the spine
        // is authorizing just as genuinely as the service is, and the surface should not punish the shape.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IDocumentAccessResolver _access;
    public TestController(IDocumentAccessResolver access) { _access = access; }

    [HttpPost]
    public async Task<IActionResult> Attach(int entityId)
    {
        var decision = await _access.AuthorizeAsync(
            new BusinessContext(), new DocumentOwnerRef("Employee", entityId), DocumentAction.Upload, 1);
        if (!decision.Allowed) return NotFound();
        return Ok();
    }
}
""", "Attach");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void The_concrete_types_are_credited_too_because_a_holder_may_hold_either()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly PlatformDocumentService _documents;
    public TestController(PlatformDocumentService documents) { _documents = documents; }

    [HttpPost]
    public async Task<IActionResult> Reject(long id, string note)
    {
        var result = await _documents.RejectAsync(id, note);
        return result.Ok ? Ok() : NotFound();
    }
}
""", "Reject");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    // -----------------------------------------------------------------------------------------------------
    // REFUSED — the boundary. Each of these would be a false credit.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_UNRELATED_service_is_not_credited_by_the_document_addition()
    {
        // The addition must widen nothing outside the two declared types. IThingService authorizes
        // nothing and is named nowhere in the surface; if this started passing, the analyzer would have
        // begun crediting by shape.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IThingService _things;
    public TestController(IThingService things) { _things = things; }

    [HttpPost]
    public async Task<IActionResult> SaveThing(int id)
    {
        await _things.SaveAsync(id);
        return Ok();
    }
}
""", "SaveThing");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void A_document_STORAGE_service_is_not_an_authority_however_close_it_sits()
    {
        // The nearest miss in the whole platform, and the one worth a test of its own: IDocumentStorage
        // lives in the same folder, is injected beside the service and handles the same files. It moves
        // bytes. Storing a file is not deciding who may, and an endpoint that reached only storage would
        // have written a blob with nobody having been asked.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IDocumentStorage _storage;
    public TestController(IDocumentStorage storage) { _storage = storage; }

    [HttpPost]
    public async Task<IActionResult> Store(int id)
    {
        await _storage.StoreAsync(System.IO.Stream.Null, "x.pdf");
        return Ok();
    }
}
""", "Store");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void A_look_alike_resolver_with_the_same_method_shape_is_NOT_credited()
    {
        // Authority is granted by NAME, deliberately, one type at a time. A type nobody declared is not
        // an authority however exactly its signature matches.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IFakeDocumentAccessResolver _access;
    public TestController(IFakeDocumentAccessResolver access) { _access = access; }

    [HttpPost]
    public async Task<IActionResult> Attach(int entityId)
    {
        var decision = await _access.AuthorizeAsync(
            new BusinessContext(), new DocumentOwnerRef("Employee", entityId), DocumentAction.Upload, 1);
        if (!decision.Allowed) return NotFound();
        return Ok();
    }
}
""", "Attach");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void A_genuinely_unprotected_mutation_is_still_caught_after_the_addition()
    {
        // The control. If declaring an authority could make a bare POST look protected, the whole
        // measurement would be worthless — so the addition is proven not to have moved the floor.
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost]
    public async Task<IActionResult> DeleteEverything(int id)
    {
        await Task.CompletedTask;
        return Ok();
    }
}
""", "DeleteEverything");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void Sitting_on_a_controller_that_holds_an_authority_credits_nothing_by_proximity()
    {
        // Credit is per endpoint, resolved from that endpoint's own call graph. Purge never reaches the
        // document service, so injecting one beside it must change nothing.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IPlatformDocumentService _documents;
    private readonly IDocumentStorage _storage;
    public TestController(IPlatformDocumentService documents, IDocumentStorage storage)
    { _documents = documents; _storage = storage; }

    [HttpPost]
    public async Task<IActionResult> Purge(string key)
    {
        await _storage.DeleteAsync(key);
        return Ok();
    }
}
""", "Purge");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }
}
