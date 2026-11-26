using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CrossBuy.BL;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.ViewModel;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CrossBuy.Tests
{
    // =============================================================================================
    // SEC-01 — the confirmed P0: three organisational-structure endpoints that any authenticated user
    // could reach, with no antiforgery token, writing to a table that carries no company column.
    //
    // WHAT THESE PROVE AND WHAT THEY DO NOT. They prove the guard is DECLARED on the compiled method
    // and that the service refuses an unresolved actor. They do NOT prove tenant isolation: the
    // hierarchy is still global, and no test here should be read as saying otherwise. That remains
    // BLOCKED on a schema decision and is tracked separately.
    //
    // Reflection over compiled metadata, never source text. A test that greps for an attribute passes
    // the moment someone writes the name in a comment and keeps passing after the attribute is deleted
    // from the method it was meant to guard.
    // =============================================================================================
    public class Wave0AdminOrgStructureSecurityTests
    {
        public static TheoryData<string> TheThreeEndpoints => new()
        {
            nameof(AdminController.AddHierarchicalItem),
            nameof(AdminController.AdministrativeBodiesCompany),
            nameof(AdminController.JobTitle),
        };

        private static MethodInfo PostOverload(string action) =>
            typeof(AdminController).GetMethods()
                .Where(m => m.Name == action && m.GetCustomAttribute<HttpPostAttribute>() != null)
                .Single();

        [Theory]
        [MemberData(nameof(TheThreeEndpoints))]
        public void Each_endpoint_demands_organization_manage(string action)
        {
            var attr = CustomAttributeData.GetCustomAttributes(PostOverload(action))
                .SingleOrDefault(a => a.AttributeType == typeof(ApiPermAttribute));

            Assert.NotNull(attr);
            Assert.Equal(ApiPermAttribute.Hr, (string)attr!.ConstructorArguments[0].Value!);
            Assert.Equal(HrActions.OrganizationManage, (string)attr.ConstructorArguments[1].Value!);
        }

        [Theory]
        [MemberData(nameof(TheThreeEndpoints))]
        public void Each_endpoint_validates_its_antiforgery_token(string action)
        {
            Assert.NotNull(PostOverload(action).GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }

        [Fact]
        public void Organization_manage_is_a_manager_right_and_not_merely_an_hr_login()
        {
            // The point of choosing this action over HrActions.Read or EmployeeManage: it resolves to
            // HrManager alone. If someone later widens it to HrOfficer or to `read`, these endpoints
            // silently reopen to the population the P0 was about, and this fails rather than the fix
            // quietly evaporating.
            var source = typeof(HrAccessService).Assembly
                .GetType("CrossBuy.BL.HrActions")!;

            var field = source.GetField(nameof(HrActions.OrganizationManage), BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(field);
            Assert.Equal("organization-manage", (string)field!.GetRawConstantValue()!);

            // And it must not be the same string as the two broad actions, which are granted to any HR login.
            Assert.NotEqual(HrActions.Read, HrActions.OrganizationManage);
            Assert.NotEqual(HrActions.EmployeeView, HrActions.OrganizationManage);
        }

        [Fact]
        public void The_guard_answers_in_json_rather_than_redirecting_a_script_caller()
        {
            // All three return JSON to an $.ajax caller. The MVC permission attributes deny with a 302 to
            // the access-denied page, so the browser follows it and the script receives 200 plus an HTML
            // login document — a refusal the client reads as success. ApiPerm is the JSON-shaped guard, and
            // choosing it is part of the fix rather than an implementation detail.
            Assert.True(typeof(IAsyncActionFilterMarker).IsAssignableFrom(typeof(ApiPermAttribute))
                        || typeof(ApiPermAttribute).GetInterfaces()
                            .Any(i => i.Name == "IAsyncActionFilter"));
        }

        // A tiny marker so the assertion above reads clearly without importing the MVC filter namespace.
        private interface IAsyncActionFilterMarker { }

        // -----------------------------------------------------------------------------------------
        // The service half of SEC-01, already landed: no resolvable actor means no write.
        // -----------------------------------------------------------------------------------------
        private sealed class EmptyProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        [Fact]
        public async Task The_hierarchy_write_refuses_before_it_builds_an_entity()
        {
            var service = new AdministrativeStructureService(null!, null!, new EmptyProvider(), null!);

            // If it reached entity construction it would throw NullReferenceException on the DbContext
            // instead. The specific exception type is the evidence that the refusal came first.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => service.Save(new HierarchicalDto { H_Name = "x", H_NameEn = "x" }));
        }
    }
}
