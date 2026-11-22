using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.BL.Platform;
using CrossBuy.Models.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H225 — the Ministry of Health gateway. What must never happen: a reference made up from an answer that
	// carries none (or from an error page), a hospital with no gateway configured believing it is connected, or the
	// table drifting from its slice.
	// ==========================================================================================
	public class HospitalMohGatewayTests
	{
		[Theory]
		[InlineData("{\"reference\":\"MOH-2026-118\"}", "MOH-2026-118")]
		[InlineData("{\"referenceNo\":\" SL-77 \"}", "SL-77")]
		[InlineData("{\"id\":4512}", "4512")]
		public void The_reference_is_read_from_the_ministrys_answer(string body, string expected) =>
			Assert.Equal(expected, HospitalMohGatewayRules.ReferenceIn(body));

		[Theory]
		[InlineData(null)]
		[InlineData("")]
		[InlineData("<html>Bad gateway</html>")]
		[InlineData("{\"status\":\"ok\"}")]
		[InlineData("[\"MOH-1\"]")]
		[InlineData("{\"reference\":\"\"}")]
		public void An_answer_without_a_reference_gives_none(string? body) => Assert.Null(HospitalMohGatewayRules.ReferenceIn(body));

		[Fact]
		public void A_reference_longer_than_its_column_is_not_taken() =>
			Assert.Null(HospitalMohGatewayRules.ReferenceIn("{\"reference\":\"" + new string('X', 61) + "\"}"));

		[Theory]
		[InlineData("https://moh.example/api", null)]
		[InlineData("http://localhost:5080/moh", null)]
		[InlineData("http://moh.example/api", "The gateway address must be a full https address")]
		[InlineData("moh.example/api", "The gateway address must be a full https address")]
		[InlineData("  ", "Write the gateway address")]
		public void The_gateway_is_a_full_https_address(string url, string? refusal) => Assert.Equal(refusal, HospitalMohGatewayRules.UrlRefusal(url));

		// H229: the administrator keeps the connection; the key is stored protected, never shown, kept when left empty,
		// removed only when asked; switched off (or another company) there is no connection at all.
		[Fact]
		public async Task The_connection_is_the_administrators_and_its_key_is_never_in_the_clear()
		{
			using var host = new PlatformTestHost(companyId: 1);
			var access = new HospitalAccessService(new PlatformRoleDirectory(host.Db, NullLogger<PlatformRoleDirectory>.Instance), NullLogger<HospitalAccessService>.Instance);
			var dp = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
			var svc = new HospitalMohGatewayService(host.Db, access, null, dp);
			var ctx = new BusinessContext { CompanyId = 1, EmployeeId = 7, UserId = "u7", Roles = Array.Empty<string>() };
			Assert.Null(await svc.ConnectionAsync(1));
			Assert.False(await HospitalMohGatewayService.ConnectedAsync(host.Db, 1));
			Assert.True((await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput(), 7, DateTime.UtcNow)).Ok);   // off and empty: allowed
			Assert.Equal("The gateway address must be a full https address",
				(await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = "http://moh.example", IsEnabled = true }, 7, DateTime.UtcNow)).Error);

			Assert.True((await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = " https://moh.example/api ", Key = "s3cret-key", IsEnabled = true }, 7, DateTime.UtcNow)).Ok);
			var row = await host.Db.Set<HospitalMohSettings>().AsNoTracking().SingleAsync();
			Assert.DoesNotContain("s3cret-key", row.KeyProtected);
			Assert.Equal(new HospitalMohConnection("https://moh.example/api", "s3cret-key"), await svc.ConnectionAsync(1));
			Assert.True(await HospitalMohGatewayService.ConnectedAsync(host.Db, 1));
			Assert.Null(await svc.ConnectionAsync(2));
			var view = await svc.SettingsAsync(ctx);
			Assert.Equal(("https://moh.example/api", true, true), (view.Url, view.HasKey, view.IsEnabled));

			Assert.True((await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = "https://moh.example/v2", IsEnabled = true }, 7, DateTime.UtcNow)).Ok);   // key left empty: kept
			Assert.Equal("s3cret-key", (await svc.ConnectionAsync(1))!.Key);
			Assert.True((await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = "https://moh.example/v2", ClearKey = true, IsEnabled = false }, 7, DateTime.UtcNow)).Ok);
			Assert.Null(await svc.ConnectionAsync(1));
			Assert.False((await svc.SettingsAsync(ctx)).HasKey);

			// another server's keys cannot read it: no connection, never a garbled key sent
			Assert.True((await svc.SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = "https://moh.example/v2", Key = "k2", IsEnabled = true }, 7, DateTime.UtcNow)).Ok);
			Assert.Null(await new HospitalMohGatewayService(host.Db, access, null, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()).ConnectionAsync(1));
			Assert.Equal("The key cannot be stored on this server",
				(await new HospitalMohGatewayService(host.Db, access).SaveSettingsAsync(ctx, new HospitalMohSettingsInput { Url = "https://moh.example/v2", Key = "k3", IsEnabled = true }, 7, DateTime.UtcNow)).Error);
		}

		[Fact]
		public void The_settings_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalMohGatewayModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h229_moh_settings.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalMohSettings), "HospitalMohSettings");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalMohSettings), "HospitalMohSettings");
		}

		[Fact]
		public void The_table_matches_its_slice()
		{
			var model = HospitalSliceParity.Model(HospitalMohGatewayModel.Configure);
			var s = HospitalSliceParity.Slice("hospital_h225_moh_gateway.sql");
			HospitalSliceParity.NamesItsTable(model, s, typeof(HospitalMohSubmission), "HospitalMohSubmissions");
			HospitalSliceParity.ColumnsMatch(model, s, typeof(HospitalMohSubmission), "HospitalMohSubmissions");
			Assert.Contains("'" + HospitalMohKind.SickLeave + "'", s);
			Assert.Contains("'" + HospitalMohKind.Disease + "'", s);
		}
	}
}
