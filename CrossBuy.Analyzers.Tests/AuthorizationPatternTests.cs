using System.Threading;
using Microsoft.CodeAnalysis;

namespace CrossBuy.Analyzers.Tests;

/// <summary>
/// Every authorization pattern the brief names, plus every Stage 1 failure pattern.
///
/// The assertions go through <see cref="AuthorizationInventory"/> — the same code path the diagnostics use — so a
/// test cannot pass against a classification the analyzer would not actually report.
/// </summary>
public class AuthorizationPatternTests
{
    // -----------------------------------------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------------------------------------

    private static AuthorizationInventoryResult Inventory(string controllerSource)
    {
        var compilation = AnalyzerHarness.CompileController(controllerSource);
        AnalyzerHarness.AssertCompiles(compilation);
        return AuthorizationInventory.Build(compilation, CancellationToken.None);
    }

    private static EndpointFacts Single(string controllerSource, string action)
    {
        var inventory = Inventory(controllerSource);
        var match = inventory.Endpoints.SingleOrDefault(e => e.Action == action);
        Assert.NotNull(match);
        return match!;
    }

    /// <summary>Used only by the multi-file tests, where each tree needs its own using block.</summary>
    private const string Using = Surface.Preamble;

    // -----------------------------------------------------------------------------------------------------
    // 1-2 — attribute forms
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_action_level_permission_attribute_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost][ValidateAntiForgeryToken][AccPerm("post")]
    public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.ActionAttribute, endpoint.Authorization);
        Assert.Contains("AccPerm", endpoint.PermissionAttributes);
    }

    [Fact]
    public void ApiPerm_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost, ApiPerm("acc", "post")]
    public IActionResult QuickAdd() => Ok();
}
""", "QuickAdd");

        Assert.Equal(AuthorizationKind.ActionAttribute, endpoint.Authorization);
    }

    [Fact]
    public void A_class_level_permission_attribute_is_inherited_by_every_action()
    {
        var inventory = Inventory("""
[PlatformOps]
public class TestController : Controller
{
    [HttpPost] public IActionResult A() => Ok();
    [HttpPost] public IActionResult B() => Ok();
}
""");

        Assert.Equal(2, inventory.MutatingCount);
        Assert.All(inventory.Endpoints, e =>
            Assert.Equal(AuthorizationKind.InheritedAttribute, e.Authorization));
    }

    [Fact]
    public void A_fully_qualified_attribute_is_credited_which_is_CORRECTION_003()
    {
        // 85 real permission attributes were invisible to the original text scanner because they were written
        // fully qualified. A symbol has no qualified form.
        var endpoint = Single("""
