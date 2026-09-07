using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossBuy.BL;
using CrossBuy.BL.ModulePermissions;
using CrossBuy.BL.Platform;
using Xunit;

namespace CrossBuy.Tests
{
    // Pins the permission-vocabulary decoupling.
    //
    // WHY, measured rather than asserted: a kernel-only candidate built against HEAD failed because
    // PlatformGrantWriter named module constants directly, so the Platform Kernel could not be committed
    // to Git without dragging five module access services owned by other workstreams. The dependency is
    // now inverted — modules publish a vocabulary, the kernel resolves it — and these tests exist so it
    // cannot silently invert back. The last group greps the kernel source itself, because that is the
    // property that actually blocked the commit; a behavioural test alone would not have caught it.
    public class PlatformPermissionVocabularyTests
    {
        private static PlatformPermissionVocabularyRegistry Production() =>
            new(new IPlatformPermissionVocabulary[]
            {
                new HrPermissionVocabulary(),
                new ProjectsPermissionVocabulary(),
                new TasksPermissionVocabulary(),
                new CommunicationPermissionVocabulary(),
            });

        // ---- 1-5: every administrable module still resolves, with its OWN published values ----------

        [Fact]
        public void Tasks_roles_and_manage_action_resolve_from_the_modules_own_vocabulary()
        {
            var r = Production();
            Assert.NotEmpty(TasksRoles.All);
            foreach (var role in TasksRoles.All) Assert.True(r.IsKnownRole(EntityRegistry.ScopeTasks, role));
            Assert.Equal(TasksActions.Manage, r.ManageActionFor(EntityRegistry.ScopeTasks));
        }

        [Fact]
        public void Hr_roles_and_manage_action_resolve()
        {
            var r = Production();
            foreach (var role in HrRoles.All) Assert.True(r.IsKnownRole(EntityRegistry.ScopeHr, role));
            // Named, not guessed: HR also publishes attendance-manage, which a "contains manage" heuristic
            // would have picked instead.
            Assert.Equal(HrActions.OrganizationManage, r.ManageActionFor(EntityRegistry.ScopeHr));
        }

        [Fact]
        public void Projects_roles_and_manage_action_resolve()
        {
            var r = Production();
            foreach (var role in ProjectsRoles.All) Assert.True(r.IsKnownRole(EntityRegistry.ScopeProjects, role));
            Assert.Equal(ProjectsActions.Manage, r.ManageActionFor(EntityRegistry.ScopeProjects));
        }

        [Fact]
        public void Communication_roles_and_manage_action_resolve()
        {
            var r = Production();
            foreach (var role in CommunicationRoles.All) Assert.True(r.IsKnownRole(EntityRegistry.ScopeCommunication, role));
            Assert.Equal(CommunicationActions.ManageGroup, r.ManageActionFor(EntityRegistry.ScopeCommunication));
        }

        [Fact]
        public void Calendar_publishes_no_grant_vocabulary_and_is_therefore_not_administrable()
        {
            // Calendar was never in the grant writer's role map, and this records that deliberately rather
            // than leaving it as an accident of omission.
            var r = Production();
            Assert.Null(r.ManageActionFor(EntityRegistry.ScopeCalendar));
            Assert.DoesNotContain(EntityRegistry.ScopeCalendar, r.AdministrableScopes);
        }

        // ---- 6-8: unknown values fail closed --------------------------------------------------------

        [Fact]
        public void An_unknown_module_scope_is_denied()
        {
            var r = Production();
            Assert.False(r.IsKnownRole("not-a-scope", "anything"));
            Assert.Null(r.ManageActionFor("not-a-scope"));
            Assert.Equal("(none registered)", r.KnownRoles("not-a-scope"));
        }

        [Fact]
        public void An_unknown_role_is_denied_even_in_a_known_scope()
            => Assert.False(Production().IsKnownRole(EntityRegistry.ScopeTasks, "emperor"));

        [Fact]
        public void A_role_is_matched_ordinally_so_case_is_not_a_grant()
        {
            var r = Production();
            var role = TasksRoles.All.First();
            Assert.True(r.IsKnownRole(EntityRegistry.ScopeTasks, role));
            Assert.False(r.IsKnownRole(EntityRegistry.ScopeTasks, role.ToUpperInvariant() + "X"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Blank_scope_or_role_is_denied(string? blank)
        {
            var r = Production();
            Assert.False(r.IsKnownRole(blank!, "x"));
            Assert.False(r.IsKnownRole(EntityRegistry.ScopeTasks, blank!));
            Assert.Null(r.ManageActionFor(blank!));
        }

        // ---- 9-10: registration conflicts are STARTUP failures --------------------------------------

        private sealed class Fake : IPlatformPermissionVocabulary
        {
            public Fake(string scope, IReadOnlyList<string> roles, string? manage = "manage")
            { Scope = scope; Roles = roles; ManageAction = manage; }
            public string Scope { get; }
            public IReadOnlyList<string> Roles { get; }
            public string? ManageAction { get; }
        }

        [Fact]
        public void Two_vocabularies_claiming_the_same_scope_refuse_to_start()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new PlatformPermissionVocabularyRegistry(
                new IPlatformPermissionVocabulary[] { new Fake("dup", new[] { "a" }), new Fake("dup", new[] { "b" }) }));
            Assert.Contains("dup", ex.Message);
        }

        [Fact]
        public void A_duplicate_role_inside_one_scope_refuses_to_start()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => new PlatformPermissionVocabularyRegistry(
                new IPlatformPermissionVocabulary[] { new Fake("s", new[] { "a", "a" }) }));
            Assert.Contains("twice", ex.Message);
        }

