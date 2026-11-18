using System.Security.Cryptography;
using System.Text;
using CrossBuy.BL;
using CrossBuy.BL.Hospital;
using CrossBuy.Models.Context.Hospital;
using Xunit;

namespace CrossBuy.Tests
{
	// H176 — internal calls: who may call whom, the relay password, the call's states. No database.
	public class HospitalCallRulesTests
	{
		[Fact]
		public void A_person_with_no_hospital_role_is_administration_and_a_practitioner_is_a_doctor()
		{
			Assert.Equal(new[] { HospitalCallGroups.Administration }, HospitalCallGroups.Of(Array.Empty<string>(), false));
			Assert.Equal(new[] { HospitalCallGroups.Doctors }, HospitalCallGroups.Of(Array.Empty<string>(), true));
			var lab = HospitalCallGroups.Of(new[] { HospitalRoles.HospitalRadiologist, HospitalRoles.HospitalReception }, false);
			Assert.Contains(HospitalCallGroups.Diagnostics, lab);
			Assert.Contains(HospitalCallGroups.Reception, lab);
			Assert.DoesNotContain(HospitalCallGroups.Administration, lab);
		}

		[Fact]
		public void The_published_default_keeps_the_desk_away_from_the_benches_but_lets_doctors_reach_everyone()
		{
			var p = HospitalCallRules.ParsePolicy(null);
			Assert.False(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Reception }, new[] { HospitalCallGroups.Diagnostics }));
			Assert.False(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Reception }, new[] { HospitalCallGroups.Doctors }));
			Assert.True(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Reception }, new[] { HospitalCallGroups.Nursing }));
			foreach (var g in HospitalCallGroups.All) Assert.True(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Doctors }, new[] { g }));
		}

		[Fact]
		public void Any_group_of_the_caller_reaching_any_group_of_the_callee_is_enough()
		{
			var p = HospitalCallRules.ParsePolicy("reception>nursing");
			Assert.True(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Diagnostics, HospitalCallGroups.Reception }, new[] { HospitalCallGroups.Doctors, HospitalCallGroups.Nursing }));
			Assert.False(HospitalCallRules.Allowed(p, new[] { HospitalCallGroups.Diagnostics }, new[] { HospitalCallGroups.Nursing }));
		}

		[Fact]
		public void A_stored_policy_round_trips_and_drops_unknown_groups()
		{
			var stored = HospitalCallRules.FormatPolicy(new[] { ("doctors", "nursing"), ("nursing", "doctors"), ("ghost", "doctors"), ("doctors", "nursing") });
			Assert.Equal("doctors>nursing,nursing>doctors", stored);
			var p = HospitalCallRules.ParsePolicy(stored + ",x>y,admin>");
			Assert.Equal(2, p.Count);
			// an empty stored matrix means nobody may call — it is not the default
			Assert.Empty(HospitalCallRules.ParsePolicy(","));
		}

		[Fact]
		public void The_relay_password_is_coturn_rest_hmac_of_expiry_and_employee()
		{
			var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
			var (user, cred) = HospitalCallRules.TurnCredential("s3cret", 42, now, TimeSpan.FromHours(12));
			long exp = new DateTimeOffset(now.AddHours(12)).ToUnixTimeSeconds();
			Assert.Equal(exp + ":42", user);
			using var h = new HMACSHA1(Encoding.UTF8.GetBytes("s3cret"));
			Assert.Equal(Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(user))), cred);
			Assert.NotEqual(cred, HospitalCallRules.TurnCredential("other", 42, now, TimeSpan.FromHours(12)).Credential);
		}

		[Fact]
		public void Only_stun_and_turn_addresses_are_kept()
		{
			var u = HospitalCallRules.ParseUrls("turn:10.0.5.20:3478, http://evil, stun:10.0.5.20:3478;turns:t.local:5349 turn:10.0.5.20:3478");
			Assert.Equal(new[] { "turn:10.0.5.20:3478", "stun:10.0.5.20:3478", "turns:t.local:5349" }, u);
			Assert.Empty(HospitalCallRules.ParseUrls(null));
		}

		[Theory]
		[InlineData(HospitalCallStatus.Ringing, "answer", false, HospitalCallStatus.Answered)]
		[InlineData(HospitalCallStatus.Ringing, "answer", true, null)]          // the caller cannot answer their own call
		[InlineData(HospitalCallStatus.Ringing, "decline", false, HospitalCallStatus.Declined)]
		[InlineData(HospitalCallStatus.Ringing, "cancel", true, HospitalCallStatus.Cancelled)]
		[InlineData(HospitalCallStatus.Ringing, "cancel", false, null)]
		[InlineData(HospitalCallStatus.Ringing, "timeout", true, HospitalCallStatus.Missed)]
		[InlineData(HospitalCallStatus.Ringing, "drop", false, HospitalCallStatus.Missed)]   // the callee's tab closed while ringing
		[InlineData(HospitalCallStatus.Ringing, "drop", true, HospitalCallStatus.Cancelled)]
		[InlineData(HospitalCallStatus.Answered, "hangup", false, HospitalCallStatus.Ended)]
		[InlineData(HospitalCallStatus.Answered, "decline", false, null)]
		[InlineData(HospitalCallStatus.Ended, "hangup", true, null)]            // an ended call stays ended
		public void A_call_moves_only_along_its_states(string from, string action, bool byCaller, string? to)
			=> Assert.Equal(to, HospitalCallRules.Next(from, action, byCaller));

		[Fact]
		public void Length_reads_as_minutes_and_seconds()
		{
			Assert.Equal("06:14", HospitalCallRules.Duration(374));
			Assert.Equal("", HospitalCallRules.Duration(null));
		}
	}
}
