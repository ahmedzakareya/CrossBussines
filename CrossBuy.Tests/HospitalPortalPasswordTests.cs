using CrossBuy.BL.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// ==========================================================================================
	// HOSPITAL H46 — the patient portal's "forgot password", with no database: a link's token is random and only its
	// SHA-256 is kept, a link works once and only until it expires, the two entries must agree, and the slice adds only
	// (nullable columns, a new table) and keeps no token.
	// ==========================================================================================
	public class HospitalPortalPasswordTests
	{
		[Fact]
		public void A_token_is_random_url_safe_and_only_its_hash_is_kept()
		{
			var a = HospitalPortalPasswordRules.NewToken(); var b = HospitalPortalPasswordRules.NewToken();
			Assert.NotEqual(a, b);
			Assert.True(a.Length >= 40);
			Assert.DoesNotContain('+', a); Assert.DoesNotContain('/', a); Assert.DoesNotContain('=', a);
			var h = HospitalPortalPasswordRules.Hash(a);
			Assert.Equal(64, h.Length);
			Assert.Equal(h, HospitalPortalPasswordRules.Hash(a));
			Assert.NotEqual(h, HospitalPortalPasswordRules.Hash(b));
			Assert.DoesNotContain(a, h);
		}

		[Fact]
		public void A_link_works_once_and_only_until_it_expires()
		{
			var now = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
			Assert.True(HospitalPortalPasswordRules.Usable(null, now.AddMinutes(1), now));
			Assert.False(HospitalPortalPasswordRules.Usable(null, now, now));                  // at the minute it ends, it has ended
			Assert.False(HospitalPortalPasswordRules.Usable(now.AddMinutes(-5), now.AddMinutes(20), now));   // used
			Assert.Equal(30, HospitalPortalPasswordRules.LinkMinutes);
			Assert.Equal(3, HospitalPortalPasswordRules.PerHour);
		}

		[Fact]
		public void The_two_entries_must_agree()
		{
			Assert.Null(HospitalPortalPasswordRules.EntryRefusal("Ptl#2026b", "Ptl#2026b"));
			Assert.Equal("The two passwords are not the same", HospitalPortalPasswordRules.EntryRefusal("Ptl#2026b", "Ptl#2026c"));
			Assert.Equal("Choose a password", HospitalPortalPasswordRules.EntryRefusal("", ""));
		}

		[Theory]
		[InlineData("testcrossbuy@gmail.com", true)]
		[InlineData("a.b@hospital.com.kw", true)]
		[InlineData("pt.ahmed", false)]
		[InlineData("no@dot", false)]
		[InlineData("two@@signs.com", false)]
		[InlineData("", false)]
		public void An_email_looks_like_one(string s, bool ok) => Assert.Equal(ok, HospitalPortalPasswordRules.LooksLikeEmail(s));

		[Fact]
		public void The_slice_only_adds_and_keeps_no_token()
		{
			var s = HospitalSliceParity.Slice("hospital_h46_portal_password.sql");
			Assert.Contains("ALTER TABLE dbo.HospitalPortalAccounts ADD Email nvarchar(256) NULL;", s);
			Assert.Contains("ALTER TABLE dbo.HospitalPortalAccounts ADD PasswordChangedAt datetime2 NULL;", s);
			Assert.Contains("TokenHash   nvarchar(64)", s);
			Assert.DoesNotContain(" Token ", s);
			Assert.DoesNotContain("DROP ", s, StringComparison.OrdinalIgnoreCase);
			Assert.Contains("CREATE UNIQUE INDEX IX_HospitalPortalResets_TokenHash", s);
		}
	}
}