public class TestController : Controller
{
    [Microsoft.AspNetCore.Mvc.HttpPost]
    [CrossBuy.Models.AccPerm("post")]   // a trailing comment after the bracket: defect #7
    public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.ActionAttribute, endpoint.Authorization);
    }

    [Fact]
    public void An_attribute_inside_a_comment_is_not_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost]
    // [AccPerm("post")]  <- commented out, and must not count
    public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    // -----------------------------------------------------------------------------------------------------
    // 3-7 — call-graph forms
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void An_authority_called_through_an_interface_reference_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IAccountingAccessService _access;
    public TestController(IAccountingAccessService access) { _access = access; }

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await _access.CanAsync("post")) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Equal("IAccountingAccessService.CanAsync", endpoint.InBodyEvidence);
    }

    [Fact]
    public void A_private_helper_that_authorizes_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly ITasksAccessService _tasks;
    public TestController(ITasksAccessService tasks) { _tasks = tasks; }

    private async Task<bool> TaskGateAsync(string action) =>
        await _tasks.CanAsync(new BusinessContext(), action);

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await TaskGateAsync("edit")) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Contains("TaskGateAsync", endpoint.InBodyChain);
    }

    [Fact]
    public void A_transitive_helper_chain_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly ITasksAccessService _tasks;
    public TestController(ITasksAccessService tasks) { _tasks = tasks; }

    private async Task<bool> AskAsync(string a) => await _tasks.CanAsync(new BusinessContext(), a);
    private async Task<bool> MiddleAsync(string a) => await AskAsync(a);
    private async Task<bool> OuterAsync(string a) => await MiddleAsync(a);

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await OuterAsync("edit")) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Equal("Save -> OuterAsync -> MiddleAsync -> AskAsync -> ITasksAccessService.CanAsync",
            endpoint.InBodyChain);
    }

    [Fact]
    public void A_helper_in_another_partial_FILE_is_credited()
    {
        // The syntactic scanner declares it cannot do this: "It cannot follow an authorization call through an
        // interface, a base class or another file."
        var compilation = AnalyzerHarness.CompileFiles(
            ("Surface.cs", Surface.Preamble + Surface.Source),
            ("TestController.cs", Using + """
public partial class TestController : Controller
{
    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await GateAsync("edit")) return Forbid();
        return Ok();
    }
}
"""),
            ("TestController.Gate.cs", Using + """
public partial class TestController
{
    private readonly ITasksAccessService _tasks = null!;

    private async Task<bool> GateAsync(string action) =>
        await _tasks.CanAsync(new BusinessContext(), action);
}
"""));

        AnalyzerHarness.AssertCompiles(compilation);
        var inventory = AuthorizationInventory.Build(compilation, CancellationToken.None);

        var endpoint = Assert.Single(inventory.Endpoints);
        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void A_gate_on_a_BASE_controller_is_credited()
    {
        var endpoint = Single("""
public abstract class GuardedController : Controller
{
    protected readonly ITasksAccessService Tasks = null!;

    protected async Task<bool> GateAsync(string action) =>
        await Tasks.CanAsync(new BusinessContext(), action);
}

public class TestController : GuardedController
{
    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await GateAsync("edit")) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void An_authority_reached_through_a_local_function_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IAccountingAccessService _access;
    public TestController(IAccountingAccessService access) { _access = access; }

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        async Task<bool> AllowedAsync() => await _access.CanAsync("post");
        if (!await AllowedAsync()) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void Mutually_recursive_helpers_terminate_and_are_not_credited()
    {
        // The fixpoint's fixed point. A cycle must return "not authorized", not hang the compiler.
        var endpoint = Single("""
public class TestController : Controller
{
    private async Task<bool> PingAsync(int n) => n <= 0 ? false : await PongAsync(n - 1);
    private async Task<bool> PongAsync(int n) => n <= 0 ? false : await PingAsync(n - 1);

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await PingAsync(4)) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void Business_membership_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly ICommunicationAccessService _comm;
    public TestController(ICommunicationAccessService comm) { _comm = comm; }

    [HttpPost]
    public async Task<IActionResult> Send(int conversationId)
    {
        if (!await _comm.IsConversationParticipantAsync(new BusinessContext(), conversationId)) return Forbid();
        return Ok();
    }
}
""", "Send");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void An_approved_POS_role_check_is_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IPosAccessService _pos;
    public TestController(IPosAccessService pos) { _pos = pos; }

    [HttpPost]
    public IActionResult Pay()
    {
        if (!_pos.CanSell(new[] { "Cashier" })) return Forbid();
        return Ok();
    }
}
""", "Pay");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Equal("IPosAccessService.CanSell", endpoint.InBodyEvidence);
    }

    [Fact]
    public void The_Hotfix_A1_in_service_guard_is_credited()
    {
        var endpoint = Single("""
public class TestController : ControllerBase
{
    private readonly IAccountingApiAuthorization _guard;
    public TestController(IAccountingApiAuthorization guard) { _guard = guard; }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        if (!await _guard.AuthorizeAsync("post")) return Forbid();
        return Ok();
    }
}
""", "Post");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    // -----------------------------------------------------------------------------------------------------
    // 8-13 — the do-not-credit list. This block is the false-credit prevention suite.
    // -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Authentication_alone_is_not_authorization()
    {
        var endpoint = Single("""
using Microsoft.AspNetCore.Authorization;
[Authorize]
public class TestController : Controller
{
    [HttpPost] public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.True(endpoint.HasAuthenticationOnly);
    }

    [Fact]
    public void Anti_forgery_alone_is_not_authorization()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost][ValidateAntiForgeryToken] public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.True(endpoint.HasAntiForgery);
    }

    [Fact]
    public void SessionValidation_alone_is_not_authorization()
    {
        var endpoint = Single("""
[SessionValidation]
public class TestController : Controller
{
    [HttpPost] public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.True(endpoint.HasAuthenticationOnly);
    }

    [Fact]
    public void PosLaneActivityGuard_alone_is_not_authorization_which_is_CORRECTION_004()
    {
        // The defect that credited 44 mutating POS actions as protected by a filter that checks no role.
        var endpoint = Single("""
public class TestController : Controller
{
    [HttpPost][PosLaneActivityGuard("hyper")] public IActionResult Save() => Ok();
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.True(endpoint.HasLaneGuard);
    }

    [Fact]
    public void The_lane_predicate_is_not_laundered_by_living_on_an_authority_type()
    {
        // IsActivityAllowedForLane sits on IPosAccessService. "Any call on an access service" would credit it and
        // re-open CORRECTION-004 through the back door.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IPosAccessService _pos;
    public TestController(IPosAccessService pos) { _pos = pos; }

    [HttpPost]
    public IActionResult Save()
    {
        var (laneOk, _) = _pos.IsActivityAllowedForLane("HYPER", "hyper");
        if (!laneOk) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void DevOnly_is_recognized_as_an_environment_gate_and_is_not_credited()
    {
        var endpoint = Single("""
[DevOnly]
public class TestController : Controller
{
    [HttpPost] public IActionResult Seed() => Ok();
}
""", "Seed");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.True(endpoint.HasEnvironmentGate);
    }

    [Fact]
    public void Session_existence_entity_existence_and_logging_are_not_authorization()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IThingService _things;
    public TestController(IThingService things) { _things = things; }

    [HttpPost]
    public async Task<IActionResult> Save(int id)
    {
        if (HttpContext.Session.GetInt32("EmpId") == null) return Unauthorized();   // session exists
        if (id <= 0) return NotFound();                                            // entity exists
        Console.WriteLine("saving " + id);                                         // logging
        await _things.SaveAsync(id);
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
    }

    [Fact]
    public void An_ordinary_application_service_call_is_not_authorization()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IThingService _things;
    public TestController(IThingService things) { _things = things; }

    [HttpPost]
    public async Task<IActionResult> Save(int id) { await _things.SaveAsync(id); return Ok(); }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.Empty(endpoint.UnsupportedAuthorizationHelpers);
    }

    [Fact]
    public void An_unsupported_authorization_shaped_helper_is_reported_not_credited()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IThingService _things;
    public TestController(IThingService things) { _things = things; }

    [HttpPost]
    public async Task<IActionResult> Save(int id)
    {
        if (!await _things.AuthorizeThingAsync(id)) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.Contains("IThingService.AuthorizeThingAsync", endpoint.UnsupportedAuthorizationHelpers);
    }

    [Fact]
    public void A_permission_DTO_factory_is_not_reported_as_an_unsupported_helper()
    {
        // Found by dogfooding: PermissionTarget.ForSubjectEmployee produced the only CBA003 on the real tree, on
        // an endpoint that was ALREADY correctly credited. Matching the containing type's name made a data carrier
        // look like a check. Pinned so the heuristic cannot widen back.
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly ITasksAccessService _tasks;
    public TestController(ITasksAccessService tasks) { _tasks = tasks; }

    [HttpPost]
    public async Task<IActionResult> Save(int employeeId)
    {
        var target = new PermissionTarget { BranchId = employeeId };
        if (!await _tasks.CanAsync(new BusinessContext(), "edit", target)) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
        Assert.Empty(endpoint.UnsupportedAuthorizationHelpers);
    }

    [Fact]
    public void A_CanAsync_on_an_UNDECLARED_service_is_reported_as_unsupported()
    {
        // The forward-looking case: someone adds a ninth access service and does not add it to the declared
        // surface. Its endpoints would read as debt. CBA003 is how that becomes visible rather than silent.
        var endpoint = Single("""
public interface IFutureAccessService { Task<bool> CanAsync(BusinessContext c, string a); }

public class TestController : Controller
{
    private readonly IFutureAccessService _future;
    public TestController(IFutureAccessService future) { _future = future; }

    [HttpPost]
    public async Task<IActionResult> Save()
    {
        if (!await _future.CanAsync(new BusinessContext(), "edit")) return Forbid();
        return Ok();
    }
}
""", "Save");

        Assert.Equal(AuthorizationKind.None, endpoint.Authorization);
        Assert.Contains("IFutureAccessService.CanAsync", endpoint.UnsupportedAuthorizationHelpers);
    }

    // -----------------------------------------------------------------------------------------------------
    // discovery
    // -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("HttpPost", true)]
    [InlineData("HttpPut", true)]
    [InlineData("HttpDelete", true)]
    [InlineData("HttpPatch", true)]
    [InlineData("HttpGet", false)]
    [InlineData("HttpHead", false)]
    public void The_verb_decides_whether_an_endpoint_is_mutating(string verb, bool mutating)
    {
        var inventory = Inventory($$"""
public class TestController : Controller
{
    [{{verb}}] public IActionResult Act() => Ok();
}
""");

        Assert.Equal(mutating ? 1 : 0, inventory.MutatingCount);
    }

    [Fact]
    public void An_un_attributed_action_is_GET_and_therefore_not_mutating()
    {
        Assert.Equal(0, Inventory("""
public class TestController : Controller
{
    public IActionResult Index() => Ok();
}
""").MutatingCount);
    }

    [Fact]
    public void AcceptVerbs_is_supported_in_both_the_string_and_the_enum_form()
    {
        // Applied nowhere today. Unsupported, it would silently classify a mutating action as GET and remove it
        // from the measured surface altogether.
        var inventory = Inventory("""
public class TestController : Controller
{
    [AcceptVerbs("POST", "PUT")] public IActionResult A() => Ok();
    [AcceptVerbs("GET")] public IActionResult B() => Ok();
}
""");

        Assert.Equal(1, inventory.MutatingCount);
        Assert.Equal("POST|PUT", inventory.Endpoints.Single().HttpMethods);
    }

    [Fact]
    public void An_expression_bodied_action_is_discovered()
    {
        var endpoint = Single("""
public class TestController : Controller
{
    private readonly IAccountingAccessService _access;
    public TestController(IAccountingAccessService access) { _access = access; }

    [HttpPost]
    public async Task<IActionResult> Save() => await _access.CanAsync("post") ? Ok() : Forbid();
}
""", "Save");

        Assert.True(endpoint.IsMutating);
        Assert.Equal(AuthorizationKind.InBody, endpoint.Authorization);
    }

    [Fact]
    public void NonAction_static_and_lifecycle_members_are_not_endpoints()
    {
        var inventory = Inventory("""
public class TestController : Controller
{
    [HttpPost][NonAction] public IActionResult NotAnEndpoint() => Ok();
    [HttpPost] public static IActionResult Helper() => new OkResult();
    [HttpPost] public IActionResult Real() => Ok();
    public override void OnActionExecuting(Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext c) { }
}
""");

        Assert.Equal(1, inventory.MutatingCount);
        Assert.Equal("Real", inventory.Endpoints.Single().Action);
    }

    [Fact]
    public void A_controller_outside_the_Controllers_folder_is_outside_the_measured_scope()
    {
        // A declared limitation, tested so it stays declared rather than becoming folklore. Widening the scope
        // would change 388 without changing any code.
        var compilation = AnalyzerHarness.CompileOutsideControllers(Surface.Preamble + """
public class TestController : Controller
{
    [HttpPost] public IActionResult Save() => Ok();
}
""" + "\n" + Surface.Source);

        AnalyzerHarness.AssertCompiles(compilation);
        Assert.Equal(0, AuthorizationInventory.Build(compilation, CancellationToken.None).MutatingCount);
    }

    [Fact]
    public void An_api_controller_is_identified_as_an_api()
    {
        var endpoint = Single("""
[ApiController]
public class TestController : ControllerBase
{
    [HttpPost] public IActionResult Save() => Ok();
}
""", "Save");

        Assert.True(endpoint.IsApi);
    }
}
