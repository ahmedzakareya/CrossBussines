using CrossBuy.BL;
using CrossBuy.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace CrossBuy.Tests
{
	// =================================================================================================
	// CrmPermAttribute must refuse when it cannot ask.
	//
	// THE DEFECT, restated exactly. The filter used to read:
	//
	//     var acc = ...GetService(typeof(ICrmAccessService)) as ICrmAccessService;
	//     if (acc == null) { await next(); return; }
	//
	// Thirty-seven CRM endpoints carry this attribute. A container that had not registered
	// ICrmAccessService — a trimmed composition root, a registration lost in a merge, a service whose
	// own constructor threw during activation — turned all thirty-seven into unguarded endpoints. Not
	// one of them would have looked different from outside, and nothing was logged.
	//
	// These tests deliberately BREAK the dependency and prove the action never runs. The "next was not
	// invoked" assertion is the one that matters: a filter can set a Result and still let the action
	// execute if it forgets to return, and that mistake would leave the write happening behind a page
	// that says it did not.
	// =================================================================================================
	public class CrmPermFailClosedTests
	{
		// A provider that has never heard of ICrmAccessService — the missing-registration case.
		private sealed class EmptyProvider : IServiceProvider
		{
			public object? GetService(Type serviceType) => null;
		}

		// A provider that HAS the registration but blows up producing it — the failed-activation case.
		// GetService (not GetRequiredService) normally hides this behind null, but a factory registration
		// throws straight through, so the filter must survive it too.
		private sealed class ThrowingProvider : IServiceProvider
		{
			public object? GetService(Type serviceType) =>
				throw new InvalidOperationException("dependency graph is broken");
		}

		// An authority that resolves but cannot decide — a dropped database connection mid-request.
		private sealed class ThrowingAccess : ICrmAccessService
		{
			public int? CurrentEmployeeId() => 7;
			public Task<List<string>> MyRolesAsync() => throw new InvalidOperationException("db down");
			public Task<bool> CanAsync(string action) => throw new InvalidOperationException("db down");
			public Task<HashSet<int>?> VisibleOwnerIdsAsync() => throw new InvalidOperationException("db down");
			public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => throw new InvalidOperationException("db down");
			public Task<string> RoleLabelAsync(bool isAr) => throw new InvalidOperationException("db down");
		}

		private sealed class FixedAccess : ICrmAccessService
		{
			private readonly bool _allow;
			public FixedAccess(bool allow) { _allow = allow; }
			public int? CurrentEmployeeId() => 7;
			public Task<List<string>> MyRolesAsync() => Task.FromResult(new List<string> { "SalesManager" });
			public Task<bool> CanAsync(string action) => Task.FromResult(_allow);
			public Task<HashSet<int>?> VisibleOwnerIdsAsync() => Task.FromResult<HashSet<int>?>(null);
			public Task<HashSet<int>> TeamOwnerIdsAsync(int managerEmployeeId) => Task.FromResult(new HashSet<int>());
			public Task<string> RoleLabelAsync(bool isAr) => Task.FromResult("Sales manager");
		}

		private sealed class SingleAccess : IServiceProvider
		{
			private readonly ICrmAccessService _svc;
			public SingleAccess(ICrmAccessService svc) { _svc = svc; }
			public object? GetService(Type serviceType) =>
				serviceType == typeof(ICrmAccessService) ? _svc : null;
		}

		private static ActionExecutingContext Ctx(IServiceProvider services)
		{
			var http = new DefaultHttpContext { RequestServices = services };
			var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());
			return new ActionExecutingContext(
				actionContext,
				new List<IFilterMetadata>(),
				new Dictionary<string, object?>(),
				controller: null!);   // the filter only touches Controller to set TempData; null exercises that branch
		}

		private static async Task<(bool ranAction, IActionResult? result)> RunAsync(
			CrmPermAttribute filter, IServiceProvider services)
		{
			var ctx = Ctx(services);
			bool ran = false;

			await filter.OnActionExecutionAsync(ctx, () =>
			{
				ran = true;
				var executed = new ActionExecutedContext(ctx, new List<IFilterMetadata>(), controller: null!);
				return Task.FromResult(executed);
			});

			return (ran, ctx.Result);
		}

		[Fact]
		public async Task A_missing_access_service_does_not_execute_the_protected_action()
		{
			var (ran, result) = await RunAsync(new CrmPermAttribute("edit"), new EmptyProvider());

			Assert.False(ran);
			Assert.NotNull(result);
		}

		[Fact]
		public async Task A_missing_access_service_denies_rather_than_falling_through()
		{
			var (_, result) = await RunAsync(new CrmPermAttribute("manage"), new EmptyProvider());

			var redirect = Assert.IsType<RedirectToActionResult>(result);
			Assert.Equal("Index", redirect.ActionName);
			Assert.Equal("Crm", redirect.ControllerName);
		}

		[Fact]
		public async Task A_container_that_throws_while_resolving_the_authority_does_not_execute_the_action()
		{
			// GetService itself throwing must not escape as a 500 that some outer handler could turn into
			// a retry, and must certainly not run the action.
			var filter = new CrmPermAttribute("edit");
			var ctx = Ctx(new ThrowingProvider());
			bool ran = false;

			await Assert.ThrowsAnyAsync<Exception>(async () =>
				await filter.OnActionExecutionAsync(ctx, () =>
				{
					ran = true;
					return Task.FromResult(new ActionExecutedContext(ctx, new List<IFilterMetadata>(), controller: null!));
				}));

			Assert.False(ran);
		}

		[Fact]
		public async Task An_authority_that_throws_while_deciding_does_not_execute_the_action()
		{
			var (ran, result) = await RunAsync(new CrmPermAttribute("edit"), new SingleAccess(new ThrowingAccess()));

			Assert.False(ran);
			Assert.IsType<RedirectToActionResult>(result);
		}

		[Fact]
		public async Task A_broken_dependency_is_indistinguishable_from_a_plain_refusal()
		{
			// If "the permission service is down" looked different from "you may not", a caller could
			// probe the installation's health through an endpoint they are not allowed to reach.
			var (_, denied) = await RunAsync(new CrmPermAttribute("edit"), new SingleAccess(new FixedAccess(false)));
			var (_, broken) = await RunAsync(new CrmPermAttribute("edit"), new EmptyProvider());
			var (_, threw) = await RunAsync(new CrmPermAttribute("edit"), new SingleAccess(new ThrowingAccess()));

			var a = Assert.IsType<RedirectToActionResult>(denied);
			var b = Assert.IsType<RedirectToActionResult>(broken);
			var c = Assert.IsType<RedirectToActionResult>(threw);

			Assert.Equal(a.ActionName, b.ActionName);
			Assert.Equal(a.ActionName, c.ActionName);
			Assert.Equal(a.ControllerName, b.ControllerName);
			Assert.Equal(a.ControllerName, c.ControllerName);
		}

		[Fact]
		public async Task A_permitted_action_still_runs()
		{
			// The repair must not have closed the gate on everybody.
			var (ran, result) = await RunAsync(new CrmPermAttribute("edit"), new SingleAccess(new FixedAccess(true)));

			Assert.True(ran);
			Assert.Null(result);
		}

		[Fact]
		public void The_filter_contains_no_fall_through_on_a_null_authority()
		{
			// Pinned in source as well as in behaviour: the old line was a single `await next()` guarded by
			// a null check, and it would read as a harmless defensive branch to anyone re-adding it.
			// Comments are stripped first. The file's own header quotes the defective line verbatim so a
			// future reader can see what was wrong; scanning the raw text would match that prose and turn
			// the explanation into a failure.
			var code = StripComments(SourceText("CrossBuy", "Models", "CrmPermAttribute.cs"));

			Assert.DoesNotContain("await next(); return;", code, StringComparison.Ordinal);
			Assert.Contains("if (acc == null) { Deny(context); return; }", code, StringComparison.Ordinal);
		}

		// Line comments only: this file has no block comments, and a regex for /* */ that got the string
		// literals wrong would quietly delete real code from the text under assertion.
		private static string StripComments(string code) => string.Join("\n",
			code.Split('\n').Select(line =>
			{
				int i = line.IndexOf("//", StringComparison.Ordinal);
				return i < 0 ? line : line.Substring(0, i);
			}));

		private static string SourceText(params string[] relativeParts)
		{
			var dir = new DirectoryInfo(AppContext.BaseDirectory);
			while (dir != null && !File.Exists(Path.Combine(dir.FullName, "CrossBuy.sln"))) dir = dir.Parent;
			Assert.True(dir != null, "could not locate the repository root from " + AppContext.BaseDirectory);
			var path = Path.Combine(new[] { dir!.FullName }.Concat(relativeParts).ToArray());
			Assert.True(File.Exists(path), "source file not found: " + path);
			return File.ReadAllText(path);
		}
	}
}
