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

		// ---- recording, with the other side's consent ------------------------------------------------------------

		[Theory]
		[InlineData("Asked", "agree", false, "Recording")]     // only the OTHER side's yes starts it
		[InlineData("Asked", "agree", true, null)]             // the requester cannot agree for the other side
		[InlineData("Asked", "refuse", false, "Refused")]
		[InlineData("Asked", "cancel", true, "Refused")]       // withdrawn before the answer
		[InlineData("Recording", "save", true, "Saved")]
		[InlineData("Recording", "save", false, null)]         // only the browser that recorded hands the file in
		[InlineData("Refused", "agree", false, null)]          // a refusal is final
		[InlineData("Saved", "expire", false, "Deleted")]
		[InlineData("Saved", "agree", false, null)]
		public void A_recording_moves_only_along_its_states(string from, string action, bool byRequester, string? to)
			=> Assert.Equal(to, HospitalCallRecordingRules.Next(from, action, byRequester));

		[Fact]
		public void Nothing_is_taken_for_a_recording_that_is_not_running_and_pieces_come_in_order()
		{
			Assert.Equal("This recording is not running", HospitalCallRecordingRules.ChunkRefusal("Asked", 0, 0, 100, 0, true, true));
			Assert.Equal("A piece of the recording arrived out of order", HospitalCallRecordingRules.ChunkRefusal("Recording", 3, 5, 100, 300, false, false));
			Assert.Equal("The recording is not in a supported format", HospitalCallRecordingRules.ChunkRefusal("Recording", 0, 0, 100, 0, true, false));
			Assert.Equal("A piece of the recording is too large", HospitalCallRecordingRules.ChunkRefusal("Recording", 0, 0, HospitalCallRecordingRules.MaxChunkBytes + 1, 0, true, true));
			Assert.Equal("The recording is too long", HospitalCallRecordingRules.ChunkRefusal("Recording", 9, 9, 1000, HospitalCallRecordingRules.MaxBytes, false, false));
			Assert.Null(HospitalCallRecordingRules.ChunkRefusal("Recording", 0, 0, 100, 0, true, true));
			Assert.Null(HospitalCallRecordingRules.ChunkRefusal("Recording", 4, 4, 100, 400, false, false));   // later pieces are not WebM headers
		}

		[Fact]
		public void Only_a_webm_file_start_is_accepted()
		{
			Assert.True(HospitalCallRecordingRules.IsWebm(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01 }));
			Assert.False(HospitalCallRecordingRules.IsWebm(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
			Assert.False(HospitalCallRecordingRules.IsWebm(new byte[] { 0x1A, 0x45 }));
		}

		[Fact]
		public void Asking_needs_recording_on_and_one_of_my_groups_allowed()
		{
			var doctor = new[] { HospitalCallGroups.Doctors };
			var desk = new[] { HospitalCallGroups.Reception };
			Assert.False(HospitalCallRecordingRules.MayAsk(false, null, doctor));                  // off = nobody
			Assert.True(HospitalCallRecordingRules.MayAsk(true, null, doctor));                    // default: doctors and administration
			Assert.False(HospitalCallRecordingRules.MayAsk(true, null, desk));
			Assert.True(HospitalCallRecordingRules.MayAsk(true, "reception", desk));
			Assert.False(HospitalCallRecordingRules.MayAsk(true, HospitalCallRecordingRules.FormatGroups(Array.Empty<string>()), doctor));   // none ticked stays none
			Assert.Equal("doctors,nursing", HospitalCallRecordingRules.FormatGroups(new[] { "doctors", "ghost", "nursing", "doctors" }));
		}

		[Fact]
		public void The_picture_is_recorded_only_when_allowed_and_the_call_is_a_video_call()
		{
			Assert.Equal("video", HospitalCallRecordingRules.KindFor(HospitalCallRecordingMode.Both, true));
			Assert.Equal("voice", HospitalCallRecordingRules.KindFor(HospitalCallRecordingMode.Both, false));
			Assert.Equal("voice", HospitalCallRecordingRules.KindFor(HospitalCallRecordingMode.Voice, true));
		}

		[Fact]
		public void Retention_sets_the_deletion_day_or_none()
		{
			var saved = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
			Assert.Equal(saved.AddDays(90), HospitalCallRecordingRules.DeleteAfter(saved, 90));
			Assert.Null(HospitalCallRecordingRules.DeleteAfter(saved, 0));
			Assert.Equal("CALL-000184-7.webm", HospitalCallRecordingRules.FileName(184, 7));
		}

		[Fact]
		public void Only_an_administrator_reads_a_recording_through_the_platform()
		{
			var r = new HospitalCallDocumentOwnerResolver(null!);
			Assert.Equal(HospitalActions.CallRecordings, r.ModuleActionFor(CrossBuy.BL.Platform.DocumentAction.View));
			Assert.Equal(HospitalActions.CallRecordings, r.ModuleActionFor(CrossBuy.BL.Platform.DocumentAction.Download));
			Assert.Equal(HospitalActions.CallRecordings, r.ModuleActionFor(CrossBuy.BL.Platform.DocumentAction.Delete));
			Assert.Equal(HospitalActions.Read, r.ModuleActionFor(CrossBuy.BL.Platform.DocumentAction.Upload));
		}

		[Fact]
		public async Task With_no_hospital_role_configured_everything_is_open_except_other_peoples_recordings()
		{
			var access = new HospitalAccessService(NoRoles.Create(), Microsoft.Extensions.Logging.Abstractions.NullLogger<HospitalAccessService>.Instance);
			var ctx = new CrossBuy.Models.Platform.BusinessContext { CompanyId = 1, EmployeeId = 30, UserId = "u30" };
			Assert.True(await access.CanAsync(ctx, HospitalActions.Manage, null));            // bootstrap-open, as before
			Assert.False(await access.CanAsync(ctx, HospitalActions.CallRecordings, null));   // never by default
			Assert.Contains(HospitalActions.CallRecordings, HospitalActions.All);
		}

		/// A role directory with nothing configured, built at run time so it fits whichever members the interface has.
		public class NoRoles : System.Reflection.DispatchProxy
		{
			public static CrossBuy.BL.Platform.IPlatformRoleDirectory Create() => Create<CrossBuy.BL.Platform.IPlatformRoleDirectory, NoRoles>();
			protected override object? Invoke(System.Reflection.MethodInfo? m, object?[]? args) => m!.Name switch
			{
				"RolesAsync" => Task.FromResult<IReadOnlyList<CrossBuy.BL.Platform.RoleGrant>>(Array.Empty<CrossBuy.BL.Platform.RoleGrant>()),
				"AnyConfiguredAsync" => Task.FromResult(false),
				"EmployeeIdsForRoleAsync" => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>()),
				_ => throw new NotSupportedException(m.Name),
			};
		}

		// ---- group calls ------------------------------------------------------------------------------------------

		[Fact]
		public void A_group_is_three_to_four_people_until_a_media_server_exists()
		{
			Assert.Equal(4, HospitalCallGroupRules.Cap(8));    // settings may say 8; a mesh carries four
			Assert.Equal(3, HospitalCallGroupRules.Cap(2));
			Assert.Equal(4, HospitalCallGroupRules.Cap(4));
			Assert.Equal(1, HospitalCallGroupRules.Room(4, 2, 1));   // two in it and one ringing: room for one more
			Assert.Equal(0, HospitalCallGroupRules.Room(4, 3, 2));
		}

		[Theory]
		[InlineData(1, 0, true)]     // only one person left
		[InlineData(1, 1, false)]    // someone is still being rung
		[InlineData(2, 0, false)]
		[InlineData(0, 0, true)]
		public void A_group_call_ends_when_one_person_or_fewer_is_left_and_nobody_is_ringing(int joined, int ringing, bool ends)
			=> Assert.Equal(ends, HospitalCallGroupRules.Ends(joined, ringing));

		[Theory]
		[InlineData(HospitalCallParticipantState.Invited, "join", HospitalCallParticipantState.Joined)]
		[InlineData(HospitalCallParticipantState.Invited, "decline", HospitalCallParticipantState.Declined)]
		[InlineData(HospitalCallParticipantState.Invited, "timeout", HospitalCallParticipantState.Missed)]
		[InlineData(HospitalCallParticipantState.Joined, "leave", HospitalCallParticipantState.Left)]
		[InlineData(HospitalCallParticipantState.Joined, "remove", HospitalCallParticipantState.Removed)]
		[InlineData(HospitalCallParticipantState.Left, "invite", HospitalCallParticipantState.Invited)]      // invited again
		[InlineData(HospitalCallParticipantState.Declined, "invite", HospitalCallParticipantState.Invited)]
		[InlineData(HospitalCallParticipantState.Joined, "invite", null)]                                    // already in it
		[InlineData(HospitalCallParticipantState.Removed, "join", null)]                                     // removed cannot rejoin by themselves
		[InlineData(HospitalCallParticipantState.Declined, "join", null)]
		public void A_participant_moves_only_along_their_states(string from, string action, string? to)
			=> Assert.Equal(to, HospitalCallGroupRules.Next(from, action));

		[Fact]
		public void A_group_recording_starts_only_when_everyone_else_agreed()
		{
			var inCall = new[] { 1, 2, 3 };   // 1 asked
			Assert.False(HospitalCallGroupRules.AllAgreed(inCall, 1, new[] { 2 }));
			Assert.True(HospitalCallGroupRules.AllAgreed(inCall, 1, new[] { 2, 3 }));
			Assert.True(HospitalCallGroupRules.AllAgreed(new[] { 1, 2 }, 1, new[] { 2 }));
		}

		[Fact]
		public void Length_reads_as_minutes_and_seconds()
		{
			Assert.Equal("06:14", HospitalCallRules.Duration(374));
			Assert.Equal("", HospitalCallRules.Duration(null));
		}
	}
}
