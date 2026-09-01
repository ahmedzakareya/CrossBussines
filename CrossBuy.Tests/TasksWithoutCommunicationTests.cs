using System.Reflection;
using CrossBuy.Controllers;
using CrossBuy.Models.Menu;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests;

/// <summary>
/// The Communication Platform is OPTIONAL. Tasks must work without it.
///
/// WHAT HAPPENED, AND WHY NOTHING CAUGHT IT
///
/// AddCommunicationPlatform is deliberately not called in Program.cs - activating it is a separate
/// platform decision. TasksController nevertheless took ICommThreadService and ICommCommentService as
/// CONSTRUCTOR parameters, so DI could not build the controller at all:
///
///     System.InvalidOperationException: Unable to resolve service for type
///     'CrossBuy.BL.Communication.ICommThreadService' while attempting to activate
///     'CrossBuy.Controllers.TasksController'.
///
/// All eight Tasks screens returned HTTP 500 while only the task discussion needed Communication.
///
/// The suite was green throughout. CommunicationDiWiringTests builds its OWN ServiceCollection and calls
/// AddCommunicationPlatform on it, so it proves the platform's registrations are coherent - a true and
/// useful claim, and a different one from "the application can construct its controllers". That is the
/// rule CLAUDE.md already records: a DI graph is not verified by tests that construct services by hand.
///
/// So these tests assert the shape of the DEPENDENCY, which is where the defect lives and which needs no
/// container: a controller may not REQUIRE a service that the application never registers. That invariant
/// is checked across every controller, not just this one, because the next occurrence will be elsewhere.
///
/// What these tests deliberately do NOT claim: that a live request returns 200. No in-process host exists
/// in this project (no Microsoft.AspNetCore.Mvc.Testing), and adding one would boot the real application
/// against the real database inside the unit suite. Live HTTP verification is run separately and recorded
/// in the closure report.
/// </summary>
public sealed class TasksWithoutCommunicationTests
{
    private static readonly Assembly App = typeof(MainMenu).Assembly;

    /// The namespace that AddCommunicationPlatform - and only AddCommunicationPlatform - populates.
    private const string CommunicationServiceNamespace = "CrossBuy.BL.Communication";

    /// The eight screens the audit found returning 500. Named explicitly: a discovery sweep would have
    /// silently shrunk to zero if the folder ever moved, and passed.
    private static readonly string[] TaskScreens =
    {
        "Index", "All", "Board", "Reports", "HoursReport", "AutoRules", "MatchSuggestions", "Templates",
    };

    // =============================================================================================
    // 1 - the dependency itself
    // =============================================================================================

    [Fact]
    public void TasksController_requires_no_Communication_service_to_be_constructed()
    {
        var required = typeof(TasksController)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => IsCommunicationService(p.ParameterType))
            .Select(p => p.ParameterType.Name)
            .Distinct()
            .ToList();

