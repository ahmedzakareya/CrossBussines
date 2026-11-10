using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H47 — the desk's patient portal accounts, with no database: the state the desk sees (disabled first,
	// then locked, then where the sign-in stands, then whether the file is matched), and the user names it may give.
	// ==========================================================================================
	public class HospitalPortalAccountsAdminTests
	{
		private static readonly DateTime Now = new(2026, 11, 8, 10, 0, 0, DateTimeKind.Utc);
		private const string V = HospitalPortalAccountStatus.Verified, U = HospitalPortalAccountStatus.Unverified, D = HospitalPortalAccountStatus.Disabled;

		[Fact]
		public void Disabled_and_locked_come_before_everything()
		{
			Assert.Equal(HospitalPortalAccountState.Disabled, HospitalPortalAccountRules.State(D, true, true, Now.AddMinutes(5), null, Now));
			Assert.Equal(HospitalPortalAccountState.Locked, HospitalPortalAccountRules.State(V, true, true, Now.AddMinutes(5), null, Now));
			Assert.Equal(HospitalPortalAccountState.Active, HospitalPortalAccountRules.State(V, true, true, Now.AddMinutes(-1), null, Now));   // a lock that has run out
		}

		[Fact]
		public void The_sign_in_is_followed_from_none_to_invited_to_active()
		{
			Assert.Equal(HospitalPortalAccountState.NoSignIn, HospitalPortalAccountRules.State(V, false, false, null, null, Now));
			Assert.Equal(HospitalPortalAccountState.Invited, HospitalPortalAccountRules.State(V, true, false, null, Now.AddHours(70), Now));
			Assert.Equal(HospitalPortalAccountState.InviteExpired, HospitalPortalAccountRules.State(V, true, false, null, Now.AddMinutes(-1), Now));
			Assert.Equal(HospitalPortalAccountState.InviteExpired, HospitalPortalAccountRules.State(V, true, false, null, null, Now));
			Assert.Equal(HospitalPortalAccountState.Active, HospitalPortalAccountRules.State(V, true, true, null, null, Now));
			Assert.Equal(HospitalPortalAccountState.Unverified, HospitalPortalAccountRules.State(U, true, true, null, null, Now));
		}

		[Theory]
		[InlineData("pt.sara", null)]
		[InlineData("pt_sara-2", null)]
		[InlineData("abc", "A user name has at least 4 characters")]
		[InlineData("  ", "A user name has at least 4 characters")]
		[InlineData("pt sara", "A user name is letters, digits, dots and dashes")]
		[InlineData("سارة.مريض", "A user name is letters, digits, dots and dashes")]
		public void A_user_name_is_plain(string name, string? why) => Assert.Equal(why, HospitalPortalAccountRules.UserNameRefusal(name));

		[Fact]
		public void The_invitation_outlives_a_reset_and_the_lock_is_five_tries_fifteen_minutes()
		{
			Assert.Equal(72, HospitalPortalPasswordRules.InviteHours);
			Assert.True(HospitalPortalPasswordRules.InviteHours * 60 > HospitalPortalPasswordRules.LinkMinutes);
			Assert.Equal(5, HospitalPortalPasswordRules.MaxFailures);
			Assert.Equal(15, HospitalPortalPasswordRules.LockMinutes);
		}
	}
}
