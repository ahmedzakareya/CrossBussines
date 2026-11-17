using CrossBuy.BL.Platform;
using CrossBuy.Controllers;
using CrossBuy.Models;
using CrossBuy.Models.Context;
using CrossBuy.Models.Context.Hospital;
using CrossBuy.Models.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H166: a desk action sent twice is done once. What must never happen:
	//   · a payment, a booking or a message done twice because the same request arrived twice;
	//   · a key reused for another action or by another person replaying someone else's answer;
	//   · a request that failed with an exception blocking its own retry;
	//   · the desk's money and booking actions left without the guard.
	// ==========================================================================================
	public class HospitalIdempotentTests
	{
		private static BusinessContext Ctx(int employeeId = 7) => new() { CompanyId = 1, EmployeeId = employeeId, UserId = "u" + employeeId, Roles = Array.Empty<string>(), CorrelationId = Guid.NewGuid() };

		private sealed class Fixed : IBusinessContextAccessor
		{
			private readonly BusinessContext? _c; public Fixed(BusinessContext? c) => _c = c;
			public Task<BusinessContext> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c!);
			public Task<BusinessContext?> TryGetCurrentAsync(CancellationToken ct = default) => Task.FromResult(_c);
		}

		/// Runs the filter once around an action that counts its runs; returns the result the caller got.
		private static async Task<IActionResult?> Send(CrossDbContext db, BusinessContext ctx, string? key, string path, Func<IActionResult> action, Action ran, bool throws = false)
		{
			var services = new ServiceCollection().AddSingleton<IBusinessContextAccessor>(new Fixed(ctx)).AddSingleton(db).BuildServiceProvider();
			var http = new DefaultHttpContext { RequestServices = services };
			http.Request.Path = path; http.Request.Method = "POST";
			if (key != null) http.Request.Headers[HospitalIdempotentAttribute.Header] = key;
			var ac = new ActionContext(http, new RouteData(), new ActionDescriptor());
			var exec = new ActionExecutingContext(ac, new List<IFilterMetadata>(), new Dictionary<string, object?>(), new object());
			await new HospitalIdempotentAttribute().OnActionExecutionAsync(exec, () =>
			{
				ran();
				var done = new ActionExecutedContext(ac, new List<IFilterMetadata>(), new object());
				if (throws) done.Exception = new InvalidOperationException("boom"); else done.Result = action();
				return Task.FromResult(done);
			});
			return exec.Result;   // null: the action's own result went through
		}

		[Fact]
		public async Task The_same_key_runs_once_and_gets_the_first_answer_back()
		{
			using var host = new PlatformTestHost();
			int runs = 0;
			IActionResult Pay() => new JsonResult(new { ok = true, id = 41 });
			Assert.Null(await Send(host.Db, Ctx(), "k-1", "/Hospital/FrontDesk/Bill/9/Collect", Pay, () => runs++));
			var again = Assert.IsType<ContentResult>(await Send(host.Db, Ctx(), "k-1", "/Hospital/FrontDesk/Bill/9/Collect", Pay, () => runs++));
			Assert.Equal(1, runs);
			Assert.Equal("{\"ok\":true,\"id\":41}", again.Content);
			Assert.Null(await Send(host.Db, Ctx(), null, "/Hospital/FrontDesk/Bill/9/Collect", Pay, () => runs++));   // no key: runs as always
			Assert.Equal(2, runs);
		}

		[Fact]
		public async Task A_key_is_not_replayed_for_another_action_or_person_and_an_exception_frees_it()
		{
			using var host = new PlatformTestHost();
			int runs = 0;
			IActionResult Ok() => new JsonResult(new { ok = true });
			await Send(host.Db, Ctx(7), "k-2", "/Hospital/FrontDesk/Bill/9/Collect", Ok, () => runs++);
			Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(await Send(host.Db, Ctx(7), "k-2", "/Hospital/FrontDesk/Patient/9/Deposit", Ok, () => runs++)).StatusCode);
			Assert.Equal(StatusCodes.Status409Conflict, Assert.IsType<ObjectResult>(await Send(host.Db, Ctx(8), "k-2", "/Hospital/FrontDesk/Bill/9/Collect", Ok, () => runs++)).StatusCode);
			Assert.Equal(1, runs);

			await Send(host.Db, Ctx(7), "k-3", "/Hospital/FrontDesk/WalkIn", Ok, () => runs++, throws: true);
			Assert.False(await host.Db.Set<HospitalRequestKey>().AnyAsync(k => k.RequestKey == "k-3"));   // freed
			Assert.Null(await Send(host.Db, Ctx(7), "k-3", "/Hospital/FrontDesk/WalkIn", Ok, () => runs++));
			Assert.Equal(3, runs);
		}

		[Theory]
		[InlineData("FrontDeskCollect")]
		[InlineData("FrontDeskTakeDeposit")]
		[InlineData("FrontDeskUseDeposit")]
		[InlineData("FrontDeskRefundDeposit")]
		[InlineData("FrontDeskWalkIn")]
		[InlineData("FrontDeskSendHandoff")]
		[InlineData("FrontDeskCloseCheckout")]
		[InlineData("ClinicDisruptionApply")]
		[InlineData("FrontDeskMessage")]
		[InlineData("FrontDeskHandOver")]
		public void The_desks_money_booking_and_send_actions_carry_the_guard(string action)
			=> Assert.NotNull(typeof(HospitalController).GetMethods().First(m => m.Name == action).GetCustomAttributes(typeof(HospitalIdempotentAttribute), false).FirstOrDefault());

		[Fact]
		public void The_table_is_read_in_the_shape_its_slice_creates()
		{
			var model = HospitalSliceParity.Model(HospitalPharmacyModel.Configure);
			HospitalSliceParity.NamesItsTable(model, HospitalSliceParity.Slice("hospital_h166_request_keys.sql"), typeof(HospitalRequestKey), "HospitalRequestKeys");
			HospitalSliceParity.ColumnsMatch(model, HospitalSliceParity.Slice("hospital_h166_request_keys.sql"), typeof(HospitalRequestKey), "HospitalRequestKeys");
		}
	}
}