        Assert.True(required.Count == 0,
            "TasksController takes " + string.Join(", ", required) + " as a mandatory constructor " +
            "dependency. The Communication Platform is not registered, so DI cannot build the controller " +
            "and every Tasks screen returns HTTP 500. Resolve it at the point of use instead, as " +
            "WorkspaceService does.");
    }

    /// The same defect in any other controller would be just as fatal, so the invariant is product-wide.
    /// It is stated CONDITIONALLY: if the platform is ever activated in Program.cs, a constructor
    /// dependency becomes legitimate and this test steps aside rather than blocking the activation.
    [Fact]
    public void No_controller_requires_the_Communication_Platform_while_it_is_inactive()
    {
        if (CommunicationPlatformIsActivated()) return;

        var offenders = new List<string>();

        foreach (var controller in App.GetTypes().Where(IsController))
        {
            var required = controller.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Where(p => IsCommunicationService(p.ParameterType))
                .Select(p => p.ParameterType.Name)
                .Distinct()
                .ToList();

            if (required.Count > 0)
            {
                offenders.Add(controller.Name + " requires " + string.Join(", ", required));
            }
        }

        Assert.True(offenders.Count == 0,
            "AddCommunicationPlatform is not called in Program.cs, so these controllers cannot be " +
            "constructed and every screen they serve returns HTTP 500:" +
            Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }

    /// Guards the guard. If the reflection sweep stopped matching controllers - a rename, a moved
    /// assembly - the test above would pass by examining nothing at all.
    [Fact]
    public void Controller_discovery_is_not_vacuous()
    {
        var controllers = App.GetTypes().Where(IsController).ToList();

        Assert.True(controllers.Count > 20,
            "Only " + controllers.Count + " controllers discovered - the sweep is not finding the product, " +
            "so every controller-wide assertion in this file is passing vacuously.");
        Assert.Contains(controllers, c => c == typeof(TasksController));
    }

    // =============================================================================================
    // 2 - the eight screens
    // =============================================================================================

    [Fact]
    public void All_eight_Tasks_screens_are_public_GET_actions()
    {
        var missing = new List<string>();

        foreach (var screen in TaskScreens)
        {
            var action = typeof(TasksController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == screen && !m.IsSpecialName);

            if (action == null) { missing.Add(screen + ": no such action"); continue; }

            // A sidebar link issues a GET. A screen behind [HttpPost] reads to the user as broken.
            if (action.GetCustomAttribute<HttpPostAttribute>() != null) { missing.Add(screen + ": is [HttpPost]"); }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    /// The screens must be reachable by URL as well as by menu link. /Tasks and /Calendar returned 404
    /// because the default route defaults the ACTION to "Login" - the same defect already fixed for
    /// /Workspace, /Reports and /BusinessEventMonitor.
    [Fact]
    public void The_bare_Tasks_and_Calendar_routes_are_registered()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

        foreach (var name in new[] { "Tasks", "Calendar" })
        {
            Assert.True(
                program.Contains("pattern: \"" + name + "\"", StringComparison.Ordinal),
                "no route maps the bare \"/" + name + "\" to " + name + "Controller.Index, so a hand-typed " +
                "or shared bare URL 404s while /" + name + "/Index works.");
        }
    }

    // =============================================================================================
    // 3 - explicit degradation, and the permission boundary it must not disturb
    // =============================================================================================

    /// Both Communication-backed endpoints must answer with the machine code rather than throwing. The
    /// assertion is on the SOURCE because the alternative - invoking the action - requires a company
    /// resolver, a business context and a database, none of which is what is being tested here.
    [Fact]
    public void Both_Communication_backed_endpoints_report_Unavailable_instead_of_failing()
    {
        var source = TasksControllerSource();

        foreach (var action in new[] { "TaskComments", "TaskCommentAdd" })
        {
            var body = ActionBody(source, action);
            Assert.True(body.Contains("TryCommunication()", StringComparison.Ordinal),
                action + " does not ask whether the Communication Platform is present.");
            Assert.True(body.Contains("CommunicationUnavailable()", StringComparison.Ordinal),
                action + " does not return the explicit unavailable result when the platform is absent.");
        }

        Assert.Equal("communication_unavailable", TasksController.CommunicationUnavailableCode);
    }

    /// The availability check must never precede the permission gate: a caller with no right to the task
    /// must be refused, not told which platforms the deployment runs.
    [Fact]
    public void The_permission_gate_runs_before_the_availability_check()
    {
        var source = TasksControllerSource();

        foreach (var action in new[] { "TaskComments", "TaskCommentAdd" })
        {
            var body = ActionBody(source, action);
            var gate = body.IndexOf("TaskGateAsync(", StringComparison.Ordinal);
            var availability = body.IndexOf("TryCommunication()", StringComparison.Ordinal);

            Assert.True(gate >= 0, action + " no longer consults the permission gate at all.");
            Assert.True(gate < availability,
                action + " checks platform availability before checking permission, so an unauthorized " +
                "caller learns the deployment's platform state.");
        }
    }

    /// Core Tasks operations must not have been thinned out to make the screens load. The permission
    /// gates are the load-bearing part: removing Communication must not have removed authorization with it.
    [Fact]
    public void Core_Tasks_operations_and_their_permission_gates_are_intact()
    {
        var source = TasksControllerSource();

        // INVOCATIONS only. My first attempt counted "TaskGateAsync(" and "new TaskGate()" together and
        // arrived at 26, which is not the number of guarded actions - twelve of those are the refusal
        // RETURNS inside the two gate helpers. Sixteen actions ask permission: fourteen per-task, two
        // company-wide. A baseline that counts the wrong thing cannot notice the right thing changing.
        var gateCalls = CountOccurrences(source, "await TaskGateAsync(")
                      + CountOccurrences(source, "await TaskManageGateAsync()");
        Assert.True(gateCalls >= 16,
            "only " + gateCalls + " permission-gate invocations remain in TasksController (16 expected) - " +
            "authorization coverage has been reduced, not merely decoupled from Communication.");

        // The discussion is degraded, never deleted: the platform calls still exist for the deployment
        // that activates it.
        Assert.Contains("GetOrCreateAsync", source);
        Assert.Contains("AddAsync", source);
    }

    // =============================================================================================
    // 4 - no substitute platform
    // =============================================================================================

    /// The forbidden shortcut is a fake: registering a stub ICommThreadService so DI stops complaining.
    /// That would make an inactive platform look active and would silently swallow every comment.
    [Fact]
    public void No_substitute_Communication_implementation_is_registered()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"));

        var suspicious = new[] { "ICommThreadService", "ICommCommentService", "ICommMentionService" }
            .Where(t => program.Contains(t, StringComparison.Ordinal))
            .ToList();

        Assert.True(suspicious.Count == 0,
            "Program.cs registers " + string.Join(", ", suspicious) + " outside AddCommunicationPlatform. " +
            "A second implementation of a platform service is not an activation - it is a forgery that " +
            "reports success while storing nothing.");

        // The assertion that used to stand here refused activation, and left its own instruction:
        //   "If it was deliberate, delete this assertion in the same commit that made the decision."
        // It was deliberate. Communication was activated as a PLATFORM decision, not as a fix for
        // Tasks: schema slice 001 applied first, the DI graph audited (all Scoped, no hosted service),
        // ValidateOnBuild + ValidateScopes proven in CommunicationPlatformActivationTests, and the
        // business-event bridge deliberately left disconnected. The guard above still stands, and it is
        // the one that matters now: activation must come from AddCommunicationPlatform and never from a
        // substitute registration in Program.cs.
    }

    // =============================================================================================
    // helpers
    // =============================================================================================

    private static bool IsCommunicationService(Type t) =>
        t.Namespace != null
        && t.Namespace.StartsWith(CommunicationServiceNamespace, StringComparison.Ordinal)
        && t.IsInterface;

    private static bool IsController(Type t) =>
        !t.IsAbstract && typeof(Controller).IsAssignableFrom(t);

    private static bool CommunicationPlatformIsActivated() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Program.cs"))
            .Contains("AddCommunicationPlatform(", StringComparison.Ordinal);

    private static string TasksControllerSource() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "CrossBuy", "Controllers", "TasksController.cs"));

    /// Everything from an action's signature to the start of the next action. Crude, and sufficient:
    /// the two assertions are about ORDER and PRESENCE within one method body.
    private static string ActionBody(string source, string action)
    {
        var start = source.IndexOf("> " + action + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, "action " + action + " not found in TasksController.cs");

        var next = source.IndexOf("[Http", start, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0) { count++; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal); }
        return count;
    }

    private static string RepoRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("CROSSBUY_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)
            && File.Exists(Path.Combine(fromEnvironment, "CrossBuy.sln")))
        {
            return fromEnvironment;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
