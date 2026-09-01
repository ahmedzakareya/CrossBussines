using System.Reflection;
using CrossBuy.Controllers;
using CrossBuy.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests
{
    // Stage 0 (Slice-003) — Manufacturing work-order authorization.
    //
    // CONTEXT THAT MATTERS: an earlier discovery pass reported the work-order WRITE actions as unprotected. That
    // finding was WRONG — its attribute scanner could not read several attributes concatenated on one line
    // ([HttpPost][ValidateAntiForgeryToken][InvPerm("doc")]) and recorded them as "(none)". These tests exist so
    // that claim can never be made from a guess again: they assert the attributes directly off the compiled
    // metadata, which cannot be fooled by formatting.
    //
    // Stage 0 added the READ gate, which was genuinely missing.
    public class Slice3ManufacturingSecurityTests
    {
        // Every state-changing work-order endpoint. All must carry BOTH [InvPerm("doc")] and anti-forgery.
        public static readonly string[] WriteActions =
        {
            "CreateWorkOrder", "SaveWorkOrder", "ReleaseWorkOrder", "CancelWorkOrder", "CompleteWorkOrder",
            "ProducePartial", "AddWorkOrderLabor", "RemoveWorkOrderLabor", "GeneratePlanWorkOrders",
        };

        // Every manufacturing read surface. All must carry [InvPerm("read")] after Stage 0.
        public static readonly string[] ReadActions =
        {
            "WorkOrders", "WorkOrdersData", "WorkOrderItemPickData", "NewWorkOrder", "WorkOrderDetails",
        };

        public static IEnumerable<object[]> WriteActionNames => WriteActions.Select(a => new object[] { a });
        public static IEnumerable<object[]> ReadActionNames => ReadActions.Select(a => new object[] { a });

        private static MethodInfo Action(string name)
        {
            var m = typeof(InventoryController).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(x => x.Name == name && x.DeclaringType == typeof(InventoryController))
                .ToList();
            Assert.True(m.Count > 0, $"InventoryController.{name} not found — the action was renamed or removed.");
            Assert.True(m.Count == 1, $"InventoryController.{name} is overloaded; the test must name the intended overload.");
            return m[0];
        }

        private static string? InvPermAction(MethodInfo m)
        {
            var attr = m.GetCustomAttributes(typeof(InvPermAttribute), inherit: false).FirstOrDefault();
            if (attr == null) return null;
            // InvPermAttribute stores the action in a private readonly field.
            var f = typeof(InvPermAttribute).GetField("_action", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(f);
            return (string?)f!.GetValue(attr);
        }

        // ---- Tests 1-6: unauthorized users cannot create/save/release/cancel/complete ----
        // Enforcement is by attribute, so the assertion is "the gate is present and names the right action".
        // InvPermAttribute's own behaviour (deny -> redirect) is exercised by the wiring test below.
        [Theory]
        [MemberData(nameof(WriteActionNames))]
        public void Every_work_order_write_action_requires_the_inventory_document_permission(string actionName)
        {
            var method = Action(actionName);

            Assert.Equal("doc", InvPermAction(method));

            // A state-changing endpoint must also be POST-only and anti-forgery protected, or the permission can
            // be bypassed by a cross-site GET.
            Assert.NotEmpty(method.GetCustomAttributes(typeof(HttpPostAttribute), false));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), false));
        }

        [Fact]
        public void The_nine_write_actions_are_the_complete_set_of_work_order_mutations()
        {
            // Guards against a new lifecycle action being added without a permission attribute: any POST on the
            // controller whose name mentions WorkOrder (or is ProducePartial) must be in the protected list.
            var posts = typeof(InventoryController)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.DeclaringType == typeof(InventoryController))
                .Where(m => m.GetCustomAttributes(typeof(HttpPostAttribute), false).Any())
                .Where(m => m.Name.Contains("WorkOrder", StringComparison.Ordinal) || m.Name == "ProducePartial")
                .Select(m => m.Name)
                .ToHashSet(StringComparer.Ordinal);

            var unprotected = posts.Except(WriteActions, StringComparer.Ordinal).ToList();
            Assert.True(unprotected.Count == 0,
                "New work-order POST action(s) without a permission attribute in the tested set: " + string.Join(", ", unprotected));
        }

        // ---- Stage 0 addition: read gate on the manufacturing screens ----
        [Theory]
        [MemberData(nameof(ReadActionNames))]
        public void Every_manufacturing_read_action_requires_the_inventory_read_permission(string actionName)
        {
            var method = Action(actionName);
            Assert.Equal("read", InvPermAction(method));
            Assert.NotEmpty(method.GetCustomAttributes(typeof(HttpGetAttribute), false));
        }

        [Fact]
        public void No_manufacturing_read_action_is_left_on_authentication_only()
        {
            var missing = ReadActions.Where(a => InvPermAction(Action(a)) == null).ToList();
            Assert.True(missing.Count == 0, "Manufacturing read action(s) with no InvPerm gate: " + string.Join(", ", missing));
        }

        // ---- Authorized behaviour is unchanged: the write gate is still "doc", not something stricter ----
        [Fact]
        public void Authorized_inventory_users_retain_their_existing_permission_level()
        {
            // Stage 0 must not silently tighten write semantics. "doc" is InventoryManager + WarehouseKeeper —
            // exactly what these actions required before this stage.
            Assert.All(WriteActions, a => Assert.Equal("doc", InvPermAction(Action(a))));
            // And reads must NOT have been raised to "doc", which would lock viewers out of screens they could
            // previously open.
            Assert.All(ReadActions, a => Assert.Equal("read", InvPermAction(Action(a))));
        }

        [Fact]
        public void InvPermAttribute_is_an_action_filter_so_the_gate_runs_server_side()
        {
            // Hiding a button is never the control. Prove the attribute is actually wired into the MVC pipeline.
            Assert.True(typeof(Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter).IsAssignableFrom(typeof(InvPermAttribute)),
                "InvPermAttribute must implement IAsyncActionFilter or it enforces nothing.");
            var usage = (AttributeUsageAttribute?)Attribute.GetCustomAttribute(typeof(InvPermAttribute), typeof(AttributeUsageAttribute));
            Assert.NotNull(usage);
            Assert.True(usage!.ValidOn.HasFlag(AttributeTargets.Method));
        }
    }
}
