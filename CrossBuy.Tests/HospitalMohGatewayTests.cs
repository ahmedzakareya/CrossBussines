using CrossBuy.BL.Hospital;
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

		[Fact]
		public void Without_a_gateway_address_the_hospital_is_not_connected()
		{
			var none = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
			Assert.False(new HospitalMohGatewayService(null!, null!, null, none).Connected);
			Assert.False(new HospitalMohGatewayService(null!, null!, null, null).Connected);
			Assert.True(new HospitalMohGatewayService(null!, null!, null, new Cfg("https://moh.example/api")).Connected);
			Assert.False(new HospitalMohGatewayService(null!, null!, null, new Cfg("  ")).Connected);
		}

		private sealed class Cfg : Microsoft.Extensions.Configuration.IConfiguration
		{
			private readonly string _url; public Cfg(string url) => _url = url;
			public string? this[string key] { get => key == "Hospital:MohGateway:Url" ? _url : null; set { } }
			public IEnumerable<Microsoft.Extensions.Configuration.IConfigurationSection> GetChildren() => Array.Empty<Microsoft.Extensions.Configuration.IConfigurationSection>();
			public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => throw new NotSupportedException();
			public Microsoft.Extensions.Configuration.IConfigurationSection GetSection(string key) => throw new NotSupportedException();
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
