using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H251 — portal proxy access and the payment link. What must never happen: an ended proxy still giving
	// access; a proxy scope outside the two; a payment link over plain http, or without the bill's reference or amount;
	// an amount written with the wrong decimals or a reference not escaped; a link for a bill with nothing due; the slice
	// missing a column the model reads.
	// ==========================================================================================
	public class HospitalPortalProxyTests
	{
		private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

		[Fact]
		public void An_ended_proxy_gives_nothing()
		{
			Assert.True(HospitalPortalProxyRules.Active(new HospitalPortalFamilyLink { ExpiresAt = null }, Now));
			Assert.True(HospitalPortalProxyRules.Active(new HospitalPortalFamilyLink { ExpiresAt = Now.AddDays(1) }, Now));
			Assert.False(HospitalPortalProxyRules.Active(new HospitalPortalFamilyLink { ExpiresAt = Now }, Now));
			Assert.Null(HospitalPortalProxyRules.Refusal(HospitalPortalProxyScope.Booking, Now.AddDays(30), Now));
			Assert.NotNull(HospitalPortalProxyRules.Refusal("Everything", null, Now));
			Assert.NotNull(HospitalPortalProxyRules.Refusal(HospitalPortalProxyScope.Full, Now.AddMinutes(-1), Now));
			Assert.Equal(HospitalPortalProxyScope.Full, new HospitalPortalPerson(1, "a", "a", "m", null, "Self").Scope);
		}

		[Theory]
		[InlineData(null, null)]
		[InlineData("https://pay.example.com/c?ref={ref}&amount={amount}&cur={currency}", null)]
		[InlineData("http://pay.example.com/c?ref={ref}&amount={amount}", "The payment link is the provider's https address")]
		[InlineData("https://pay.example.com/c?amount={amount}", "The payment link needs {ref} and {amount}")]
		public void A_payment_link_is_https_with_the_reference_and_amount(string? template, string? refusal) =>
			Assert.Equal(refusal, HospitalPortalPayLink.Refusal(template));

		[Fact]
		public void A_payment_link_is_built_per_bill()
		{
			var t = "https://pay.example.com/c?ref={ref}&amount={amount}&cur={currency}";
			Assert.Equal("https://pay.example.com/c?ref=BILL%2000012&amount=12.500&cur=KWD", HospitalPortalPayLink.Build(t, "BILL 00012", 12.5m, "KWD", 3));
			Assert.Null(HospitalPortalPayLink.Build(t, "B1", 0m, "KWD", 3));
			Assert.Null(HospitalPortalPayLink.Build(null, "B1", 10m, "KWD", 3));
		}

		[Fact]
		public void The_slice_adds_what_the_model_reads()
		{
			var s = HospitalSliceParity.Slice("hospital_h251_portal_proxy_payment.sql");
			foreach (var c in new[] { "Scope nvarchar(8)", "ExpiresAt datetime2", "PayLinkTemplate nvarchar(300)" }) Assert.Contains(c, s);
		}
	}
}