        [Fact]
        public void A_blank_scope_or_blank_role_refuses_to_start()
        {
            Assert.Throws<InvalidOperationException>(() => new PlatformPermissionVocabularyRegistry(
                new IPlatformPermissionVocabulary[] { new Fake("  ", new[] { "a" }) }));
            Assert.Throws<InvalidOperationException>(() => new PlatformPermissionVocabularyRegistry(
                new IPlatformPermissionVocabulary[] { new Fake("s", new[] { "" }) }));
        }

        [Fact]
        public void A_scope_with_no_manage_action_is_not_administrable()
        {
            var r = new PlatformPermissionVocabularyRegistry(
                new IPlatformPermissionVocabulary[] { new Fake("s", new[] { "a" }, manage: null) });
            Assert.DoesNotContain("s", r.AdministrableScopes);
            Assert.True(r.IsKnownRole("s", "a"));   // still a known role — just not grantable here
        }

        // ---- 16-17: behaviour preserved -------------------------------------------------------------

        [Fact]
        public void The_administrable_scopes_are_exactly_the_four_the_grant_writer_had_before()
        {
            // Before the refactor PlatformGrantWriter.RolesByScope contained exactly Hr, Projects, Tasks
            // and Communication. Same four, now resolved instead of hard-coded.
            Assert.Equal(
                new[] { EntityRegistry.ScopeCommunication, EntityRegistry.ScopeHr, EntityRegistry.ScopeProjects, EntityRegistry.ScopeTasks }.OrderBy(x => x, StringComparer.Ordinal),
                Production().AdministrableScopes.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void KnownRoles_still_lists_the_modules_own_roles_for_error_messages()
        {
            var listed = Production().KnownRoles(EntityRegistry.ScopeTasks);
            foreach (var role in TasksRoles.All) Assert.Contains(role, listed);
        }

        // ---- 11-15: the property that actually blocked the Git commit -------------------------------
        //
        // Source-level guards. A behavioural test cannot catch someone re-adding `TasksRoles.All` to a
        // kernel file, and that single line is what made the kernel un-committable.

        private static string KernelRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "CrossBuy", "BL", "Platform")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return Path.Combine(dir!.FullName, "CrossBuy");
        }

        private static IEnumerable<string> KernelSourceFiles()
        {
            var root = KernelRoot();
            foreach (var dir in new[] { Path.Combine(root, "BL", "Platform"),
                                        Path.Combine(root, "Models", "Platform"),
                                        Path.Combine(root, "Models", "Context", "Platform") })
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
                {
                    // BL/Platform/Ai is a separate workstream's surface, not the kernel foundation.
                    if (f.Replace('\\', '/').Contains("/Platform/Ai/", StringComparison.OrdinalIgnoreCase)) continue;
                    yield return f;
                }
            }
        }

        [Theory]
        [InlineData("TasksRoles")]
        [InlineData("TasksActions")]
        [InlineData("CalendarActions")]
        [InlineData("CommunicationRoles")]
        [InlineData("CommunicationActions")]
        [InlineData("HrRoles")]
        [InlineData("HrActions")]
        [InlineData("ProjectsRoles")]
        [InlineData("ProjectsActions")]
        public void No_kernel_source_file_names_a_module_permission_vocabulary(string moduleSymbol)
        {
            var offenders = KernelSourceFiles()
                .Where(f => File.ReadAllText(f).Contains(moduleSymbol, StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToArray();

            Assert.True(offenders.Length == 0,
                $"Platform Kernel source must not name '{moduleSymbol}'. Modules publish an " +
                $"IPlatformPermissionVocabulary instead. Offending file(s): {string.Join(", ", offenders)}");
        }

        [Fact]
        public void The_kernel_source_tree_was_actually_found_so_the_guard_is_not_vacuous()
        {
            // A guard that silently scans zero files passes forever. This is the falsifier for the theory above.
            var files = KernelSourceFiles().ToArray();
            Assert.True(files.Length > 30, $"expected the kernel source tree, found {files.Length} file(s)");
            Assert.Contains(files, f => Path.GetFileName(f) == "PlatformGrantWriter.cs");
            Assert.Contains(files, f => Path.GetFileName(f) == "PlatformPermissionProvider.cs");
        }
    }
}
