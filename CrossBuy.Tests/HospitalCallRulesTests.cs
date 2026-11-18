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
		public void A_recording_asked_from_a_phone_is_recorded_by_the_browser_named_for_it()
		{
			var own = new HospitalCallRecording { RequestedByEmployeeId = 7 };
			Assert.Equal(7, HospitalCallRecordingRules.RecorderOf(own));   // the requester's browser records, as always
			var fromPhone = new HospitalCallRecording { RequestedByEmployeeId = 7, RecorderEmployeeId = 9 };
			Assert.Equal(9, HospitalCallRecordingRules.RecorderOf(fromPhone));   // the browser at the other end sends the file
		}

		[Fact]
		public void A_screen_shown_in_a_voice_call_is_recorded_with_its_picture()
		{
			Assert.Equal("video", HospitalCallRecordingRules.KindFor(HospitalCallRecordingMode.Both, false, pictureNow: true));
			Assert.Equal("voice", HospitalCallRecordingRules.KindFor(HospitalCallRecordingMode.Voice, false, pictureNow: true));   // voice-only settings win
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

		// ---- the internal media server, and the named reviewers ---------------------------------------------------

		[Fact]
		public void A_room_token_is_a_signed_livekit_grant_for_one_room()
		{
			var now = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
			var tok = HospitalCallSfu.Token("devkey", "secret", "1044", "Sara", "hcall-1-7", now, TimeSpan.FromHours(6));
			var parts = tok.Split('.');
			Assert.Equal(3, parts.Length);
			string Pad(string x) { x = x.Replace('-', '+').Replace('_', '/'); return x + new string('=', (4 - x.Length % 4) % 4); }
			var body = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(Pad(parts[1]))).RootElement;
			Assert.Equal("devkey", body.GetProperty("iss").GetString());
			Assert.Equal("1044", body.GetProperty("sub").GetString());
			Assert.Equal(6 * 3600, body.GetProperty("exp").GetInt64() - body.GetProperty("nbf").GetInt64());
			var video = body.GetProperty("video");
			Assert.Equal("hcall-1-7", video.GetProperty("room").GetString());
			Assert.True(video.GetProperty("roomJoin").GetBoolean());
			Assert.False(video.TryGetProperty("roomAdmin", out _));
			using var h = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
			var sig = Convert.ToBase64String(h.ComputeHash(Encoding.UTF8.GetBytes(parts[0] + "." + parts[1]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
			Assert.Equal(sig, parts[2]);
			var admin = HospitalCallSfu.Token("devkey", "secret", "srv", null, "hcall-1-7", now, TimeSpan.FromMinutes(2), admin: true);
			var av = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(Pad(admin.Split('.')[1]))).RootElement.GetProperty("video");
			Assert.True(av.GetProperty("roomAdmin").GetBoolean());
			Assert.False(av.TryGetProperty("roomJoin", out _));   // an admin token does not join
		}

		[Fact]
		public void The_media_server_raises_the_cap_only_when_it_is_fully_configured()
		{
			Assert.Equal(4, HospitalCallGroupRules.Cap(12));
			Assert.Equal(12, HospitalCallGroupRules.Cap(12, sfu: true));
			Assert.Equal(12, HospitalCallGroupRules.Cap(40, sfu: true));
			Assert.True(HospitalCallGroupRules.SfuReady("wss://lk.local:7880", "k", "s"));
			Assert.False(HospitalCallGroupRules.SfuReady("https://lk.local", "k", "s"));
			Assert.False(HospitalCallGroupRules.SfuReady("wss://lk.local", "k", null));
			Assert.Equal("https://lk.local:7880", HospitalCallSfu.HttpBase("wss://lk.local:7880"));
			Assert.Equal("http://localhost:7880", HospitalCallSfu.HttpBase("ws://localhost:7880"));
			Assert.Equal("hcall-3-41", HospitalCallSfu.Room(3, 41));
		}

		[Fact]
		public void Reviewers_are_an_explicit_list_of_people()
		{
			Assert.Equal(new[] { 5, 17 }, HospitalCallRecordingRules.ParseIds("5, 17,x,-3,5"));
			Assert.Empty(HospitalCallRecordingRules.ParseIds(null));
			Assert.Null(HospitalCallRecordingRules.FormatIds(Array.Empty<int>()));
			Assert.Equal("5,17", HospitalCallRecordingRules.FormatIds(new[] { 5, 17, 5, 0 }));
		}

		[Fact]
		public void Length_reads_as_minutes_and_seconds()
		{
			Assert.Equal("06:14", HospitalCallRules.Duration(374));
			Assert.Equal("", HospitalCallRules.Duration(null));
		}
	}
}
